/// The invoice aggregate's data (INV-DATA-001..014), one test per
/// requirement, each asserting what the requirement says.
module Summa.Tests.InvoiceDataTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let private html (invoice: IssuedInvoice) = Documents.render (Documents.ofInvoice invoice) |> ok
let private json (invoice: IssuedInvoice) = Documents.toJson (Documents.ofInvoice invoice)

[<Fact>]
let ``INV-DATA-001: an invoice's id is stable and independent of its number; a draft has no number`` () =
    let drafted = withDraft (draftFor [ consulting 1000L 10000L ])
    let draft = drafted.Drafts["D-1"]
    Assert.Equal("D-1", draft.DraftId)
    Assert.DoesNotContain("Number", typeof<DraftInvoice>.GetProperties() |> Array.map _.Name)
    let books, invoice = issue context request drafted |> ok
    Assert.Equal(request.InvoiceId, invoice.InvoiceId)
    Assert.NotEqual<string>(invoice.InvoiceId, invoice.Number)
    Assert.Equal(Some invoice.InvoiceId, books.IssuedFrom.TryFind "D-1")

[<Fact>]
let ``INV-DATA-002: an issued invoice has exactly one number, and it never changes`` () =
    let books, invoice = issued ()
    Assert.Equal("EF-2026-0001", invoice.Number)
    // Asking again with another number is a conflict, never a second number.
    Assert.True(Result.isError (issue context { request with NumberOverride = Some "EF-2026-0999" } books))
    Assert.Equal<InvoiceProblem list>([ AlreadyIssued invoice.InvoiceId ], saveDraft context (draftFor [ consulting 1000L 10000L ]) books |> refused)
    Assert.Equal(invoice.Number, books.Invoices[invoice.InvoiceId].Number)

[<Fact>]
let ``INV-DATA-003: an issued invoice carries the fields the requirement names`` () =
    let books, invoice = issued ()
    let r = start books
    Assert.False(String.IsNullOrEmpty invoice.InvoiceId)
    Assert.False(String.IsNullOrEmpty invoice.Number)
    Assert.Equal(abc.Id, invoice.CustomerId)
    Assert.Equal(abc.BillingName, invoice.Customer.BillingName)
    Assert.Equal(abc.BillingAddress, invoice.Customer.BillingAddress)
    Assert.Equal(abc.Email, invoice.Customer.Email) // the billing contact, as snapshotted
    Assert.Equal(request.IssueDate, invoice.IssueDate)
    Assert.Equal(request.IssueDate, books.Ledger.Entries[invoice.JournalEntryId].Date) // the accounting date
    Assert.Equal(DateOnly(2026, 11, 6), invoice.DueDate)
    Assert.Equal(Net 30, invoice.Terms)
    Assert.Equal("USD", invoice.Currency)
    Assert.NotEmpty invoice.Lines
    Assert.Equal(usd 603750L, invoice.Subtotal)
    Assert.NotEmpty invoice.Adjustments
    Assert.Equal(usd 605000L, invoice.Total)
    // Paid, credited, written off and outstanding are derived, never stored.
    Assert.Equal(usd 0L, amountPaid r invoice)
    Assert.Equal(usd 0L, writtenOff r invoice)
    Assert.Equal(usd 605000L, outstanding r invoice)
    Assert.Equal(Some "PRJ-ARCH", invoice.Lines.Head.Project)
    Assert.Equal(request.Template, invoice.Template)
    Assert.Equal(1, invoice.Approval.DraftVersion) // the domain version issued
    Assert.Equal(roundingPolicy, invoice.Rounding)
    Assert.Contains(books.Ledger.Audit, fun a -> a.Subject = invoice.InvoiceId) // audit references

[<Fact>]
let ``INV-DATA-004: the customer is snapshotted at issue; a later change does not alter the invoice`` () =
    let books, invoice = issued ()
    let moved = books |> saveCustomer context { abc with BillingName = "ABC Holdings"; BillingAddress = "9 New Road" }
    Assert.Equal(invoice, moved.Invoices[invoice.InvoiceId])
    Assert.Equal(abc.BillingName, moved.Invoices[invoice.InvoiceId].Customer.BillingName)
    Assert.Contains(abc.BillingName, html moved.Invoices[invoice.InvoiceId])

[<Fact>]
let ``INV-DATA-005: the organization's display fields are snapshotted at issue`` () =
    let _, invoice = issued ()
    Assert.Equal(request.Issuer, invoice.Issuer)
    Assert.Contains(request.Issuer.LegalName, html invoice)
    Assert.Contains(request.Issuer.PaymentInstructions, json invoice)

[<Fact>]
let ``INV-DATA-006: internal notes never reach the customer's document`` () =
    let notes = { noDetails with CustomerNotes = Some "Thank you for your business"; InternalNotes = Some "SECRET-INTERNAL-NOTE" }
    let _, invoice = issue context request (withDraft { draftFor [ consulting 1000L 10000L ] with Details = notes }) |> ok
    Assert.Equal(Some "SECRET-INTERNAL-NOTE", invoice.Details.InternalNotes)
    Assert.DoesNotContain("SECRET-INTERNAL-NOTE", html invoice)
    Assert.DoesNotContain("SECRET-INTERNAL-NOTE", json invoice)
    Assert.Contains("Thank you for your business", html invoice)

