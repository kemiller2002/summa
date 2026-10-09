/// Natural-language invoice requests (WI-0029, INV-AGENT-001) and what an
/// audit event says a change did (INV-AUD-002): the entity's version, the
/// outcome and the fields that changed.
module Summa.Tests.AgentRequestTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Billing
open Summa.Ledger.AgentRequests
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.BillingTests

let private agent: Context =
    { context with
        Who = "summa-agent"
        When = DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)
        Provenance =
            Some
                { ActorKind = "agent"
                  Agent = Some { Provider = "anthropic"; Model = "claude"; Runtime = "claude-code" }
                  ExecutionId = Some "EXE-summa.77"
                  SourceSystem = None
                  SourceId = None
                  Reason = None } }

let private canonical =
    "Invoice ABC Corp for all approved architecture work from September 1 through September 15 at the contracted rate and include approved reimbursable expenses."

let private on (date: DateOnly) (t: SourceTime) = { t with BusinessDate = date }
let private sep day = DateOnly(2026, 9, day)

/// ABC's September: architecture time inside and outside the period, other
/// work, time not yet approved, another customer's, and two expenses.
let private september () =
    let xyz = { abc with Id = "CUST-XYZ"; Name = "XYZ Ltd"; BillingName = "XYZ Ltd" }

    let expense id (date: DateOnly) billable : Expense =
        { Id = id
          Date = date
          Description = $"Expense {id}"
          Amount = usd 12000L
          ExpenseAccountId = "travel"
          PaidFromAccountId = "cash"
          CustomerId = Some abc.Id
          ProjectId = Some "architecture-review"
          EngagementId = None
          Billable = billable
          JournalEntryId = $"JE-{id}" }

    let r =
        { fresh () with Books = saveCustomer context xyz (fresh ()).Books }
        |> importAll
            [ time "PUB-1" "A-1" 120 "p-1" "architecture-review" |> on (sep 2)
              time "PUB-2" "A-2" 90 "p-1" "architecture-review" |> on (sep 15)
              time "PUB-3" "A-3" 60 "p-1" "architecture-review" |> on (sep 16)
              time "PUB-4" "A-4" 60 "p-1" "support" |> on (sep 3)
              { time "PUB-5" "A-5" 60 "p-1" "architecture-review" with Approved = false } |> on (sep 4)
              { time "PUB-6" "A-6" 60 "p-1" "architecture-review" with ClientId = Some "CUST-XYZ" } |> on (sep 5) ]
        |> withRates [ ForCustomer abc.Id, usd 27500L ]

    r
    |> recordExpense context (expense "EXP-1" (sep 10) true)
    |> ok
    |> recordExpense context (expense "EXP-2" (sep 11) false)
    |> ok
    |> recordExpense context (expense "EXP-3" (sep 20) true)
    |> ok

[<Fact>]
let ``the documented phrasing is read into what it asks for`` () =
    match interpret (DateOnly(2026, 10, 7)) canonical with
    | Ok intent ->
        Assert.Equal("ABC Corp", intent.Customer)
        Assert.Equal(Some "architecture", intent.Work)
        Assert.Equal(Some(sep 1), intent.From)
        Assert.Equal(Some(sep 15), intent.Through)
        Assert.Equal(Contracted, intent.Rate)
        Assert.True intent.Expenses
    | Error questions -> failwith $"%A{questions}"

    let plain = interpret (DateOnly(2026, 10, 7)) "invoice ABC for approved work" |> ok
    Assert.Equal(None, plain.Work)
    Assert.Equal(None, plain.From)
    Assert.False plain.Expenses
    let iso = interpret (DateOnly(2026, 10, 7)) "Invoice ABC for all work from 2025-12-01 to 2025-12-31" |> ok
    Assert.Equal(Some(DateOnly(2025, 12, 1)), iso.From)

[<Fact>]
let ``what cannot be read is a question, never a guess`` () =
    Assert.Equal<Question list>([ NotUnderstood "Bill everyone for everything" ], interpret (DateOnly(2026, 10, 7)) "Bill everyone for everything" |> refused)
    Assert.Equal<Question list>([ WhichDate "Septembr 1" ], interpret (DateOnly(2026, 10, 7)) "Invoice ABC for work from Septembr 1 through September 15" |> refused)
    Assert.Equal<Question list>([ PeriodBackwards(sep 15, sep 1) ], interpret (DateOnly(2026, 10, 7)) "Invoice ABC for work from September 15 through September 1" |> refused)
    Assert.Contains("Ask in the form", describe (NotUnderstood "x"))

