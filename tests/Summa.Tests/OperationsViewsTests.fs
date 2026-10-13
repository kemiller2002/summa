/// Search, receivables, aging and the invoice timeline (WI-0029 slice 11,
/// INV-OPS-001 to INV-OPS-004). Drill-down and the attention queue are
/// asserted where people see them, in Summa.Web.Tests.
module Summa.Tests.OperationsViewsTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Issuance
open Summa.Ledger.Lifecycle
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let private later hours = { context with When = context.When.AddHours(float hours) }

/// One invoice with a project, an engagement, a PO, a client reference, a
/// typed reference, a Chrona time line and an expense line: 34.5 hours at
/// 175.00 plus a 62.50 expense, 6,100.00 in all.
let private detailed () =
    let time =
        { consulting 34500L 17500L with
            Source = TimeSource [ { PublicationId = "PUB-77"; ActivityId = "ACT-9"; Revision = 2; Minutes = 2070 } ] }

    let expense =
        { consulting 1000L 6250L with
            Description = "Travel"
            Project = Some "PRJ-TRAVEL"
            Source = ExpenseSource "EXP-42" }

    let draft =
        { draftFor [ time; expense ] with
            EngagementId = Some "ENG-5"
            Details =
                { noDetails with
                    PurchaseOrder = Some "PO-8812"
                    ClientReference = Some "ABC-REF-3"
                    References = [ CostCenter, "CC-404" ] } }

    let engagement =
        { Id = "ENG-5"; CustomerId = abc.Id; Name = "Assessment"; Currency = "USD"; FixedFee = None; Milestones = []; Terms = None }

    let books = withDraft draft |> Billing.saveEngagement context engagement |> ok
    let published: SourceTime =
        { PublicationId = "PUB-77"; OrganizationId = "org-1"; ActivityId = "ACT-9"; Revision = 2; PerformerId = "github:1"; BusinessDate = DateOnly(2026, 10, 6)
          ProjectId = "PRJ-ARCH"; ClientId = Some abc.Id; EngagementId = Some "ENG-5"; ActivityTypeId = "consulting"; Description = "Assessment"; ExactMinutes = 2070
          BillableMinutes = 2070; Approved = true; RateReference = None; Origin = OriginUnknown; Lineage = []; WorkItem = None; Supersedes = None
          PublishedAt = DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero) }

    let travel =
        { Id = "EXP-42"; Date = DateOnly(2026, 10, 5); Description = "Travel"; Amount = usd 6250L; ExpenseAccountId = "software"; PaidFromAccountId = "cash"
          CustomerId = Some abc.Id; ProjectId = Some "PRJ-TRAVEL"; EngagementId = Some "ENG-5"; Billable = true; JournalEntryId = "JE-EXP-42" }

    start books
    |> Billing.importTime context published |> ok |> fst
    |> Billing.recordExpense context travel |> ok
    |> submitForReview context request |> ok
    |> issueInvoice context request |> ok

let private pdfStored (r: Receivables) =
    recordPdf (later 1) "INV-001" { Reference = "artifacts://test/INV-001.pdf"; Sha256 = String('b', 64); Size = 1024L; Renderer = "folio-p0" } r |> ok

let private sent (r: Receivables) =
    let request =
        { AttemptId = "DL-1"
          InvoiceId = "INV-001"
          Recipients = { To = [ "ap@abc.example" ]; Cc = [ "controller@abc.example" ]; ReplyTo = None }
          Channel = EmailChannel
          Policy = AttachPdf
          MessageTemplate = "invoice-email/1"
          RetryOf = None }

    send (later 2) (DeliveryAuthority(fun _ -> true)) request r |> ok

[<Fact>]
let ``INV-OPS-001: an invoice is found by number, customer, amount, date, project, engagement, PO and references, source id and recipient`` () =
    let r, invoice = detailed ()
    let r = r |> pdfStored |> sent
    let found (term: string) = searchTexts r invoice |> List.exists (fun t -> t.Contains(term, StringComparison.OrdinalIgnoreCase))

    for term in
        [ "EF-2026-0001" // number
          "abc corp" // customer, any case
          abc.Id
          "6,100.00" // amount as shown
          "6100.00" // amount as typed
          "2026-10-07" // issue date
          "2026-11-06" // due date
          "PRJ-ARCH" // project
          "PRJ-TRAVEL"
          "ENG-5" // engagement
          "PO-8812" // purchase order
          "ABC-REF-3" // client reference
          "CC-404" // typed reference
          "PUB-77" // Chrona publication
          "ACT-9" // Chrona activity
          "EXP-42" // expense
          "ap@abc.example" // delivery recipient
          "controller@abc.example" ] do
        Assert.True(found term, $"not found by {term}")

    Assert.False(found "PO-9999")

[<Fact>]
let ``INV-OPS-002: issued invoices with a balance are receivables; a settled one is not`` () =
    let r, _ = detailed ()
    Assert.Equal(usd 610000L, (aging "USD" (DateOnly(2026, 10, 20)) r |> List.exactlyOne).Total)

    let paid =
        r
        |> recordPayment context (payment "PAY-1" 610000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 610000L) |> ok

    Assert.Empty(aging "USD" (DateOnly(2026, 10, 20)) paid)

[<Fact>]
let ``INV-OPS-003: aging uses what is still owed, not the invoice total`` () =
    let r, invoice = detailed ()

    let part =
        r
        |> recordPayment context (payment "PAY-1" 100000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok

    let row = aging "USD" (DateOnly(2026, 12, 1)) part |> List.exactlyOne
    Assert.Equal(usd 510000L, row.Days1To30)
    Assert.Equal(usd 510000L, row.Total)
    Assert.Equal(usd 610000L, invoice.Total)

[<Fact>]
let ``INV-OPS-004: the timeline combines issue, artifact, delivery, follow-up and payment, oldest first, and nothing of another invoice`` () =
    let r, invoice = detailed ()

    let r =
        r
        |> pdfStored
        |> sent
        |> dispute (later 3) "INV-001" "hours disputed" (DateOnly(2026, 10, 10)) |> ok
        |> recordPayment (later 4) (payment "PAY-1" 100000L) |> ok
        |> allocate (later 5) (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok
        // A payment that settles nothing of this invoice stays out of its timeline.
        |> recordPayment (later 6) { payment "PAY-2" 5000L with Reference = "unrelated" } |> ok

    let events = timeline r invoice
    let whats = events |> List.map _.What

    // Domain (review, issue), artifact, delivery, follow-up and payment events, in the order they happened.
    let ofInterest =
        [ "invoice-ready-for-review"; "invoice-issued"; "invoice-pdf-generated"; "invoice-delivery-queued"; "invoice-disputed"; "payment-recorded"; "payment-allocated" ]

    Assert.Equal<string list>(ofInterest, whats |> List.filter (fun w -> List.contains w ofInterest))
    Assert.DoesNotContain(events, fun a -> a.Subject = "PAY-2")
    Assert.Equal<DateTimeOffset list>(events |> List.map _.When |> List.sort, events |> List.map _.When)
    Assert.True(events |> List.forall (fun a -> a.Who <> ""))
