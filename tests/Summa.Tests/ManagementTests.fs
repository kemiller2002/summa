/// Management reporting (WI-0044, v0.4 §29-30; SUM4-029, SUM4-030).
module Summa.Tests.ManagementTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Sources
open Summa.Ledger.Payments
open Summa.Ledger.Billing
open Summa.Ledger.Management
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let private october = DateOnly(2026, 10, 1), DateOnly(2026, 10, 31)

/// ABC Corp's invoice EF-2026-0001 (6,050.00 USD, project PRJ-ARCH) on its
/// engagement "Retainer", and two software expenses: 1,000.00 on PRJ-ARCH
/// and the Retainer, 200.00 on neither.
let private books () =
    let retainer: Engagement =
        { Id = "ENG-1"; CustomerId = abc.Id; Name = "Retainer"; Currency = "USD"; FixedFee = None; Milestones = []; Terms = None }

    let withEngagement = { books with Engagements = Map.ofList [ retainer.Id, retainer ] }
    let draft = { draftFor [ consulting 34500L 17500L ] with Adjustments = [ { Kind = Fee; Label = "Processing fee"; Amount = usd 1250L } ]; EngagementId = Some "ENG-1" }
    let issuedBooks = saveDraft context draft withEngagement |> ok |> issue context request |> ok |> fst

    let expense id cents project engagement : Expense =
        { Id = id
          Date = DateOnly(2026, 10, 10)
          Description = $"Licences {id}"
          Amount = usd cents
          ExpenseAccountId = "software"
          PaidFromAccountId = "cash"
          CustomerId = Some abc.Id
          ProjectId = project
          EngagementId = engagement
          Billable = false
          JournalEntryId = $"JE-{id}" }

    start issuedBooks
    |> recordExpense context (expense "EXP-1" 100000L (Some "PRJ-ARCH") (Some "ENG-1")) |> ok
    |> recordExpense context (expense "EXP-2" 20000L None None) |> ok

let private rows (xs: Row list) = xs |> List.map (fun x -> x.Label, x.Amount.Minor)

[<Fact>]
let ``SUM4-029: revenue by client, by project and by engagement, from the posted journal`` () =
    let r = books ()
    let from, until = october
    Assert.Equal<(string * int64) list>([ "ABC Corp", 605000L ], revenueBy ByClient r "USD" from until |> rows)
    Assert.Equal<(string * int64) list>([ "PRJ-ARCH", 605000L ], revenueBy ByProject r "USD" from until |> rows)
    // The invoice's lines are posted by client and project; its engagement is the invoice's.
    Assert.Equal<(string * int64) list>([ "Retainer", 605000L ], revenueBy ByEngagement r "USD" from until |> rows)
    // Outside the period, nothing.
    Assert.Empty(revenueBy ByClient r "USD" (DateOnly(2026, 11, 1)) (DateOnly(2026, 11, 30)))

[<Fact>]
let ``SUM4-029: expenses by project keep what has no project, as unassigned and last`` () =
    let r = books ()
    let from, until = october
    Assert.Equal<(string * int64) list>([ "PRJ-ARCH", 100000L; unassigned, 20000L ], expensesBy ByProject r "USD" from until |> rows)

[<Fact>]
let ``SUM4-029: what each customer owes, as of a date`` () =
    let r = books ()
    Assert.Equal<(string * int64) list>([ "ABC Corp", 605000L ], outstandingByCustomer r "USD" (DateOnly(2026, 10, 31)) |> rows)
    // Before the invoice was issued, nothing was owed.
    Assert.Empty(outstandingByCustomer r "USD" (DateOnly(2026, 10, 1)))

[<Fact>]
let ``SUM4-030: an engagement's contribution is its revenue less the costs Summa holds; costs with no source are not zero but unknown`` () =
    let r = books ()
    let from, until = october

    match contribution r "USD" from until with
    | [ retainer; rest ] ->
        Assert.Equal("Retainer", retainer.Label)
        Assert.Equal(usd 605000L, retainer.Revenue)
        Assert.Equal(usd 100000L, retainer.ExternalExpenses)
        Assert.Equal(usd 505000L, retainer.Contribution)
        Assert.Equal(None, retainer.LaborCost)
        Assert.Equal(None, retainer.AiCost)
        Assert.Equal(None, retainer.InfrastructureCost)
        Assert.Equal(unassigned, rest.Label)
        Assert.Equal(usd -20000L, rest.Contribution)
    | other -> failwith $"%A{other}"
