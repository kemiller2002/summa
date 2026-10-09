/// A small set of books to store: a chart of accounts, a customer, an
/// issued invoice, a payment allocated to it and a reversed manual entry.
module Summa.Storage.Tests.Books

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

let ledgerContext = { Who = "github:583231"; When = DateTimeOffset(2026, 10, 7, 12, 0, 0, 123, TimeSpan.Zero).AddTicks(4567L); Source = "summa-test"; CorrelationId = Some "c-1" }

let ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"%A{problems}"

let accounts =
    [ { Id = "cash"; Code = "1000"; Name = "Operating Cash"; Type = Asset; Active = true }
      { Id = "ar"; Code = "1100"; Name = "Accounts Receivable"; Type = Asset; Active = true }
      { Id = "equity"; Code = "3000"; Name = "Owner Equity"; Type = Equity; Active = true }
      { Id = "revenue"; Code = "4000"; Name = "Consulting Revenue"; Type = Revenue; Active = true }
      { Id = "software"; Code = "6100"; Name = "Software"; Type = Expense; Active = true }
      { Id = "credits"; Code = "2100"; Name = "Customer Credits"; Type = Liability; Active = true }
      { Id = "deposits"; Code = "2200"; Name = "Customer Deposits"; Type = Liability; Active = true }
      { Id = "baddebt"; Code = "6500"; Name = "Bad Debt"; Type = Expense; Active = true } ]

let chart = accounts |> List.fold (fun ledger a -> addAccount ledgerContext a ledger |> ok) empty

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

