/// Invoice generation, part 2 (WI-0027): delivery attempts, follow-up,
/// independent state dimensions and the actions they allow, the related-
/// document graph, partial credits, typed adjustments, payment profiles,
/// assumptions and approval reasons (INV-STATE, INV-DEL, INV-COR, INV-ADJ,
/// INV-PAYINST, INV-REV).
module Summa.Tests.LifecycleTests

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

let private extra =
    [ { Id = "credits"; Code = "2100"; Name = "Customer Credits"; Type = Liability; Active = true }
      { Id = "deposits"; Code = "2200"; Name = "Customer Deposits"; Type = Liability; Active = true }
      { Id = "salestax"; Code = "2300"; Name = "Sales Tax Payable"; Type = Liability; Active = true }
      { Id = "baddebt"; Code = "6500"; Name = "Bad Debt"; Type = Expense; Active = true } ]

let private accounts: Credits.ReceivableAccounts =
    { Cash = "cash"; Receivable = "ar"; CustomerCredits = "credits"; CustomerDeposits = "deposits"; BadDebt = "baddebt" }

let private baseBooks () =
    { books with Ledger = extra |> List.fold (fun l a -> addAccount context a l |> ok) books.Ledger }

/// INV-001 for 6,037.50, reviewed and issued with its artifacts.
let private issuedWith (draft: DraftInvoice) (req: IssueRequest) =
    start (saveDraft context draft (baseBooks ()) |> ok)
    |> submitForReview context req
    |> ok
    |> issueInvoice context req
    |> ok

let private issued () = issuedWith (draftFor [ consulting 34500L 17500L ]) request

let private to' address = { To = [ address ]; Cc = []; ReplyTo = None }

let private delivery id policy =
    { AttemptId = id
      InvoiceId = "INV-001"
      Recipients = to' "ap@abc.example"
      Channel = EmailChannel
      Policy = policy
      MessageTemplate = "invoice-email/1"
      RetryOf = None }

let private pdfStored (r: Receivables) =
    recordPdf context "INV-001" { Reference = "artifacts://test/INV-001.pdf"; Sha256 = String('b', 64); Size = 1024L; Renderer = "folio-p0" } r |> ok

[<Fact>]
let ``delivery is separate from issue: attempts move forward and never claim more than the provider said`` () =
    let r, _ = issued ()
    Assert.Equal(NotPrepared, deliveryState r "INV-001")
    Assert.Equal<DeliveryProblem list>([ PdfNotReady(Some ArtifactStatus.Pending) ], send context (delivery "DL-1" AttachPdf) r |> refused)
    let ready = pdfStored r
    Assert.Equal(ReadyToSend, deliveryState ready "INV-001")
    let queued = send context (delivery "DL-1" AttachPdf) ready |> ok
    Assert.Equal(Pending, deliveryState queued "INV-001")
    let accepted = queued |> recordOutcome context "DL-1" SentToProvider (Some "msg-1") |> ok |> recordOutcome context "DL-1" ProviderAccepted None |> ok
    // Accepted by the provider is Sent, not Delivered; it never goes backwards.
    Assert.Equal(Sent, deliveryState accepted "INV-001")
    Assert.Equal(Some "msg-1", accepted.Books.Deliveries["DL-1"].ProviderReference)
    Assert.Equal<DeliveryProblem list>([ OutcomeNotAllowed(ProviderAccepted, Queued) ], recordOutcome context "DL-1" Queued None accepted |> refused)
    let bounced = recordOutcome context "DL-1" (Bounced "mailbox full") None accepted |> ok
    Assert.Equal(BouncedBack, deliveryState bounced "INV-001")
    Assert.Equal<(string * string) list>([ "INV-001", "the last delivery bounced" ], attention bounced)
    // A resend is a new attempt for the same invoice: no new invoice, no new entry.
    let resent = resend context "DL-2" "DL-1" bounced |> ok
    Assert.Equal(Some "DL-1", resent.Books.Deliveries["DL-2"].RetryOf)
    Assert.True((bounced.Books.Invoices = resent.Books.Invoices))
    Assert.Equal(bounced.Books.Ledger.Entries.Count, resent.Books.Ledger.Entries.Count)
    Assert.Equal(DeliveredByProvider, deliveryState (recordOutcome context "DL-2" Delivered (Some "msg-2") resent |> ok) "INV-001")
    // Retrying the same attempt changes nothing.
    Assert.Equal(resent, resend context "DL-2" "DL-1" resent |> ok)

