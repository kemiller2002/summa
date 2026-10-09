/// Summa's books as Arca records (SUM0-012..014, SUM0-027, SUM0-044):
/// one object or event per file, deterministic paths, canonical content,
/// posted records immutable, derived values recomputed.
module Summa.Storage.Tests.FinancialRecordsTests

open Xunit
open Arca
open Summa.Ledger.Ledger
open Summa.Ledger.Payments
open Summa.Ledger.Reports
open Summa.Storage
open Summa.Storage.FinancialRecords
open Summa.Ledger.Money
open Summa.Storage.Tests.Books

let private stored (r: Receivables) =
    toRecords r
    |> Result.bind contents
    |> ok
    |> Map.toList
    |> List.mapi (fun i (p, (_, content)) ->
        ({ Path = RelativePath.parse p |> Result.defaultWith (failwithf "%A")
           Content = content
           Revision = Revision $"r{i}" }: StoredObject))

[<Fact>]
let ``every financial object and event is its own file at a deterministic path`` () =
    let paths = stored (full ()) |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

    for expected in
        [ "records/summa.account/cash.json"
          "records/summa.period/2026-09.json"
          "records/summa.entry/2026/10/JE-000001.json"
          "records/summa.entry/2026/10/JE-000004.json"
          "records/summa.customer/CUST-ABC.json"
          "records/summa.invoice/2026/INV-001.json"
          "records/summa.delivery/INV-001-sent-1.json"
          "records/summa.obligation/OBL-001.json"
          "records/summa.payment/2026/PAY-1.json"
          "records/summa.allocation/AL-1.json" ] do
        Assert.Contains(expected, paths)

    // The issued draft is gone; audit events are one file each.
    Assert.DoesNotContain("records/summa.draft/D-1.json", paths)
    let audits = paths |> Set.filter (fun p -> p.StartsWith "records/summa.audit/2026/10/")
    Assert.Equal((full ()).Books.Ledger.Audit.Length, audits.Count)
    // The same books always give the same files and bytes.
    Assert.True((stored (full ()) = stored (full ())))

[<Fact>]
let ``loading what was stored gives back the same books, with derived values recomputed`` () =
    let original = full ()
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let r = loaded.State
    Assert.Equal<Map<string, Account>>(original.Books.Ledger.Accounts, r.Books.Ledger.Accounts)
    Assert.Equal<Map<string, PostedEntry>>(original.Books.Ledger.Entries, r.Books.Ledger.Entries)
    Assert.Equal<Map<string, string>>(original.Books.Ledger.Keys, r.Books.Ledger.Keys)
    Assert.True((original.Books.Invoices = r.Books.Invoices))
    Assert.True((original.Books.Obligations = r.Books.Obligations))
    Assert.True((original.Books.IssuedFrom = r.Books.IssuedFrom))
    Assert.True((original.Payments = r.Payments))
    Assert.Equal<Allocation list>(original.Allocations, r.Allocations)
    Assert.True((Set.ofList original.Books.Ledger.Audit = Set.ofList r.Books.Ledger.Audit))
    Assert.Equal(Closed, r.Books.Ledger.Periods[(2026, 9)])
    // "Reversed" is derived from the reversal, not stored on the original.
    Assert.Equal(Reversed "JE-000004", r.Books.Ledger.Entries["JE-000003"].State)
    // The books still balance and the invoice is still Paid.
    let tb = trialBalance "USD" (System.DateOnly(2026, 10, 31)) r.Books.Ledger
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)
    Assert.Equal(Paid, status r r.Books.Invoices["INV-001"])

[<Fact>]
let ``posted records are immutable envelopes; mutable ones say so`` () =
    let byPath = stored (full ()) |> List.map (fun o -> RelativePath.render o.Path, o.Content) |> Map.ofList
    Assert.Contains("\"mutability\":\"immutable\"", byPath["records/summa.entry/2026/10/JE-000001.json"])
    Assert.Contains("\"mutability\":\"immutable\"", byPath["records/summa.invoice/2026/INV-001.json"])
    Assert.Contains("\"mutability\":\"immutable\"", byPath["records/summa.payment/2026/PAY-1.json"])
    Assert.Contains("\"mutability\":\"mutable\"", byPath["records/summa.customer/CUST-ABC.json"])
    // The reversed original's file is byte-for-byte what was first written.
    Assert.DoesNotContain("Reversed", byPath["records/summa.entry/2026/10/JE-000003.json"])

