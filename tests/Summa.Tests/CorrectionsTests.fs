/// v0.2 receivables edge cases, part 2 (WI-0024): corrections before and
/// after issue, voids, discounts, payment terms priority, late status and
/// date semantics (§6-8, §13, §18, §19, §27).
module Summa.Tests.CorrectionsTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Corrections
open Summa.Ledger.Reports
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let private voidOf reason = { InvoiceId = "INV-001"; Reason = reason; Date = DateOnly(2026, 10, 9); JournalEntryId = "JE-VOID-1" }
let private asOf = DateOnly(2026, 12, 31)

let private balancesOk (r: Receivables) =
    let tb = trialBalance "USD" asOf r.Books.Ledger
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)

let private entryLines (books: Books) (entryId: string) =
    books.Ledger.Entries[entryId].Lines |> List.map (fun l -> l.AccountId, l.Side, l.Memo)

[<Fact>]
let ``a draft may be edited freely and posts nothing until it is issued`` () =
    let other = { abc with Id = "CUST-XYZ"; Name = "XYZ Ltd" }
    let start = books |> saveCustomer context other
    let first = saveDraft context (draftFor [ consulting 1000L 10000L ]) start |> ok

    let edited =
        { draftFor [ { consulting 2500L 12000L with Description = "Revised scope"; Project = Some "PRJ-NEW" } ] with
            CustomerId = other.Id
            Terms = Some(Net 15)
            DueDate = Some(DateOnly(2026, 11, 1)) }

    let changed = saveDraft context edited first |> ok
    Assert.Equal(edited, changed.Drafts["D-1"])
    Assert.Empty(changed.Ledger.Entries)
    Assert.Equal<string list>([ "invoice-created"; "invoice-changed" ], changed.Ledger.Audit |> List.filter (fun a -> a.Subject = "D-1") |> List.map _.What)
    // Once issued it is not a draft any more.
    let issuedBooks, invoice = issue context request changed |> ok
    Assert.Equal<InvoiceProblem list>([ AlreadyIssued invoice.InvoiceId ], saveDraft context edited issuedBooks |> refused)

[<Fact>]
let ``voiding reverses the entry, cancels the receivable and keeps the invoice`` () =
    let r = receivables ()
    let voided = voidInvoice context (voidOf "Issued to the wrong customer") r |> ok
    let invoice = voided.Books.Invoices["INV-001"]
    Assert.Equal(Voided, status voided invoice)
    Assert.Equal(usd 0L, outstanding voided invoice)
    Assert.Equal(Cancelled, obligationStatus voided voided.Books.Obligations[invoice.ObligationId])
    // Debit Revenue, Credit AR: the mirror of the original, which stays.
    Assert.Equal(Reversed "JE-VOID-1", voided.Books.Ledger.Entries[invoice.JournalEntryId].State)
    Assert.Equal(Some invoice.JournalEntryId, voided.Books.Ledger.Entries["JE-VOID-1"].Reverses)
    Assert.Equal(usd 0L, balance voided.Books.Ledger "USD" asOf "ar")
    Assert.Equal(usd 0L, balance voided.Books.Ledger "USD" asOf "revenue")
    Assert.Contains(voided.Books.Ledger.Audit, fun a -> a.What = "invoice-voided" && a.Subject = "INV-001")
    // Retrying is a no-op; voiding again differently is refused; nothing more can be paid.
    Assert.Equal(voided, voidInvoice context (voidOf "Issued to the wrong customer") voided |> ok)
    Assert.Equal<CorrectionProblem list>([ AlreadyVoided "INV-001" ], voidInvoice context (voidOf "Another reason") voided |> refused)
    let paid = voided |> recordPayment context (payment "PAY-1" 605000L) |> ok
    Assert.True(Result.isError (allocate context (allocation "AL-1" "PAY-1" "INV-001" 605000L) paid))
    balancesOk voided