[<Fact>]
let ``a delivery needs valid recipients and a live invoice; a manual send is recorded as such`` () =
    let r, _ = issued ()
    Assert.Equal<DeliveryProblem list>([ InvalidRecipient "not an address" ], send context { delivery "DL-1" LinkOnly with Recipients = to' "not an address" } r |> refused)
    let manual = recordManual context "DL-M" "INV-001" (to' "ap@abc.example") "posted by mail" r |> ok
    Assert.Equal(SentManually, deliveryState manual "INV-001")
    Assert.Equal(Manual "posted by mail", manual.Books.Deliveries["DL-M"].Channel)
    let voidRequest: Corrections.VoidRequest = { InvoiceId = "INV-001"; Reason = "Duplicate"; Date = DateOnly(2026, 10, 8); JournalEntryId = "JE-VOID-1" }
    let voided = Corrections.voidInvoice context voidRequest r |> ok
    Assert.Equal<DeliveryProblem list>([ VoidedInvoice "INV-001" ], send context (delivery "DL-1" LinkOnly) voided |> refused)

[<Fact>]
let ``state dimensions are independent: disputed, overdue and partly paid at once, with the books untouched`` () =
    let r, invoice = issued ()
    let paid = r |> recordPayment context (payment "PAY-1" 100000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok
    let disputed = dispute context "INV-001" "Hours on 3 October questioned" (DateOnly(2026, 11, 10)) paid |> ok
    let chased = setCollection context "INV-001" (Reminded 2) disputed |> ok
    let v = view (DateOnly(2026, 11, 20)) chased invoice
    Assert.Equal(IssuedStage, v.Preparation)
    Assert.Equal(PartiallyPaid, v.Settlement)
    Assert.Equal(Overdue, v.Timing)
    Assert.Equal(NotPrepared, v.Delivery)
    Assert.Equal(Disputed("Hours on 3 October questioned", DateOnly(2026, 11, 10)), v.Dispute)
    Assert.Equal(Reminded 2, v.Collection)
    // Neither the dispute nor collection touched the invoice, its balance or the ledger.
    Assert.True((paid.Books.Invoices = chased.Books.Invoices))
    Assert.True((paid.Books.Ledger.Entries = chased.Books.Ledger.Entries))
    Assert.Equal(outstanding paid invoice, outstanding chased invoice)
    let resolved = resolveDispute context "INV-001" "Hours confirmed" (DateOnly(2026, 11, 12)) chased |> ok
    Assert.Equal(DisputeResolved("Hours confirmed", DateOnly(2026, 11, 12)), (followUp resolved "INV-001").Dispute)

/// Whether each listed action is exactly what the commands allow.
let private actionsMatchCommands (r: Receivables) =
    let invoice = r.Books.Invoices["INV-001"]
    let listed = actions r invoice
    let voidRequest: Corrections.VoidRequest = { InvoiceId = "INV-001"; Reason = "Check"; Date = DateOnly(2026, 10, 30); JournalEntryId = "JE-CHECK-VOID" }
    let canVoid = Corrections.voidInvoice context voidRequest r |> Result.isOk
    let writeOff: WriteOff = { Id = "WO-CHECK"; InvoiceId = "INV-001"; Amount = usd 1L; Reason = "Check"; Date = DateOnly(2026, 10, 30); JournalEntryId = "JE-CHECK-WO" }
    let canWriteOff = Credits.writeOff context accounts writeOff r |> Result.isOk
    let canDeliver = send context (delivery "DL-CHECK" LinkOnly) r |> Result.isOk

    let canPay =
        r
        |> recordPayment context (payment "PAY-CHECK" 1L)
        |> Result.bind (allocate context (allocation "AL-CHECK" "PAY-CHECK" "INV-001" 1L))
        |> Result.isOk

    Assert.Equal(canVoid, List.contains VoidIt listed)
    Assert.Equal(canWriteOff, List.contains WriteOffBalance listed)
    Assert.Equal(canDeliver, List.contains Deliver listed)
    Assert.Equal(canPay, List.contains RecordPaymentFor listed)

[<Fact>]
let ``the actions offered are exactly the transitions the commands allow, in every state`` () =
    let fresh, _ = issued ()
    let partly = fresh |> recordPayment context (payment "PAY-1" 100000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok
    let paid = fresh |> recordPayment context (payment "PAY-1" 603750L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 603750L) |> ok
    let voidRequest: Corrections.VoidRequest = { InvoiceId = "INV-001"; Reason = "Duplicate"; Date = DateOnly(2026, 10, 8); JournalEntryId = "JE-VOID-1" }
    let voided = Corrections.voidInvoice context voidRequest fresh |> ok
    let writeOff: WriteOff = { Id = "WO-1"; InvoiceId = "INV-001"; Amount = usd 603750L; Reason = "Uncollectible"; Date = DateOnly(2027, 3, 1); JournalEntryId = "JE-WO-1" }
    let writtenOff = Credits.writeOff context accounts writeOff fresh |> ok

    for state in [ fresh; partly; paid; voided; writtenOff ] do
        actionsMatchCommands state

    Assert.Equal<InvoiceAction list>([ Deliver; RecordPaymentFor; ApplyCredit; WriteOffBalance; CreateCreditMemo; OpenDispute; VoidIt ], actions fresh fresh.Books.Invoices["INV-001"])
    Assert.Equal<InvoiceAction list>([], actions voided voided.Books.Invoices["INV-001"])
    Assert.Equal<DraftAction list>([ EditDraft; DeleteDraft; SubmitForReview ], draftActions (draftFor []))
    Assert.Equal<DraftAction list>([ EditDraft; ReturnToDraft; Issue ], draftActions { draftFor [] with Review = SubmittedForReview 1 })

[<Fact>]
let ``corrections keep a graph of related documents`` () =
    let r, original = issued ()
    let memo: CreditMemo = { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some "INV-001"; Amount = usd 17500L; RevenueAccountId = "revenue"; Reason = "One hour not worked"; IssueDate = DateOnly(2026, 10, 9); JournalEntryId = "JE-CM-1"; Lines = [ 0 ] }
    let credited = Credits.issueCreditMemo context accounts memo r |> ok
    let refundRequest: Refund = { Id = "RF-1"; CustomerId = abc.Id; Source = FromCreditMemo "CM-1"; Amount = usd 17500L; Date = DateOnly(2026, 10, 10); Method = Ach; Reference = "OUT-1"; JournalEntryId = "JE-RF-1" }
    let refunded = Credits.refund context accounts refundRequest credited |> ok
    let paid = refunded |> recordPayment context (payment "PAY-1" 100000L) |> ok |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok
    Assert.Equal<Relation list>([ CreditedBy "CM-1"; PaidBy "PAY-1"; RefundOf "RF-1" ], related paid "INV-001")
    // Void and reissue: both directions are named.
    let voidRequest: Corrections.VoidRequest = { InvoiceId = "INV-001"; Reason = "Wrong rate"; Date = DateOnly(2026, 10, 8); JournalEntryId = "JE-VOID-1" }
    let voided, draft = Corrections.voidAndReissue context voidRequest "D-2" r |> ok
    let second = { request with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-000002"; ObligationId = "OBL-002" }
    let reissued, _ = voided |> submitForReview context second |> ok |> issueInvoice context second |> ok
    Assert.Equal<Relation list>([ ReissuedAs "INV-002"; VoidedBy "JE-VOID-1" ], related reissued original.InvoiceId)
    Assert.Equal<Relation list>([ ReissueOf "INV-001" ], related reissued "INV-002")
    Assert.Equal(Some "INV-001", draft.Corrects)

[<Fact>]
let ``a partial credit names the lines it credits and never more than they charged`` () =
    let r, _ = issuedWith (draftFor [ consulting 34500L 17500L; { consulting 2000L 9000L with Description = "Workshop" } ]) request
    let memo: CreditMemo = { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some "INV-001"; Amount = usd 18000L; RevenueAccountId = "revenue"; Reason = "Workshop cancelled"; IssueDate = DateOnly(2026, 10, 9); JournalEntryId = "JE-CM-1"; Lines = [ 1 ] }
    Assert.Equal<int list>([ 1 ], (Credits.issueCreditMemo context accounts memo r |> ok).CreditMemos["CM-1"].Lines)
    Assert.Equal<Credits.CreditProblem list>([ Credits.InvalidCreditLines "the credit is more than the credited lines charged" ], Credits.issueCreditMemo context accounts { memo with Amount = usd 18001L } r |> refused)
    Assert.Equal<Credits.CreditProblem list>([ Credits.InvalidCreditLines "a credited line does not exist or is named twice" ], Credits.issueCreditMemo context accounts { memo with Lines = [ 2 ] } r |> refused)

[<Fact>]
let ``a credit memo's document names the invoice and lines it credits, from the records alone`` () =
    let r, _ = issuedWith (draftFor [ consulting 34500L 17500L; { consulting 2000L 9000L with Description = "Workshop" } ]) request
    let memo: CreditMemo = { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some "INV-001"; Amount = usd 18000L; RevenueAccountId = "revenue"; Reason = "Workshop cancelled"; IssueDate = DateOnly(2026, 10, 9); JournalEntryId = "JE-CM-1"; Lines = [ 1 ] }
    let credited = Credits.issueCreditMemo context accounts memo r |> ok
    let invoice = credited.Books.Invoices["INV-001"]

    match CreditMemoDocuments.ofCreditMemo credited "CM-1" with
    | Error problem -> failwith $"%A{problem}"
    | Ok doc ->
        Assert.Equal((invoice.Number, invoice.IssueDate), doc.Credits)
        Assert.Equal(invoice.Issuer, doc.Issuer)
        Assert.Equal(invoice.Customer, doc.Customer)
        Assert.Equal<string list>([ "Workshop" ], doc.Lines |> List.map _.Description)
        Assert.Equal(usd 18000L, doc.Amount)

        match CreditMemoDocuments.render doc with
        | Error why -> failwith why
        | Ok html ->
            Assert.Contains("<ef-print-document>", html)
            Assert.Contains($"Credits invoice</dt><dd>{invoice.Number}", html)
            Assert.Contains("180.00 USD", html)
            // The same records render the same document: it is reproducible.
            Assert.Equal(Ok html, CreditMemoDocuments.render doc)

        Assert.Equal(Error "template summa.credit-memo 9.9.9 is not available", CreditMemoDocuments.render { doc with Template = { doc.Template with Version = "9.9.9" } })

    Assert.Equal(Error(CreditMemoDocuments.UnknownMemo "CM-9"), CreditMemoDocuments.ofCreditMemo credited "CM-9")
    let unattached = Credits.issueCreditMemo context accounts { memo with Id = "CM-2"; InvoiceId = None; Lines = []; JournalEntryId = "JE-CM-2" } credited |> ok
    Assert.Equal(Error(CreditMemoDocuments.NoInvoice "CM-2"), CreditMemoDocuments.ofCreditMemo unattached "CM-2")

[<Fact>]
let ``adjustments are typed and positive; a tax posts to its own liability account`` () =
    let taxed =
        { draftFor [ { consulting 10000L 10000L with Tax = Taxable "services" } ] with
            Adjustments =
                [ { Kind = Fee; Label = "Rush fee"; Amount = usd 5000L }
                  { Kind = Tax(taxCharge "NY-8.875" "salestax"); Label = "Sales tax"; Amount = usd 8875L } ] }

    let r, invoice = issuedWith taxed request
    Assert.Equal(usd (100000L + 5000L + 8875L), invoice.Total)
    let entry = r.Books.Ledger.Entries[invoice.JournalEntryId]
    Assert.Contains(entry.Lines, fun l -> l.AccountId = "salestax" && l.Side = Credit(usd 8875L))
    Assert.Contains(entry.Lines, fun l -> l.AccountId = "revenue" && l.Side = Credit(usd 105000L))
    let html = Documents.render (Documents.ofInvoice invoice) |> ok
    Assert.Contains("<th scope=\"row\">Sales tax</th>", html)
    // Reductions are discounts or credit memos, never negative adjustments; a tax needs a liability account.
    let negative = { draftFor [ consulting 10000L 10000L ] with Adjustments = [ { Kind = Surcharge; Label = "Goodwill"; Amount = usd -500L } ] }
    Assert.Equal<string list>([ "invalid-adjustment" ], blockers (start (saveDraft context negative (baseBooks ()) |> ok)) request |> List.map _.Code)
    let wrongAccount = { draftFor [ { consulting 10000L 10000L with Tax = Taxable "services" } ] with Adjustments = [ { Kind = Tax(taxCharge "X" "revenue"); Label = "Tax"; Amount = usd 100L } ] }
    Assert.Equal<string list>([ "invalid-adjustment" ], blockers (start (saveDraft context wrongAccount (baseBooks ()) |> ok)) request |> List.map _.Code)

[<Fact>]
let ``payment profiles are versioned, keep secrets out, and an issued invoice keeps what it showed`` () =
    let profile = { Id = "us-bank"; Version = 0; Label = "US bank"; Methods = [ "ACH"; "Check" ]; Instructions = "ACH: Example Bank, account ending 6789" }
    let withProfile = savePaymentProfile context profile (baseBooks ()) |> ok
    Assert.Equal(Some 1, latestProfile withProfile "us-bank" |> Option.map _.Version)
    Assert.True(Result.isError (savePaymentProfile context { profile with Instructions = "ACH account 123456789012" } withProfile))
    Assert.True(Result.isError (savePaymentProfile context { profile with Instructions = "Pay with token ghp_abcdefghijklmnopqrstuvwxyz0123456789" } withProfile))
    let customerBooks = withProfile |> saveCustomer context { abc with PaymentProfileId = Some "us-bank" }
    let r = start (saveDraft context (draftFor [ consulting 1000L 10000L ]) customerBooks |> ok) |> submitForReview context request |> ok
    let issued, invoice = issueInvoice context request r |> ok
    Assert.Equal(Some "us-bank@1", invoice.Issuer.PaymentProfile)
    Assert.Equal<string list>([ "ACH"; "Check" ], invoice.Issuer.PaymentMethods)
    Assert.Contains("<p>Accepted: ACH, Check</p>", Documents.render (Documents.ofInvoice invoice) |> ok)
    // A new version later changes nothing already issued.
    let changed = { issued with Books = savePaymentProfile context { profile with Instructions = "Wire only, account ending 1111" } issued.Books |> ok }
    Assert.Equal(Some 2, latestProfile changed.Books "us-bank" |> Option.map _.Version)
    Assert.Equal<(ArtifactKind * ArtifactCheck) list>([ InvoiceHtml, Intact; InvoiceJson, Intact ], verify changed invoice.InvoiceId)

[<Fact>]
let ``review shows assumptions; the approval keeps its reason; discounts keep theirs`` () =
    let prepared =
        { draftFor [ consulting 1000L 10000L ] with
            Assumptions = [ "Grouped Chrona time by project"; "Used the customer's 2026 rate" ]
            Discounts = [ { Label = "Loyalty"; Rule = Percent 500; Reason = Some "Approved by K. Miller" } ] }

    let r = start (saveDraft context prepared (baseBooks ()) |> ok)
    Assert.Equal<string list>(prepared.Assumptions, (review r "D-1" |> Option.get).Assumptions)
    let withReason = { request with ApprovalReason = Some "Within the agreed cap" }
    let _, invoice = r |> submitForReview context withReason |> ok |> issueInvoice context withReason |> ok
    Assert.Equal(Some "Within the agreed cap", invoice.Approval.Reason)
    Assert.Equal(Some "Approved by K. Miller", invoice.Discounts.Head.Reason)
    Assert.Equal<string list>(prepared.Assumptions, invoice.Assumptions)
