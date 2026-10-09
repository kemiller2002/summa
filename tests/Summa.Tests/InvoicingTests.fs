/// Invoices and receivables (WI-0013): v0.1 §3-6, §8-10 and §19.
module Summa.Tests.InvoicingTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Tests.LedgerTests

let abc =
    { Id = "CUST-ABC"
      Name = "ABC Corp"
      BillingName = "ABC Corp Accounts Payable"
      BillingAddress = "1 Main St"
      Email = "ap@abc.example"
      DefaultTerms = Some(Net 30)
      PaymentProfileId = None
      Active = true
      Tax = TaxNotAssessed }

let consulting hours rate =
    { Description = "Architecture assessment"
      QuantityThousandths = hours
      UnitPrice = usd rate
      RevenueAccountId = "revenue"
      Project = Some "PRJ-ARCH"
      WorkItem = None
      Discount = None
      Source = Summa.Ledger.Sources.ManualLine
      Rate = None
      Tax = NotAssessed }

let draftFor lines =
    { DraftId = "D-1"
      CustomerId = abc.Id
      Currency = "USD"
      Lines = lines
      Adjustments = []
      Discounts = []
      Terms = None
      DueDate = None
      Corrects = None
      EngagementId = None
      Details = noDetails
      Assumptions = []
      Recipients = None
      Version = 0
      Review = Editing }

let request =
    { DraftId = "D-1"
      IssueDate = DateOnly(2026, 10, 7)
      NumberOverride = None
      Numbering = defaultNumbering "EF"
      ExpectedVersion = None
      Issuer =
        { LegalName = "Echelon Foundry LLC"
          Address = "1 Foundry Way\nSpringfield"
          TaxId = Some "12-3456789"
          Email = "billing@echelon.example"
          PaymentInstructions = "ACH to account ending 6789"
          PaymentMethods = []
          PaymentProfile = None }
      Template = Summa.Ledger.Documents.currentTemplate
      ApprovalReason = None
      SystemTerms = Net 30
      ReceivableAccountId = "ar"
      InvoiceId = "INV-001"
      JournalEntryId = "JE-000001"
      ObligationId = "OBL-001" }

let books = openBooks chart |> saveCustomer context abc

let withDraft draft = saveDraft context draft books |> ok

let issued () =
    // 34.5 hours at 175.00 = 6,037.50; a 12.50 fee brings it to 6,050.00.
    let draft = { draftFor [ consulting 34500L 17500L ] with Adjustments = [ { Kind = Fee; Label = "Processing fee"; Amount = usd 1250L } ] }
    issue context request (withDraft draft) |> ok

[<Fact>]
let ``issuing posts AR and revenue as one operation, with an obligation`` () =
    let books, invoice = issued ()
    Assert.Equal("EF-2026-0001", invoice.Number)
    Assert.Equal(usd 605000L, invoice.Total)
    Assert.Equal(DateOnly(2026, 11, 6), invoice.DueDate) // Net 30 from the customer
    let entry = books.Ledger.Entries[invoice.JournalEntryId]
    Assert.Equal(Debit(usd 605000L), entry.Lines.Head.Side)
    Assert.Equal(usd 605000L, balance books.Ledger "USD" request.IssueDate "ar")
    Assert.Equal(usd 605000L, balance books.Ledger "USD" request.IssueDate "revenue")
    Assert.Equal(Some "CUST-ABC", entry.Lines[1].Dimensions.Client)
    Assert.Equal(usd 605000L, books.Obligations["OBL-001"].OriginalAmount)
    Assert.True(issuedInvoicesHaveEntries books)
    Assert.False(books.Drafts.ContainsKey "D-1")

[<Fact>]
let ``retrying an issue five times leaves one invoice, one obligation and one entry`` () =
    let once, first = issued ()
    let after = [ 1..5 ] |> List.fold (fun b _ -> issue context request b |> ok |> fst) once
    Assert.Equal(1, after.Invoices.Count)
    Assert.Equal(1, after.Obligations.Count)
    Assert.Equal(1, after.Ledger.Entries.Count)
    Assert.Equal(first, (issue context request after |> ok |> snd))

