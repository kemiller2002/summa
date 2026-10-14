/// Management report views at #/reports/management (WI-0044, v0.4 §29-30;
/// SUM4-029, SUM4-030).
module Summa.Web.Tests.ManagementReportTests

open System
open Xunit
open Limen.Routing
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Sources
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

/// ABC Corp's invoice for 1,500.00 USD on its engagement "Retainer", and a
/// 400.00 software expense on that engagement.
let private books () =
    let started =
        run [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}"""); Loaded None ] initial |> fst

    let set =
        run
            [ go "#/customers"; CustomerNameChanged "ABC Corp"; CustomerAddressChanged "1 Main Street"; CustomerTermsChanged "30"; CustomerAdded
              go "#/engagements"; EngagementCustomerChanged "CUST-0001"; EngagementNameChanged "Retainer"; EngagementFeeChanged ""; EngagementAdded
              go "#/invoices/new?customer=CUST-0001" ]
            started
        |> fst

    let engagement = set.Books.Value.Books.Engagements |> Map.toList |> List.exactlyOne |> fst
    let key = set.Draft.Lines.Head.Key

    let issued =
        run [ DraftCustomerChanged "CUST-0001"; DraftEngagementChosen engagement; LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "150"); DraftSubmitted; DraftIssued ] set
        |> fst

    let expense: Expense =
        { Id = "EXP-1"
          Date = DateOnly(2026, 10, 7)
          Description = "Licences"
          Amount = usd 40000L
          ExpenseAccountId = "6100"
          PaidFromAccountId = "1000"
          CustomerId = Some "CUST-0001"
          ProjectId = None
          EngagementId = Some engagement
          Billable = false
          JournalEntryId = "JE-EXP-1" }

    let b = issued.Books.Value
    let withExpense = Billing.recordExpense { Who = "t"; When = ctx.Now; Source = "test"; CorrelationId = None; Provenance = None } expense b |> Result.defaultWith (fun e -> failwith $"%A{e}")
    { issued with Books = Some withExpense }

[<Fact>]
let ``SUM4-029: the reports page lists the saved views, each a link that opens it`` () =
    let reports = run [ go "#/reports" ] (books ()) |> fst

    Assert.Equal<(string * string) list>(
        [ "Revenue by client", "#/reports/management"
          "Revenue by project", "#/reports/management?view=revenue-by-project"
          "Expenses by project", "#/reports/management?view=expenses-by-project"
          "Profitability by engagement", "#/reports/management?view=engagement-contribution"
          "Outstanding by customer", "#/reports/management?view=outstanding-by-customer" ],
        items "managementViewLinks" reports |> List.map (fun r -> r["label"], r["href"])
    )

    let byClient = run [ go "#/reports/management?from=2026-10-01&to=2026-10-31" ] (books ()) |> fst
    Assert.Equal("Revenue by client", value "managementTitle" byClient)
    Assert.Equal<(string * string) list>([ "ABC Corp", "1,500.00" ], items "managementRows" byClient |> List.map (fun r -> r["label"], r["amount"]))
    Assert.Equal("1,500.00", value "managementTotal" byClient)
    // The period stays in every view's link, so a chosen view is kept by its address.
    let views = items "managementViews" byClient
    Assert.Equal("#/reports/management?view=expenses-by-project&from=2026-10-01&to=2026-10-31", views[2]["href"])
    Assert.Equal("page", views[0]["current"])

[<Fact>]
let ``SUM4-029: the period refines the address in place`` () =
    let model = run [ go "#/reports/management?view=revenue-by-project" ] (books ()) |> fst
    let _, effects = run [ ManagementFromChanged "2026-10-01" ] model
    Assert.Equal<AppEffect list>([ Navigate(NavigationEffect.Replace "/reports/management?view=revenue-by-project&from=2026-10-01") ], effects)
    let later = run [ go "#/reports/management?view=revenue-by-project&from=2026-11-01&to=2026-11-30" ] (books ()) |> fst
    Assert.Equal("True", value "noManagementRows" later)

[<Fact>]
let ``SUM4-030: profitability by engagement is revenue less external expenses; costs with no source say so`` () =
    let model = run [ go "#/reports/management?view=engagement-contribution&from=2026-10-01&to=2026-10-31" ] (books ()) |> fst
    let row = items "contributionRows" model |> List.exactlyOne
    Assert.Equal("Retainer", row["label"])
    Assert.Equal("1,500.00", row["revenue"])
    Assert.Equal("400.00", row["expenses"])
    Assert.Equal("Not available", row["labor"])
    Assert.Equal("Not available", row["ai"])
    Assert.Equal("Not available", row["infrastructure"])
    Assert.Equal("1,100.00", row["contribution"])
    Assert.StartsWith("Labor, AI and infrastructure costs have no source in Summa yet", value "managementNote" model)

[<Fact>]
let ``SUM4-029: outstanding by customer is as of a date`` () =
    let model = run [ go "#/reports/management?view=outstanding-by-customer&to=2026-10-31" ] (books ()) |> fst
    Assert.Equal("False", value "managementIsPeriod" model)
    Assert.Equal<(string * string) list>([ "ABC Corp", "1,500.00" ], items "managementRows" model |> List.map (fun r -> r["label"], r["amount"]))
    Assert.StartsWith("As of ", value "managementPeriod" model)