[<Fact>]
let ``INV-DATA-007: every issued line has a stable, unique identifier`` () =
    let _, invoice = issue context request (withDraft (draftFor [ consulting 1000L 10000L; { consulting 2000L 10000L with Description = "Workshop" } ])) |> ok
    let ids = identifiedLines invoice |> List.map fst
    Assert.Equal<string list>([ "INV-001/L1"; "INV-001/L2" ], ids)
    Assert.Equal<string list>(ids, identifiedLines invoice |> List.map fst)
    Assert.Equal("INV-001/L2", lineId invoice 1)

[<Fact>]
let ``INV-DATA-008: a line may carry its detailed description, unit, service period, category and the customer's code`` () =
    let details =
        { DetailedDescription = Some "Review of the payments architecture"
          Unit = Some "hours"
          ServicePeriod = Some(DateOnly(2026, 9, 1), DateOnly(2026, 9, 15))
          Category = Some "Advisory"
          CustomerCode = Some "GL-7710" }

    let line = { consulting 1000L 10000L with Details = Some details }
    let _, invoice = issue context request (withDraft (draftFor [ line ])) |> ok
    Assert.Equal(Some details, invoice.Lines.Head.Details)
    let page = html invoice

    for expected in [ "Review of the payments architecture"; "Unit: hours"; "Service period: September 1, 2026 to September 15, 2026"; "Category: Advisory"; "Your code: GL-7710" ] do
        Assert.Contains(expected, page)

    Assert.Contains("\"customerCode\":\"GL-7710\"", json invoice)
    // A line without details renders exactly as before.
    let _, plain = issued ()
    Assert.DoesNotContain("<small>", html plain)
    Assert.DoesNotContain("\"details\"", json plain)

[<Fact>]
let ``INV-DATA-009: money is fixed precision, never floating point`` () =
    Assert.Equal(Some typeof<int64>, typeof<Money>.GetProperties() |> Array.tryFind (fun p -> p.Name = "Minor") |> Option.map _.PropertyType)

    let floating (t: Type) =
        t.GetProperties() |> Array.exists (fun p -> p.PropertyType = typeof<float> || p.PropertyType = typeof<float32>)

    for t in [ typeof<Money>; typeof<InvoiceLine>; typeof<IssuedInvoice>; typeof<DraftInvoice>; typeof<Adjustment> ] do
        Assert.False(floating t, t.Name)

[<Fact>]
let ``INV-DATA-010: an invoice has one explicit currency for all its lines`` () =
    let _, invoice = issued ()
    Assert.True(invoice.Lines |> List.forall (fun l -> l.UnitPrice.Currency = invoice.Currency))
    // A line in another currency is refused (InvoiceAccountingTests "INV-FX-002").
    let euro = { consulting 1000L 10000L with UnitPrice = { Currency = "EUR"; Minor = 10000L } }
    Assert.True(Result.isError (issue context request (withDraft (draftFor [ euro ]))))

[<Fact>]
let ``INV-DATA-011: rounding is deterministic and the invoice keeps the policy version it used`` () =
    // Half away from zero, in minor units, line by line.
    Assert.Equal(usd 1L, extend 500L (usd 1L))
    Assert.Equal(usd 0L, extend 499L (usd 1L))
    Assert.Equal(usd 2L, extend 1500L (usd 1L))
    let _, invoice = issued ()
    Assert.Equal("summa.rounding/1", invoice.Rounding)

[<Fact>]
let ``INV-DATA-012: the domain calculates the totals; nothing supplied by a client is trusted`` () =
    // A draft has no total to supply.
    Assert.DoesNotContain("Total", typeof<DraftInvoice>.GetProperties() |> Array.map _.Name)
    let _, invoice = issued ()
    let lines = invoice.Lines |> List.map lineAmount |> sum "USD"
    Assert.Equal(lines, invoice.Subtotal)
    Assert.Equal(add invoice.Subtotal (invoice.Adjustments |> List.map _.Amount |> sum "USD"), invoice.Total)

[<Fact>]
let ``INV-DATA-013: a service period is kept apart from the issue date`` () =
    let period = DateOnly(2026, 9, 1), DateOnly(2026, 9, 15)
    let _, invoice = issue context request (withDraft { draftFor [ consulting 1000L 10000L ] with Details = { noDetails with ServicePeriod = Some period } }) |> ok
    Assert.Equal(Some period, invoice.Details.ServicePeriod)
    Assert.NotEqual(fst period, invoice.IssueDate)
    Assert.Contains("September 1, 2026 to September 15, 2026", html invoice)

[<Fact>]
let ``INV-DATA-014: the customer's references are typed fields, not notes`` () =
    let refs =
        [ ContractNumber, "MSA-2026-04"
          CostCenter, "CC-118"
          MatterNumber, "M-55"
          VendorReference, "V-0042"
          EngagementNumber, "ENG-7" ]

    let details = { noDetails with PurchaseOrder = Some "PO-881"; References = refs }
    let _, invoice = issue context request (withDraft { draftFor [ consulting 1000L 10000L ] with Details = details }) |> ok
    Assert.Equal<(ClientReferenceKind * string) list>(refs, invoice.Details.References)
    Assert.Equal(None, invoice.Details.CustomerNotes)
    let page = html invoice

    for label, value in [ "Purchase order", "PO-881"; "Contract number", "MSA-2026-04"; "Cost center", "CC-118"; "Matter number", "M-55"; "Vendor reference", "V-0042"; "Engagement number", "ENG-7" ] do
        Assert.Contains($"<dt>{label}</dt><dd>{value}</dd>", page)