[<Fact>]
let ``numbers are sequential per year, overrides must be unique, and issued numbers never change`` () =
    let books, _ = issued ()
    let second = { request with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-000002"; ObligationId = "OBL-002" }
    let withSecond = saveDraft context { draftFor [ consulting 1000L 10000L ] with DraftId = "D-2" } books |> ok
    let books2, invoice2 = issue context second withSecond |> ok
    Assert.Equal("EF-2026-0002", invoice2.Number)
    Assert.Equal("EF-2027-0001", nextNumber (defaultNumbering "EF") (DateOnly(2027, 1, 4)) books2)
    let third = { second with DraftId = "D-3"; InvoiceId = "INV-003"; JournalEntryId = "JE-000003"; ObligationId = "OBL-003"; NumberOverride = Some "EF-2026-0001" }
    let withThird = saveDraft context { draftFor [ consulting 1000L 10000L ] with DraftId = "D-3" } books2 |> ok
    Assert.Equal<InvoiceProblem list>([ DuplicateInvoiceNumber "EF-2026-0001" ], issue context third withThird |> refused)
    // An issued invoice is no longer an editable draft.
    Assert.Equal<InvoiceProblem list>([ AlreadyIssued "INV-001" ], saveDraft context (draftFor []) books |> refused)

[<Fact>]
let ``an invoice that cannot be issued says why, and nothing is posted`` () =
    let bad = { draftFor [ { consulting 1000L 0L with RevenueAccountId = "cash" } ] with CustomerId = "CUST-ABC" }
    let books1 = withDraft bad
    Assert.Equal<InvoiceProblem list>([ NoSubstantiveLine; TotalNotPositive; InvalidRevenueAccount "cash" ], issue context request books1 |> refused)
    Assert.Empty(books1.Ledger.Entries)
    Assert.Equal<InvoiceProblem list>([ NoLines; TotalNotPositive ], issue context request (withDraft (draftFor [])) |> refused)
    let inactive = books |> saveCustomer context { abc with Active = false }
    let withInactive = saveDraft context (draftFor [ consulting 1000L 100L ]) inactive |> ok
    Assert.Equal<InvoiceProblem list>([ InactiveCustomer "CUST-ABC" ], issue context request withInactive |> refused)
    Assert.Equal<InvoiceProblem list>([ UnknownDraft "D-9" ], issue context { request with DraftId = "D-9" } books |> refused)

[<Fact>]
let ``issuing into a closed period posts nothing and issues nothing`` () =
    let closed = { withDraft (draftFor [ consulting 1000L 100L ]) with Ledger = closePeriod context (2026, 10) books.Ledger }
    match issue context request closed |> refused with
    | [ LedgerProblems [ PeriodNotOpen(2026, 10) ] ] -> ()
    | other -> failwith $"{other}"

[<Fact>]
let ``due dates come from terms; days due and past due are calculated`` () =
    let issueDate = DateOnly(2026, 10, 7)
    Assert.Equal(issueDate, dueDate issueDate DueOnReceipt)
    Assert.Equal<DateOnly list>([ DateOnly(2026, 10, 14); DateOnly(2026, 10, 22); DateOnly(2026, 11, 21); DateOnly(2026, 12, 6) ], [ 7; 15; 45; 60 ] |> List.map (fun d -> dueDate issueDate (Net d)))
    Assert.Equal(DateOnly(2026, 12, 1), dueDate issueDate (CustomDate(DateOnly(2026, 12, 1))))
    Assert.Equal((5, 0), (daysUntilDue (DateOnly(2026, 11, 1)) (DateOnly(2026, 11, 6)), daysPastDue (DateOnly(2026, 11, 1)) (DateOnly(2026, 11, 6))))
    Assert.Equal((0, 3), (daysUntilDue (DateOnly(2026, 11, 9)) (DateOnly(2026, 11, 6)), daysPastDue (DateOnly(2026, 11, 9)) (DateOnly(2026, 11, 6))))
    // An explicit due date or invoice terms override the customer's default.
    let books1 = withDraft { draftFor [ consulting 1000L 100L ] with Terms = Some(Net 15) }
    Assert.Equal(DateOnly(2026, 10, 22), (issue context request books1 |> ok |> snd).DueDate)

[<Fact>]
let ``sending is recorded with time and recipient, and every step is audited`` () =
    let books, _ = issued ()
    let sent = markSent { context with When = DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero) } "INV-001" "ap@abc.example" books |> ok
    let attempt = sent.Deliveries["INV-001-sent-1"]
    Assert.Equal((Some "ap@abc.example", DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero), ManuallySent), (List.tryHead attempt.Recipients.To, attempt.At, attempt.Outcome))
    let actions = sent.Ledger.Audit |> List.map _.What |> List.filter (fun w -> w.StartsWith "invoice" || w.StartsWith "customer")
    Assert.Equal<string list>([ "customer-saved"; "invoice-created"; "invoice-issued"; "invoice-sent" ], actions)