[<Fact>]
let ``a request becomes a proposal of the customer's approved, unbilled work in the period, at the contracted rate`` () =
    let r = september () |> Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-1" canonical |> ok
    let proposal = r.Books.Proposals["P-AG-1"]
    Assert.Equal(abc.Id, proposal.CustomerId)
    Assert.Equal(Proposed, proposal.State)
    // Time inside the period that mentions architecture; the billable expense in the period.
    let sources = proposal.Lines |> List.collect (fun l -> sourceKeys r.Books l.Line) |> List.sort
    Assert.Equal<string list>([ "expense:EXP-1"; "time:A-1"; "time:A-2" ], sources)
    Assert.True(proposal.Lines |> List.forall _.Priced)
    Assert.Equal<int64 list>([ 27500L; 12000L ], proposal.Lines |> List.map (fun l -> l.Line.UnitPrice.Minor))
    // Nothing is issued: a proposal waits for a person (INV-AGENT-007).
    Assert.True r.Books.Invoices.IsEmpty
    // The agent's contribution keeps the request it acted on.
    let first = proposal.Contributions.Head
    Assert.Equal("summa-agent", first.Who)
    Assert.Equal(Some $"request: {canonical}", first.Provenance |> Option.bind _.Reason)
    Assert.Equal(Some "agent", first.Provenance |> Option.map _.ActorKind)
    // The same request again changes nothing.
    Assert.Equal(r, r |> Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-1" canonical |> ok)

[<Fact>]
let ``a request that the books do not settle comes back as questions`` () =
    let r = september ()
    let ask text = Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-2" text r |> refused
    Assert.Equal<Question list>([ WhichCustomer("Acme", []) ], ask "Invoice Acme for approved work")
    let twoCorps = { r with Books = saveCustomer context { abc with Id = "CUST-ABC2"; Name = "ABC Corporation"; BillingName = "ABC Corporation" } r.Books }
    Assert.Equal<Question list>([ WhichCustomer("ABC", [ "ABC Corp"; "ABC Corporation" ]) ], Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-2" "Invoice ABC for approved work" twoCorps |> refused)
    Assert.Equal<Question list>([ RateNotContracted(usd 30000L) ], ask "Invoice ABC Corp for approved work at $300 per hour")
    Assert.Equal<Question list>([ NoMatchingWork "design" ], ask "Invoice ABC Corp for approved design work from September 1 through September 15")
    Assert.Equal<Question list>([ NothingToBill ], ask "Invoice XYZ Ltd for approved work from October 1 through October 31")
    // A unique partial name is enough; the result is still only a proposal.
    let partial = Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-3" "Invoice XYZ for approved work" r |> ok
    Assert.Equal("CUST-XYZ", partial.Books.Proposals["P-AG-3"].CustomerId)

[<Fact>]
let ``time another proposal holds is left out, and the explanation says why`` () =
    let first = september () |> Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-1" "Invoice ABC Corp for approved work from September 1 through September 2" |> ok
    let second = first |> Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-2" "Invoice ABC Corp for approved architecture work from September 1 through September 30" |> ok
    let keys = second.Books.Proposals["P-AG-2"].Lines |> List.collect (fun l -> sourceKeys second.Books l.Line) |> List.sort
    Assert.Equal<string list>([ "time:A-2"; "time:A-3" ], keys)
    let why = (explain second second.Books.Proposals["P-AG-2"] (Net 30)).Excluded
    Assert.Contains("time A-1: reserved by proposal P-AG-1", why)

// ---- What an audit event says a change did (INV-AUD-002) -------------------------------------

[<Fact>]
let ``a draft's audit events carry its version and the fields that changed`` () =
    let books = (fresh ()).Books
    let draft = { draftFor [ consulting 10000L 15000L ] with DraftId = "D-7" }
    let created = saveDraft context draft books |> ok
    let changed = saveDraft context { created.Drafts["D-7"] with Terms = Some(Net 15); Details = { noDetails with PurchaseOrder = Some "PO-1" } } created |> ok
    let events = changed.Ledger.Audit |> List.filter (fun a -> a.Subject = "D-7")
    Assert.Equal(Some { Version = Some 1; Outcome = "applied"; Changed = [] }, events[0].Change)
    Assert.Equal(Some { Version = Some 2; Outcome = "applied"; Changed = [ "terms: default -> Net 15"; "purchase order: none -> PO-1" ] }, events[1].Change)

[<Fact>]
let ``a customer's change names what changed, before and after`` () =
    let books = (fresh ()).Books
    let changed = saveCustomer context { abc with Email = "billing@abc.example"; DefaultTerms = Some DueOnReceipt } books
    let last = changed.Ledger.Audit |> List.last
    Assert.Equal(Some [ "email: ap@abc.example -> billing@abc.example"; "terms: Net 30 -> due on receipt" ], last.Change |> Option.map _.Changed)

[<Fact>]
let ``a rate change on a proposal says which line, from what to what`` () =
    let r = september () |> Summa.Ledger.AgentRequests.propose agent billingAccounts "P-AG-1" canonical |> ok
    let person = { context with Who = "github:583231" }
    let changed = r |> overrideRate person "P-AG-1" 0 (usd 30000L) "agreed senior rate" |> ok
    let last = changed.Books.Ledger.Audit |> List.last
    Assert.Equal("rate-overridden", last.What)
    Assert.Equal(Some [ "line 1 rate: 275.00 USD -> 300.00 USD" ], last.Change |> Option.map _.Changed)

[<Fact>]
let ``an issued invoice's audit event names the draft version issued`` () =
    let books = (fresh ()).Books
    let saved = saveDraft context (draftFor [ consulting 10000L 15000L ]) books |> ok
    let issued, _ = issue context request saved |> ok
    let last = issued.Ledger.Audit |> List.last
    Assert.Equal("invoice-issued", last.What)
    Assert.Equal(Some(Some 1), last.Change |> Option.map _.Version)
