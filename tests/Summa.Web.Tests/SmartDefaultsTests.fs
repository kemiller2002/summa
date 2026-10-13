/// Choosing a customer on a new invoice proposes its billing details, its
/// engagement, its terms and its rate; nothing typed is replaced and an
/// opened draft keeps what it holds (WI-0044, v0.4 §5, SUM4-005).
module Summa.Web.Tests.SmartDefaultsTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Sources
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private address (hash: string) : Limen.Routing.PageLocation =
    { Origin = "https://summa.example"; Path = "/app/"; Query = ""; Hash = hash }

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | _ -> failwith $"no scalar view value {name}"

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows |> List.map Map.ofList
    | _ -> failwith $"no list view value {name}"

/// ABC Corp (CUST-0001, net 30) with a customer rate of 175.00, and the
/// engagements named, on the new-invoice page.
let private onNewInvoice (engagements: string list) =
    let started =
        run [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"local development"}"""); Loaded None ] initial |> fst

    let customer =
        [ LocationChanged(address "#/customers")
          CustomerNameChanged "ABC Corp"
          CustomerAddressChanged "1 Main Street"
          CustomerEmailChanged "ap@abc.example"
          CustomerTermsChanged "30"
          CustomerAdded ]

    let engagementMsgs =
        engagements
        |> List.collect (fun name -> [ LocationChanged(address "#/engagements"); EngagementCustomerChanged "CUST-0001"; EngagementNameChanged name; EngagementFeeChanged ""; EngagementAdded ])

    let model = run (customer @ engagementMsgs) started |> fst
    let b = model.Books.Value
    let rated = { model with Books = Some { b with Books = { b.Books with Rates = { b.Books.Rates with Rates = [ ForCustomer "CUST-0001", usd 17500L ] } } } }
    run [ LocationChanged(address "#/invoices/new") ] rated |> fst

[<Fact>]
let ``SUM4-005: choosing a customer shows its billing details and fills the rate only on lines left without one`` () =
    let model = onNewInvoice []
    let first = model.Draft.Lines.Head.Key
    let typed, _ = run [ LineAdded ] model
    let second = (typed.Draft.Lines |> List.last).Key
    let chosen = run [ LineRateChanged(second, "120.00"); DraftCustomerChanged "CUST-0001" ] typed |> fst

    Assert.Equal("175.00", (chosen.Draft.Lines |> List.find (fun l -> l.Key = first)).Rate)
    Assert.Equal("120.00", (chosen.Draft.Lines |> List.find (fun l -> l.Key = second)).Rate)
    Assert.Equal("ABC Corp · 1 Main Street · ap@abc.example", value "draftBillTo" chosen)
    Assert.StartsWith("Rate ", value "draftRateNote" chosen)
    Assert.Contains("from the customer's rate", value "draftRateNote" chosen)
    Assert.False(String.IsNullOrEmpty(value "draftRevenueAccount" chosen))
    Assert.Equal("False", value "hasDraftEngagements" chosen)

[<Fact>]
let ``SUM4-005: the customer's only engagement is proposed; with several, the person chooses`` () =
    let one = onNewInvoice [ "Retainer" ] |> run [ DraftCustomerChanged "CUST-0001" ] |> fst
    Assert.False(String.IsNullOrEmpty one.Draft.EngagementId)
    Assert.Equal("True", value "hasDraftEngagements" one)
    Assert.True(([ "Retainer" ]: string list) = (items "draftEngagementOptions" one |> List.map (fun r -> match r["label"] with Text t -> t | _ -> "")))

    let several = onNewInvoice [ "Retainer"; "Audit" ] |> run [ DraftCustomerChanged "CUST-0001" ] |> fst
    Assert.Equal("", several.Draft.EngagementId)
    let audit = items "draftEngagementOptions" several |> List.pick (fun r -> match r["label"], r["value"] with Text "Audit", Text id -> Some id | _ -> None)
    let picked = run [ DraftEngagementChosen audit ] several |> fst
    Assert.Equal(audit, picked.Draft.EngagementId)
    Assert.Equal(audit, value "draftEngagement" picked)
    // Choosing none again keeps none.
    Assert.Equal("", (run [ DraftEngagementChosen "" ] picked |> fst).Draft.EngagementId)

[<Fact>]
let ``SUM4-005: an opened draft keeps what it holds when its customer changes`` () =
    let model = onNewInvoice [] |> run [ DraftCustomerChanged "CUST-0001" ] |> fst
    let key = model.Draft.Lines.Head.Key
    let saved = run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "150.00"); DraftSubmitted ] model |> fst
    let opened = run [ LocationChanged(address "#/drafts/D-0001") ] saved |> fst
    Assert.True opened.Draft.DraftId.IsSome
    // A line added to the opened draft stays without a rate: defaults are for new invoices only.
    let changed = run [ LineAdded; DraftCustomerChanged "CUST-0001" ] opened |> fst
    Assert.Equal("150.00", changed.Draft.Lines.Head.Rate)
    Assert.Equal("", (changed.Draft.Lines |> List.last).Rate)

[<Fact>]
let ``SUM4-005: with no rate for the customer the editor says so and invents none`` () =
    let model = onNewInvoice []
    let b = model.Books.Value
    let unrated = { model with Books = Some { b with Books = { b.Books with Rates = noRates } } }
    let chosen = run [ DraftCustomerChanged "CUST-0001" ] unrated |> fst
    Assert.Equal("", chosen.Draft.Lines.Head.Rate)
    Assert.Equal("No rate is set for this customer: enter each line's rate.", value "draftRateNote" chosen)