[<Fact>]
let ``a void needs a reason and an invoice nothing has settled`` () =
    let r = receivables () |> recordPayment context (payment "PAY-1" 100000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok
    Assert.Equal<CorrectionProblem list>([ VoidReasonRequired; HasSettlements(usd 100000L) ], voidInvoice context (voidOf " ") r |> refused)
    Assert.Equal<CorrectionProblem list>([ UnknownInvoiceToCorrect "INV-404" ], voidInvoice context { voidOf "x" with InvoiceId = "INV-404" } r |> refused)

[<Fact>]
let ``void and reissue: the original stays visible and the new invoice names it`` () =
    let r = receivables ()
    let voided, draft = voidAndReissue context (voidOf "Wrong rate") "D-2" r |> ok
    Assert.Equal(Some "INV-001", draft.Corrects)
    Assert.Equal<InvoiceLine list>(voided.Books.Invoices["INV-001"].Lines, draft.Lines)
    // The original took the customer's terms, so the reissue resolves them again.
    Assert.Equal(None, draft.Terms)
    let corrected = saveDraft context { draft with Lines = [ consulting 34500L 16000L ] } voided.Books |> ok
    let reissue = { request with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-000002"; ObligationId = "OBL-002"; IssueDate = DateOnly(2026, 10, 9) }
    let books', second = issue context reissue corrected |> ok
    let after = { voided with Books = books' }
    Assert.Equal(Some "INV-001", second.Corrects)
    Assert.Equal("EF-2026-0002", second.Number)
    Assert.Equal(Voided, status after after.Books.Invoices["INV-001"])
    Assert.Equal(Issued, status after second)
    // 34.5 hours at 160.00 plus the 12.50 fee the draft kept.
    Assert.Equal(usd 553250L, balance after.Books.Ledger "USD" asOf "ar")
    // Retrying the whole step returns the same draft.
    Assert.Equal(draft, voidAndReissue context (voidOf "Wrong rate") "D-2" r |> ok |> snd)
    balancesOk after

[<Fact>]
let ``a correcting invoice names the invoice it corrects, for the same customer only`` () =
    let r = receivables ()
    let original = r.Books.Invoices["INV-001"]
    let extra = { correctingDraft "D-2" original with Lines = [ consulting 2000L 17500L ] }
    let withExtra = saveDraft context extra r.Books |> ok
    let second = { request with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-000002"; ObligationId = "OBL-002" }
    let _, correcting = issue context second withExtra |> ok
    Assert.Equal(Some "INV-001", correcting.Corrects)
    Assert.Equal(usd 35000L, correcting.Total)
    // The original is unchanged.
    Assert.Equal(original, r.Books.Invoices["INV-001"])
    let stranger = { abc with Id = "CUST-XYZ" }
    let wrong = saveDraft context { extra with CustomerId = stranger.Id } (withExtra |> saveCustomer context stranger) |> ok
    Assert.Equal<InvoiceProblem list>([ InvalidCorrection "invoice INV-001 is another customer's" ], issue context second wrong |> refused)
    let missing = saveDraft context { extra with Corrects = Some "INV-404" } r.Books |> ok
    Assert.Equal<InvoiceProblem list>([ InvalidCorrection "invoice INV-404 is not issued" ], issue context second missing |> refused)

[<Fact>]
let ``discounts are explicit, never a changed price, and post deterministically`` () =
    // 10 hours at 150.00 with 10% off; 2 hours at 100.01 with 20.00 off;
    // then 5% and 50.00 off the invoice.
    let lines =
        [ { consulting 10000L 15000L with Discount = Some(Percent 1000) }
          { consulting 2000L 10001L with Description = "Workshop"; Discount = Some(Fixed(usd 2000L)) } ]

    let draft = { draftFor lines with Discounts = [ { Label = "Loyalty"; Rule = Percent 500 }; { Label = "Promotion"; Rule = Fixed(usd 5000L) } ] }
    // Lines: 1,500.00 - 150.00 = 1,350.00 and 200.02 - 20.00 = 180.02.
    Assert.Equal(usd 153002L, subtotal draft)
    // 5% of 1,530.02 is 76.501, rounded half away from zero to 76.50.
    Assert.Equal(usd 12650L, discountTotal "USD" (subtotal draft) draft.Discounts)
    Assert.Equal(usd 140352L, total draft)
    let issuedBooks, invoice = issue context request (withDraft draft) |> ok
    Assert.Equal<InvoiceLine list>(lines, invoice.Lines)
    Assert.Equal(usd 15000L, invoice.Lines.Head.UnitPrice)

    Assert.Equal<(string * Side * string option) list>(
        [ "ar", Debit(usd 140352L), Some "EF-2026-0001"
          "revenue", Credit(usd 135000L), Some "Architecture assessment"
          "revenue", Credit(usd 18002L), Some "Workshop"
          "revenue", Debit(usd 7650L), Some "Discount: Loyalty"
          "revenue", Debit(usd 5000L), Some "Discount: Promotion" ],
        entryLines issuedBooks invoice.JournalEntryId
    )

    // The same draft always gives the same entry.
    let again, _ = issue context request (withDraft draft) |> ok
    Assert.Equal(issuedBooks.Ledger.Entries[invoice.JournalEntryId], again.Ledger.Entries[invoice.JournalEntryId])

[<Fact>]
let ``impossible discounts are refused`` () =
    let tooMuch = { draftFor [ { consulting 1000L 10000L with Discount = Some(Percent 10001) } ] with Discounts = [ { Label = "All of it"; Rule = Fixed(usd 20000L) } ] }
    let problems = issue context request (withDraft tooMuch) |> refused
    Assert.Contains(InvalidDiscount "'Architecture assessment': a percentage must be 0.01% to 100%", problems)
    let overLine = draftFor [ { consulting 1000L 10000L with Discount = Some(Fixed(usd 10001L)) } ]
    Assert.Contains(InvalidDiscount "'Architecture assessment': the discount is larger than the line", issue context request (withDraft overLine) |> refused)
    let overInvoice = { draftFor [ consulting 1000L 10000L ] with Discounts = [ { Label = "Too generous"; Rule = Fixed(usd 10001L) } ] }
    Assert.Contains(InvalidDiscount "the invoice discounts are larger than the subtotal", issue context request (withDraft overInvoice) |> refused)

[<Fact>]
let ``terms come from the invoice, then the customer, then the system, and are kept`` () =
    Assert.Equal((Net 10, InvoiceTerms), resolveTerms (Some(Net 10)) (Some(Net 30)) DueOnReceipt)
    Assert.Equal((Net 30, CustomerTerms), resolveTerms None (Some(Net 30)) DueOnReceipt)
    Assert.Equal((DueOnReceipt, SystemTerms), resolveTerms None None DueOnReceipt)
    let issueWith (customerTerms: PaymentTerms option) (invoiceTerms: PaymentTerms option) =
        let b = openBooks chart |> saveCustomer context { abc with DefaultTerms = customerTerms }
        let withTerms = saveDraft context { draftFor [ consulting 1000L 10000L ] with Terms = invoiceTerms } b |> ok
        issue context { request with SystemTerms = Net 45 } withTerms |> ok |> snd
    let own = issueWith (Some(Net 30)) (Some(Net 10))
    Assert.Equal((Net 10, InvoiceTerms, DateOnly(2026, 10, 17)), (own.Terms, own.TermsSource, own.DueDate))
    let customers = issueWith (Some(Net 30)) None
    Assert.Equal((Net 30, CustomerTerms, DateOnly(2026, 11, 6)), (customers.Terms, customers.TermsSource, customers.DueDate))
    let system = issueWith None None
    Assert.Equal((Net 45, SystemTerms, DateOnly(2026, 11, 21)), (system.Terms, system.TermsSource, system.DueDate))

[<Fact>]
let ``overdue is derived from what is outstanding and the date, never set`` () =
    let r = receivables ()
    let invoice = invoiceOf r
    Assert.Equal(DateOnly(2026, 11, 6), invoice.DueDate)
    Assert.False(isOverdue (DateOnly(2026, 11, 6)) r invoice)
    Assert.True(isOverdue (DateOnly(2026, 11, 7)) r invoice)
    let partly = r |> recordPayment context (payment "PAY-1" 605000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 5000L) |> ok
    Assert.True(isOverdue (DateOnly(2026, 11, 7)) partly invoice)
    let paid = partly |> allocate context (allocation "AL-2" "PAY-1" "INV-001" 600000L) |> ok
    Assert.False(isOverdue (DateOnly(2027, 1, 1)) paid invoice)
    let voided = voidInvoice context (voidOf "Duplicate") r |> ok
    Assert.False(isOverdue (DateOnly(2027, 1, 1)) voided invoice)

[<Fact>]
let ``created, issued, accounting, due, posted and paid dates stay distinct`` () =
    let created = { context with When = DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero) }
    let issuing = { context with When = DateTimeOffset(2026, 10, 8, 16, 30, 0, TimeSpan.Zero) }
    let withDraft' = saveDraft created (draftFor [ consulting 34500L 17500L ]) books |> ok
    // Issued on the 8th, dated the 7th in the books.
    let issuedBooks, invoice = issue issuing request withDraft' |> ok
    let r = start issuedBooks |> recordPayment context (payment "PAY-1" 603750L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 603750L) |> ok
    let found = dates r invoice
    Assert.Equal(Some created.When, found.CreatedAt)
    Assert.Equal(issuing.When, found.IssuedAt)
    Assert.Equal(DateOnly(2026, 10, 7), found.AccountingDate)
    Assert.Equal(DateOnly(2026, 11, 6), found.DueDate)
    Assert.Equal(Some issuing.When, found.PostedAt)
    Assert.Equal(Some(DateOnly(2026, 10, 20)), found.PaidAt)
    Assert.Equal(None, (dates (start issuedBooks) invoice).PaidAt)
