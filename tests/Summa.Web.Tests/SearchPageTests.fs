/// One search over the books and the command palette, at #/search (WI-0044,
/// v0.4 §18-19, SUM4-018, SUM4-019).
module Summa.Web.Tests.SearchPageTests

open System
open Xunit
open Limen.Routing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private address hash : PageLocation =
    { Origin = "https://summa.example"; Path = "/app/"; Query = ""; Hash = hash }

let private go hash = LocationChanged(address hash)

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | _ -> failwith $"no scalar view value {name}"

let private items (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows |> List.map (List.map (fun (k, v) -> k, (match v with Text t -> t | Flag f -> string f | other -> $"%A{other}")) >> Map.ofList)
    | _ -> failwith $"no list view value {name}"

/// ABC Corp owing INV-0001 (1,500.00 USD), on the home page.
let private books () =
    let started =
        run [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}"""); Loaded None ] initial |> fst

    let withCustomer =
        run [ go "#/customers"; CustomerNameChanged "ABC Corp"; CustomerAddressChanged "1 Main Street"; CustomerTermsChanged "30"; CustomerAdded; go "#/invoices/new?customer=CUST-0001" ] started
        |> fst

    let key = withCustomer.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "150"); DraftSubmitted; DraftIssued; go "#/" ] withCustomer |> fst

[<Fact>]
let ``SUM4-018: typing in the header opens the search page from anywhere, then refines it in place`` () =
    let model = books ()
    let opened, effects = run [ GlobalSearchChanged "abc" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Push "/search?q=abc") ], effects)
    Assert.Equal("True", value "onSearch" opened)
    Assert.Equal("abc", value "globalSearch" opened)

    let refined, more = run [ GlobalSearchChanged "abc corp" ] opened
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/search?q=abc%20corp") ], more)
    Assert.Equal("ABC Corp", (items "searchResults" refined |> List.head)["title"])
    // Elsewhere the header's field is empty.
    Assert.Equal("", value "globalSearch" model)

[<Fact>]
let ``SUM4-018: each result says what it is and what matched, and links to it`` () =
    let model = run [ go "#/search?q=1500" ] (books ()) |> fst
    let results = items "searchResults" model
    let invoice = results |> List.find (fun r -> r["kind"] = "Invoice")
    Assert.Equal("#/invoices/INV-0001", invoice["href"])
    Assert.Equal("total: 1,500.00 USD", invoice["matched"])
    Assert.Contains(results, fun r -> r["kind"] = "Journal entry" && r["href"].StartsWith "#/ledger/entries/")
    Assert.Equal($"{results.Length} found for \"1500\".", value "searchSummary" model)

    let customer = run [ go "#/search?q=open%20abc%20corp" ] (books ()) |> fst
    Assert.Equal("#/customers/CUST-0001", (items "searchResults" customer |> List.head)["href"])

    let nothing = run [ go "#/search?q=zzz" ] (books ()) |> fst
    Assert.Equal("False", value "hasSearchResults" nothing)
    Assert.Equal("Nothing in the books matches \"zzz\".", value "searchSummary" nothing)

[<Fact>]
let ``SUM4-019: the palette offers the high-frequency operations, each a link, narrowed by what is typed`` () =
    let empty = run [ go "#/search" ] (books ()) |> fst

    Assert.Equal<string list>(
        [ "Create invoice"; "Record payment"; "Show overdue invoices"; "Show receivables"; "Run trial balance"; "Open the work queue" ],
        items "searchCommands" empty |> List.map (fun c -> c["label"])
    )

    Assert.Equal("Type a customer, invoice number, payment reference, amount, project, journal entry or memo.", value "searchSummary" empty)
    let commandFor q = run [ go $"#/search?q={q}" ] (books ()) |> fst |> items "searchCommands" |> List.map (fun c -> c["label"], c["href"])
    Assert.Equal<(string * string) list>([ "Run trial balance", "#/reports/trial-balance" ], commandFor "trial")
    Assert.Equal<(string * string) list>([ "Show overdue invoices", "#/invoices?overdue=true" ], commandFor "overdue")
    Assert.Equal<(string * string) list>([ "Create invoice", "#/invoices/new" ], commandFor "create%20invoice")
    Assert.Equal<(string * string) list>([ "Record payment", "#/payments" ], commandFor "record%20payment")
