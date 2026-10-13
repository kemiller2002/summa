/// The default receivables view puts what needs action first, each with
/// its reason (WI-0044, v0.4 §10, SUM4-010).
module Summa.Web.Tests.ReceivablesAttentionTests

open System
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private on (day: int) = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero).AddDays(float day); Actor = "local-person" }

let private runOn (day: int) (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update (on day) msg m in next, effects @ more) (model, [])

let private address hash : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"; Path = "/app/"; Query = ""; Hash = hash }

let private go hash = LocationChanged(address hash)

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows |> List.map Map.ofList
    | _ -> failwith $"no list view value {name}"

let private text (name: string) (row: Map<string, Scalar>) =
    match row[name] with
    | Text t -> t
    | other -> failwith $"%A{other}"

/// Four customers with different terms, each owing one invoice issued on
/// day 0: Acme net 30 (100.00), Beta net 10 (100.00), Cara net 60
/// (1,000.00), Dana net 60 (100.00). Their numbers follow that order.
let private owing () =
    let started =
        runOn 0 [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}"""); Loaded None ] initial |> fst

    let customer (name, terms) =
        [ go "#/customers"; CustomerNameChanged name; CustomerAddressChanged "1 Main Street"; CustomerTermsChanged terms; CustomerAdded ]

    let invoice (customerId: string) (rate: string) (model: Model) =
        let opened = runOn 0 [ go $"#/invoices/new?customer={customerId}" ] model |> fst
        let key = opened.Draft.Lines.Head.Key
        runOn 0 [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, rate); DraftSubmitted; DraftIssued ] opened |> fst

    started
    |> runOn 0 ([ "Acme", "30"; "Beta", "10"; "Cara", "60"; "Dana", "60" ] |> List.collect customer)
    |> fst
    |> invoice "CUST-0001" "100"
    |> invoice "CUST-0002" "100"
    |> invoice "CUST-0003" "1000"
    |> invoice "CUST-0004" "100"

[<Fact>]
let ``SUM4-010: the invoices list opens with what needs action first, saying why`` () =
    let model = owing ()
    Assert.Equal(4, model.Books.Value.Books.Invoices.Count)

    // Day 25: Beta is 15 days overdue, Acme is due in 5 days, Cara owes 76% of everything, Dana needs nothing.
    let listed = runOn 25 [ go "#/invoices" ] model |> fst |> items "invoices"

    Assert.Equal<string list>([ "Beta"; "Acme"; "Cara"; "Dana" ], listed |> List.map (text "customer"))
    Assert.Equal<string list>(
        [ "Overdue 15 days"; "Due in 5 days"; "Large balance: 76% of everything owed"; "" ],
        listed |> List.map (text "attention")
    )

[<Fact>]
let ``SUM4-010: settled invoices follow what is still open, and other orders are a choice away`` () =
    let model = owing ()
    let paid = runOn 0 [ go "#/invoices/INV-0002?tab=payments"; PaymentAmountChanged "100"; PaymentRecorded ] model |> fst
    let listed = runOn 25 [ go "#/invoices" ] paid |> fst |> items "invoices"
    // Beta is paid: it no longer needs attention and comes last.
    Assert.Equal<string list>([ "Acme"; "Cara"; "Dana"; "Beta" ], listed |> List.map (text "customer"))
    Assert.Equal("", listed |> List.last |> text "attention")

    let byNumber = runOn 25 [ go "#/invoices?sort=number" ] paid |> fst |> items "invoices"
    Assert.Equal<string list>([ "Acme"; "Beta"; "Cara"; "Dana" ], byNumber |> List.map (text "customer"))