let draft =
    { DraftId = "D-1"
      CustomerId = abc.Id
      Currency = "USD"
      Lines =
        [ { Description = "Architecture assessment"
            QuantityThousandths = 34500L
            UnitPrice = usd 17500L
            RevenueAccountId = "revenue"
            Project = Some "PRJ-ARCH"
            WorkItem = None
            Discount = None
            Source = Summa.Ledger.Sources.ManualLine
            Rate = None
            Tax = NotAssessed } ]
      Adjustments = [ { Kind = Fee; Label = "Processing fee"; Amount = usd 1250L } ]
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

let issueRequest =
    { DraftId = "D-1"
      IssueDate = DateOnly(2026, 10, 7)
      NumberOverride = None
      Numbering = defaultNumbering "INV"
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

let payment =
    { Id = "PAY-1"
      CustomerId = abc.Id
      DateReceived = DateOnly(2026, 10, 20)
      Amount = usd 605000L
      Method = Ach
      Reference = "ACH-1"
      Memo = Some "October"; }

let allocation =
    { AllocationId = "AL-1"
      PaymentId = "PAY-1"
      InvoiceId = "INV-001"
      Amount = usd 605000L
      JournalEntryId = "JE-000002"
      CashAccountId = "cash"
      ReceivableAccountId = "ar" }

let manual =
    { Date = DateOnly(2026, 10, 8)
      Description = "Software subscription"
      Lines =
        [ { AccountId = "software"; Side = Debit(usd 9900L); Memo = None; Dimensions = noDimensions }
          { AccountId = "cash"; Side = Credit(usd 9900L); Memo = None; Dimensions = { noDimensions with Project = Some "OPS" } } ]
      Source = "manual" }

/// The whole slice in memory.
let full () =
    let books = openBooks chart |> saveCustomer ledgerContext abc |> saveDraft ledgerContext draft |> ok
    let books, _ = issue ledgerContext issueRequest books |> ok
    let books = markSent ledgerContext "INV-001" "ap@abc.example" books |> ok
    let ledger, _ = post ledgerContext "manual-1" "JE-000003" manual books.Ledger |> ok
    let ledger, _ = reverse ledgerContext "JE-000003" "JE-000004" (DateOnly(2026, 10, 9)) ledger |> ok
    let ledger = closePeriod ledgerContext (2026, 9) ledger

    start { books with Ledger = ledger }
    |> recordPayment ledgerContext payment
    |> ok
    |> allocate ledgerContext allocation
    |> ok

let receivableAccounts: Summa.Ledger.Credits.ReceivableAccounts =
    { Cash = "cash"
      Receivable = "ar"
      CustomerCredits = "credits"
      CustomerDeposits = "deposits"
      BadDebt = "baddebt" }

/// The slice plus every v0.2 receivables record: an overpayment credit, a
/// deposit, a credit memo, applications, a refund, a reversed payment and a
/// write-off.
let withCredits () =
    let open' = Summa.Ledger.Credits.creditUnapplied
    let day = DateOnly(2026, 10, 25)
    let r = full ()
    let r = recordPayment ledgerContext { payment with Id = "PAY-2"; Amount = usd 10000L; Reference = "ACH-2" } r |> ok
    let r = open' ledgerContext receivableAccounts { CreditId = "CR-1"; PaymentId = "PAY-2"; Date = day; JournalEntryId = "JE-CR-1" } r |> ok
    let r = Summa.Ledger.Credits.recordDeposit ledgerContext receivableAccounts { Id = "DEP-1"; CustomerId = abc.Id; DateReceived = day; Amount = usd 50000L; Method = Wire; Reference = "W-1"; JournalEntryId = "JE-DEP-1" } r |> ok
    let r = Summa.Ledger.Credits.issueCreditMemo ledgerContext receivableAccounts { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some "INV-001"; Amount = usd 2000L; RevenueAccountId = "revenue"; Reason = "Goodwill"; IssueDate = day; JournalEntryId = "JE-CM-1"; Lines = [] } r |> ok
    let r = Summa.Ledger.Credits.refund ledgerContext receivableAccounts { Id = "RF-1"; CustomerId = abc.Id; Source = FromCredit "CR-1"; Amount = usd 4000L; Date = day; Method = Ach; Reference = "OUT-1"; JournalEntryId = "JE-RF-1" } r |> ok
    let r = recordPayment ledgerContext { payment with Id = "PAY-3"; Amount = usd 7000L; Reference = "CHK-3"; Method = Check } r |> ok
    Summa.Ledger.Credits.reversePayment ledgerContext receivableAccounts { PaymentId = "PAY-3"; Reason = "Bounced check"; Date = day; JournalEntryPrefix = "JE-REV-3" } r |> ok

/// A customer without default terms, so the system terms apply.
let xyz = { abc with Id = "CUST-XYZ"; Name = "XYZ Ltd"; Email = "ap@xyz.example"; DefaultTerms = None }

/// INV-002 for XYZ with line and invoice discounts, issued under the system
/// terms, before it is voided.
let withDiscountedInvoice () =
    let r = full ()

    let discounted =
        { draft with
            DraftId = "D-2"
            CustomerId = xyz.Id
            Lines = [ { draft.Lines.Head with Discount = Some(Percent 1000) } ]
            Adjustments = []
            Discounts = [ { Label = "Loyalty"; Rule = Fixed(usd 5000L); Reason = None } ] }

    let books = r.Books |> saveCustomer ledgerContext xyz |> saveDraft ledgerContext discounted |> ok
    let second = { issueRequest with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-INV-2"; ObligationId = "OBL-002"; SystemTerms = DueOnReceipt }
    let books, _ = issue ledgerContext second books |> ok
    { r with Books = books }

/// INV-002 voided and reissued as draft D-3, which names it.
let withCorrections () =
    let request: Summa.Ledger.Corrections.VoidRequest = { InvoiceId = "INV-002"; Reason = "Wrong discount"; Date = DateOnly(2026, 10, 9); JournalEntryId = "JE-VOID-2" }
    Summa.Ledger.Corrections.voidAndReissue ledgerContext request "D-3" (withDiscountedInvoice ()) |> ok |> fst

/// Published time for ABC, as Summa snapshots it.
let sourceTime (publication: string) (activity: string) (revision: int) : Summa.Ledger.Sources.SourceTime =
    { PublicationId = publication
      OrganizationId = "org-1"
      ActivityId = activity
      Revision = revision
      PerformerId = "github:1"
      BusinessDate = DateOnly(2026, 10, 6)
      ProjectId = "PRJ-ARCH"
      ClientId = Some abc.Id
      EngagementId = Some "ENG-1"
      ActivityTypeId = "consulting"
      Description = "Assessment interviews"
      ExactMinutes = 95
      BillableMinutes = 90
      Approved = true
      RateReference = Some "senior"
      Origin = Summa.Ledger.Sources.OriginKnown("imported:toggl", Some "toggl:obs-1", Some "EXE-1")
      Lineage = [ "act-0" ]
      WorkItem = Some "WI-0042"
      Supersedes = None
      PublishedAt = DateTimeOffset(2026, 10, 7, 11, 0, 0, TimeSpan.Zero) }

/// The slice plus billing: a rate card, an engagement with milestones, an
/// expense, imported time, an accepted proposal (INV-002), a correction
/// that raised a review, a withdrawal and an abandoned proposal.
let withBilling () =
    let open' = Summa.Ledger.Billing.importTime
    let accounts: Summa.Ledger.Billing.BillingAccounts = { TimeRevenue = "revenue"; FeeRevenue = "revenue"; ReimbursedExpenses = "revenue" }
    let r = full ()

    let card: Summa.Ledger.Sources.RateCard =
        { Rates = [ Summa.Ledger.Sources.Everyone, usd 15000L; Summa.Ledger.Sources.ForRole "senior", usd 18000L ]
          Roles = Map.ofList [ "github:1", "senior" ] }

    let engagement =
        { Id = "ENG-1"
          CustomerId = abc.Id
          Name = "Architecture Assessment"
          Currency = "USD"
          FixedFee = Some(usd 1500000L)
          Milestones =
            [ { Id = "kickoff"; Label = "Kickoff"; Amount = ShareOfFee 3000; CompletedOn = None }
              { Id = "report"; Label = "Report"; Amount = MilestoneFixed(usd 200000L); CompletedOn = None } ]
          Terms = Some(Net 15) }

    let books =
        r.Books
        |> Summa.Ledger.Billing.saveRateCard ledgerContext card
        |> ok
        |> Summa.Ledger.Billing.saveEngagement ledgerContext engagement
        |> ok
        |> Summa.Ledger.Billing.completeMilestone ledgerContext "ENG-1" "kickoff" (DateOnly(2026, 10, 5))
        |> ok

    let expense: Summa.Ledger.Sources.Expense =
        { Id = "EXP-1"
          Date = DateOnly(2026, 10, 3)
          Description = "Flight"
          Amount = usd 42000L
          ExpenseAccountId = "software"
          PaidFromAccountId = "cash"
          CustomerId = Some abc.Id
          ProjectId = Some "PRJ-ARCH"
          EngagementId = Some "ENG-1"
          Billable = true
          JournalEntryId = "JE-EXP-1" }

    let r = { r with Books = books } |> Summa.Ledger.Billing.recordExpense ledgerContext expense |> ok
    let r = open' ledgerContext (sourceTime "pub-1" "act-1" 1) r |> ok |> fst
    let r = open' ledgerContext { sourceTime "pub-2" "act-2" 1 with Origin = Summa.Ledger.Sources.OriginUnknown } r |> ok |> fst

    let proposal: Summa.Ledger.Billing.ProposalRequest =
        { ProposalId = "P-1"
          CustomerId = abc.Id
          EngagementId = Some "ENG-1"
          Currency = "USD"
          Time = [ "pub-1" ]
          Grouping = [ Summa.Ledger.Sources.ByProject; Summa.Ledger.Sources.ByServiceMonth ]
          FixedFee = false
          Milestones = [ "kickoff" ]
          Expenses = [ "EXP-1" ]
          Manual = []
          Accounts = accounts }

    let issuing = { issueRequest with DraftId = "PD-1"; InvoiceId = "INV-002"; JournalEntryId = "JE-INV-2"; ObligationId = "OBL-002" }

    let r =
        r
        |> Summa.Ledger.Billing.propose ledgerContext proposal
        |> ok
        |> Summa.Ledger.Billing.overrideRate ledgerContext "P-1" 0 (usd 19000L) "Agreed rate"
        |> ok
        |> Summa.Ledger.Billing.markReady ledgerContext false "P-1"
        |> ok
        |> Summa.Ledger.Billing.accept ledgerContext "P-1" issuing
        |> ok
        |> fst

    let r = open' ledgerContext { sourceTime "pub-1b" "act-1" 2 with Supersedes = Some "pub-1" } r |> ok |> fst
    let withdrawal: Summa.Ledger.Sources.TimeWithdrawal = { PublicationId = "pub-2"; Revision = 2; Reason = "no longer billable"; WithdrawnAt = DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero) }
    let r = Summa.Ledger.Billing.withdrawTime ledgerContext withdrawal r |> ok
    let manual = { draft.Lines.Head with Description = "Workshop" }

    r
    |> Summa.Ledger.Billing.propose ledgerContext { proposal with ProposalId = "P-2"; Time = []; Milestones = []; Expenses = []; Manual = [ manual ] }
    |> ok
    |> Summa.Ledger.Billing.abandon ledgerContext "P-2"
    |> ok

/// The slice plus delivery attempts, a payment profile, follow-up and a
/// draft with typed adjustments, assumptions and recipients.
let withLifecycle () =
    let r = withCredits ()
    let profile: PaymentProfile = { Id = "us-bank"; Version = 0; Label = "US bank"; Methods = [ "ACH" ]; Instructions = "ACH, account ending 6789" }
    let books = Summa.Ledger.Issuance.savePaymentProfile ledgerContext profile r.Books |> ok

    let prepared =
        { draft with
            DraftId = "D-9"
            Adjustments = [ { Kind = Tax(taxCharge "NY" "credits"); Label = "Sales tax"; Amount = usd 500L }; { Kind = Surcharge; Label = "Rush"; Amount = usd 100L } ]
            Assumptions = [ "Grouped by project" ]
            Recipients = Some { To = [ "ap@abc.example" ]; Cc = [ "cfo@abc.example" ]; ReplyTo = Some "billing@echelon.example" } }

    let books = saveDraft ledgerContext prepared books |> ok
    let r = { r with Books = books }

    let request: Summa.Ledger.Lifecycle.DeliveryRequest =
        { AttemptId = "DL-1"; InvoiceId = "INV-001"; Recipients = { To = [ "ap@abc.example" ]; Cc = []; ReplyTo = None }; Channel = SecureLink; Policy = LinkOnly; MessageTemplate = "invoice-email/1"; RetryOf = None }

    r
    |> Summa.Ledger.Lifecycle.send ledgerContext request
    |> ok
    |> Summa.Ledger.Lifecycle.recordOutcome ledgerContext "DL-1" (DeliveryFailed "smtp 550") (Some "msg-1")
    |> ok
    |> Summa.Ledger.Lifecycle.dispute ledgerContext "INV-001" "Rate questioned" (DateOnly(2026, 10, 21))
    |> ok
    |> Summa.Ledger.Lifecycle.setCollection ledgerContext "INV-001" (Escalated "call the CFO")
    |> ok