[<Fact>]
let ``stored content is untrusted: tampering, misplacement and broken books are reported`` () =
    let objects = stored (full ())
    let at path = objects |> List.findIndex (fun o -> RelativePath.render o.Path = path)

    let replace path (f: string -> string) =
        objects |> List.mapi (fun i o -> if i = at path then { o with Content = f o.Content } else o)

    // An entry that no longer balances.
    let unbalanced = replace "records/summa.entry/2026/10/JE-000001.json" (fun c -> let i = c.IndexOf "\"minor\":605000" in c.Substring(0, i) + "\"minor\":605001" + c.Substring(i + 15))
    Assert.NotEmpty (load unbalanced).Problems

    // An invoice whose content names another id than its file.
    let misnamed = replace "records/summa.invoice/2026/INV-001.json" (fun c -> c.Replace("\"invoiceId\":\"INV-001\"", "\"invoiceId\":\"INV-999\""))
    Assert.NotEmpty (load misnamed).Problems

    // An allocation whose payment is missing.
    let orphan = objects |> List.filter (fun o -> RelativePath.render o.Path <> "records/summa.payment/2026/PAY-1.json")
    Assert.Contains((load orphan).Problems, fun p -> (Diagnostics.describe p).Contains "payment PAY-1 is missing")

    // An unknown field (for example a token someone added) is refused.
    let extra = replace "records/summa.customer/CUST-ABC.json" (fun c -> c.Replace("\"active\":true", "\"active\":true,\"token\":\"x\""))
    Assert.NotEmpty (load extra).Problems

[<Fact>]
let ``every v0.2 receivables record round-trips and the books stay sound`` () =
    let original = withCredits ()
    Assert.Empty(Invariants.check original)
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let r = loaded.State
    Assert.True((original.Credits = r.Credits))
    Assert.True((original.Deposits = r.Deposits))
    Assert.True((original.CreditMemos = r.CreditMemos))
    Assert.True((original.Refunds = r.Refunds))
    Assert.True((original.Reversals = r.Reversals))
    Assert.True((original.WriteOffs = r.WriteOffs))
    Assert.True((Set.ofList original.Applications = Set.ofList r.Applications))
    Assert.Equal(Some(usd 6000L), Summa.Ledger.Credits.remaining r (FromCredit "CR-1"))
    let paths = stored original |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

    for expected in
        [ "records/summa.credit/CR-1.json"
          "records/summa.deposit/2026/DEP-1.json"
          "records/summa.credit-memo/2026/CM-1.json"
          "records/summa.refund/2026/RF-1.json"
          "records/summa.payment-reversal/PAY-3.json" ] do
        Assert.Contains(expected, paths)

[<Fact>]
let ``discounts, terms sources, voids and corrections round-trip and the books stay sound`` () =
    let original = withCorrections ()
    Assert.Empty(Invariants.check original)
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let r = loaded.State
    Assert.True((original.Voids = r.Voids))
    Assert.True((original.Books.Invoices["INV-002"] = r.Books.Invoices["INV-002"]))
    Assert.True((original.Books.Drafts = r.Books.Drafts))
    Assert.True((original.Books.Customers = r.Books.Customers))
    Assert.Equal(Summa.Ledger.Invoicing.SystemTerms, r.Books.Invoices["INV-002"].TermsSource)
    Assert.Equal(Some "INV-002", r.Books.Drafts["D-3"].Corrects)
    Assert.Equal(Voided, status r r.Books.Invoices["INV-002"])
    Assert.Contains("records/summa.invoice-void/INV-002.json", stored original |> List.map (fun o -> RelativePath.render o.Path))

[<Fact>]
let ``a void writes new records and cancels the obligation; it never rewrites the invoice`` () =
    let before = stored (withDiscountedInvoice ())
    let seen = before |> List.map (fun o -> let p = RelativePath.render o.Path in p, ({ Path = p; Revision = o.Revision; Content = o.Content }: Seen)) |> Map.ofList
    let wanted = withCorrections () |> toRecords |> Result.bind contents |> ok
    let found = Commands.changes seen wanted |> ok

    let touched =
        found
        |> List.map (function
            | Change.Create(p, _) -> "create", RelativePath.render p
            | Change.Update(p, _, _) -> "update", RelativePath.render p
            | Change.Delete(p, _) -> "delete", RelativePath.render p)

    Assert.Contains(("update", "records/summa.obligation/OBL-002.json"), touched)
    Assert.Contains(("create", "records/summa.invoice-void/INV-002.json"), touched)
    Assert.Contains(("create", "records/summa.entry/2026/10/JE-VOID-2.json"), touched)
    Assert.Contains(("create", "records/summa.draft/D-3.json"), touched)
    Assert.DoesNotContain(touched, fun (_, p) -> p.StartsWith "records/summa.invoice/")

[<Fact>]
let ``a void whose receivable is still open is an integrity failure`` () =
    let objects = stored (withCorrections ())
    let path = "records/summa.obligation/OBL-002.json"
    let reopened = objects |> List.map (fun o -> if RelativePath.render o.Path = path then { o with Content = o.Content.Replace("\"cancelled\":true", "\"cancelled\":false") } else o)
    Assert.Contains((load reopened).Problems, fun p -> (Diagnostics.describe p).Contains "its obligation is not cancelled")

[<Fact>]
let ``billing records round-trip: rates, engagements, expenses, time, proposals, reviews and line provenance`` () =
    let original = withBilling ()
    Assert.Empty(Invariants.check original)
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let books = loaded.State.Books
    Assert.True((original.Books.Rates = books.Rates))
    Assert.True((original.Books.Engagements = books.Engagements))
    Assert.True((original.Books.Expenses = books.Expenses))
    Assert.True((original.Books.Time = books.Time))
    Assert.True((original.Books.Withdrawals = books.Withdrawals))
    Assert.True((original.Books.Proposals = books.Proposals))
    Assert.True((original.Books.Reviews = books.Reviews))
    Assert.True((original.Books.Invoices["INV-002"] = books.Invoices["INV-002"]))
    let invoice = books.Invoices["INV-002"]
    Assert.Equal(Some "ENG-1", invoice.EngagementId)
    Assert.Equal(Summa.Ledger.Invoicing.EngagementTerms, invoice.TermsSource)
    Assert.Equal(Some Summa.Ledger.Sources.InvoiceOverride, invoice.Lines.Head.Rate |> Option.map _.RateSource)
    let paths = stored original |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

    for expected in
        [ "records/summa.rate-card/rates.json"
          "records/summa.engagement/ENG-1.json"
          "records/summa.expense/2026/EXP-1.json"
          "records/summa.time/2026/pub-1.json"
          "records/summa.time-withdrawal/pub-2.json"
          "records/summa.proposal/P-1.json"
          "records/summa.billing-review/review-pub-1.json" ] do
        Assert.Contains(expected, paths)

[<Fact>]
let ``billing the same source on two live invoices is an integrity failure`` () =
    let r = withBilling ()
    let first = r.Books.Invoices["INV-001"]
    let doubled = { first with Lines = [ { first.Lines.Head with Source = Summa.Ledger.Sources.ExpenseSource "EXP-1" } ] }
    let broken = { r with Books = { r.Books with Invoices = r.Books.Invoices.Add("INV-001", doubled) } }
    Assert.Contains(Invariants.check broken, fun v -> v.Rule = "no-double-billing" && v.Subject = "expense:EXP-1")
    let unknown = { first with Lines = [ { first.Lines.Head with Source = Summa.Ledger.Sources.TimeSource [ { PublicationId = "pub-404"; ActivityId = "act-404"; Revision = 1; Minutes = 60 } ] } ] }
    let orphan = { r with Books = { r.Books with Invoices = r.Books.Invoices.Add("INV-001", unknown) } }
    Assert.Contains(Invariants.check orphan, fun v -> v.Rule = "sources-exist")

[<Fact>]
let ``deliveries, payment profiles, follow-up and typed draft details round-trip`` () =
    let original = withLifecycle ()
    Assert.Empty(Invariants.check original)
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let books = loaded.State.Books
    Assert.True((original.Books.Deliveries = books.Deliveries))
    Assert.True((original.Books.PaymentProfiles = books.PaymentProfiles))
    Assert.True((original.Books.FollowUps = books.FollowUps))
    Assert.True((original.Books.Drafts = books.Drafts))
    Assert.True((original.CreditMemos = loaded.State.CreditMemos))
    let paths = stored original |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

    for expected in [ "records/summa.delivery/DL-1.json"; "records/summa.payment-profile/us-bank-v1.json"; "records/summa.follow-up/INV-001.json" ] do
        Assert.Contains(expected, paths)

[<Fact>]
let ``a stored payment profile holding a full account number is an integrity failure`` () =
    let r = withLifecycle ()
    let leaked = { r.Books.PaymentProfiles["us-bank@1"] with Instructions = "ACH account 123456789012" }
    let broken = { r with Books = { r.Books with PaymentProfiles = r.Books.PaymentProfiles.Add("us-bank@1", leaked) } }
    Assert.Contains(Invariants.check broken, fun v -> v.Rule = "no-secrets-in-payment-profiles")

[<Fact>]
let ``adjusting and closing entries keep their kind through storage`` () =
    let r = full ()
    let adjusting: Summa.Ledger.Periods.AdjustingEntry =
        { EntryId = "JE-ADJ-1"; Date = System.DateOnly(2026, 12, 31); Description = "Accrued hosting"; Kind = Summa.Ledger.Periods.Accrual
          Lines = [ { AccountId = "software"; Side = Debit(usd 5000L); Memo = None; Dimensions = noDimensions }; { AccountId = "credits"; Side = Credit(usd 5000L); Memo = None; Dimensions = noDimensions } ]
          Reference = Some "WP-3" }
    let ledger =
        r.Books.Ledger
        |> Summa.Ledger.Periods.postAdjusting ledgerContext adjusting
        |> ok
        |> Summa.Ledger.Periods.postClosing ledgerContext "USD" (System.DateOnly(2026, 12, 31)) "equity" "JE-CLOSE-2026"
        |> ok
    let original = { r with Books = { r.Books with Ledger = ledger } }
    let loaded = load (stored original)
    Assert.Empty loaded.Problems
    let entries = loaded.State.Books.Ledger.Entries
    Assert.Equal(Summa.Ledger.Periods.Adjusting(Summa.Ledger.Periods.Accrual, Some "WP-3"), Summa.Ledger.Periods.kindOf entries["JE-ADJ-1"])
    Assert.Equal(Summa.Ledger.Periods.YearEndClose 2026, Summa.Ledger.Periods.kindOf entries["JE-CLOSE-2026"])
    Assert.Equal(Some "github:583231", Summa.Ledger.Periods.createdBy loaded.State.Books.Ledger "JE-ADJ-1")

[<Fact>]
let ``tax as the person recorded it is stored at schema 2, and books without tax keep their schema 1 records`` () =
    // Books that record no tax: every record is written exactly as schema 1 wrote it.
    Assert.All(toRecords (full ()) |> ok, fun record -> Assert.Equal(1, schemaVersionOf record))

    let tax: Summa.Ledger.Invoicing.TaxCharge =
        { Code = "NY-8.875"
          AccountId = "credits"
          Jurisdiction = Some "New York, NY"
          RateSource = Some "NYS Pub 718, 2026-09"
          RateHundredthBasisPoints = Some 88750
          Evidence = Some "ST-100 2026-Q4"
          Pricing = Summa.Ledger.Invoicing.TaxInclusive }

    let exempt = { abc with Tax = Summa.Ledger.Invoicing.TaxExempt("ST-119.1 #4471", Some "New York") }

    let taxedDraft =
        { draft with
            DraftId = "D-TAX"
            Lines = draft.Lines |> List.map (fun l -> { l with Tax = Summa.Ledger.Invoicing.Taxable "services" })
            Adjustments = [ { Kind = Summa.Ledger.Invoicing.Tax tax; Label = "Sales tax"; Amount = usd 100L } ] }

    let books =
        let r = full ()
        { r with Books = { r.Books with Customers = r.Books.Customers.Add(exempt.Id, exempt); Drafts = r.Books.Drafts.Add(taxedDraft.DraftId, taxedDraft) } }

    let records = toRecords books |> ok
    let versionOf (path: string) = records |> List.find (fun r -> RelativePath.render (Layout.recordPath r.Key |> ok) = path) |> schemaVersionOf
    Assert.Equal(2, versionOf "records/summa.customer/CUST-ABC.json")
    Assert.Equal(2, versionOf "records/summa.draft/D-TAX.json")
    Assert.Equal(1, versionOf "records/summa.invoice/2026/INV-001.json")

    let loaded = load (stored books)
    Assert.Empty loaded.Problems
    Assert.Equal(exempt, loaded.State.Books.Customers[exempt.Id])
    Assert.True((taxedDraft = loaded.State.Books.Drafts[taxedDraft.DraftId]))
