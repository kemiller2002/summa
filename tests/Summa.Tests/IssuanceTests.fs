/// Invoice generation core (WI-0027): readiness and its blockers, review,
/// version-specific issue, numbering, snapshots, the Folio HTML and JSON
/// documents, artifacts and their checksums (INV-DRAFT, INV-REV, INV-ISS,
/// INV-NUM, INV-DOC, SUM1-007, SUM0-037, SUM3-030).
module Summa.Tests.IssuanceTests

open System
open System.Text.Json
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Issuance
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let private detailed =
    { draftFor [ consulting 34500L 17500L; { consulting 2000L 9000L with Description = "Workshop <prep> & notes" } ] with
        Details =
            { PurchaseOrder = Some "PO-7781"
              ClientReference = Some "ABC/ARCH"
              ServicePeriod = Some(DateOnly(2026, 9, 1), DateOnly(2026, 9, 30))
              CustomerNotes = Some "Thank you for your business."
              InternalNotes = Some "Discounted travel internally" } }

let private drafted (draft: DraftInvoice) = start (saveDraft context draft books |> ok)

let private codes (found: Blocker list) = found |> List.map _.Code

let private submitted () = drafted detailed |> submitForReview context request |> ok

[<Fact>]
let ``readiness names every blocker and what resolves it`` () =
    let bad =
        { draftFor [ { consulting 1000L 10000L with RevenueAccountId = "cash" } ] with
            DueDate = Some(DateOnly(2026, 10, 1)) }

    let found = blockers (drafted bad) { request with Issuer = { request.Issuer with Address = " " } }
    Assert.Equal<string list>([ "issuer-incomplete"; "due-before-issue"; "invalid-revenue-account" ], codes found)
    Assert.All(found, fun b -> Assert.False(String.IsNullOrWhiteSpace b.Resolution))
    // A closed period blocks issue; the blocker says how to proceed.
    let closed = { books with Ledger = closePeriod context (2026, 10) books.Ledger }
    let inClosed = start (saveDraft context detailed closed |> ok)
    Assert.Equal<string list>([ "period-closed" ], blockers inClosed request |> codes)
    Assert.Empty(blockers (drafted detailed) request)

[<Fact>]
let ``issue needs the reviewed version, and a change sends the draft back to editing`` () =
    let r = drafted detailed
    Assert.Equal<string list>([ "not-reviewed" ], issueInvoice context request r |> refused |> codes)
    let reviewed = submitForReview context request r |> ok
    Assert.Equal(SubmittedForReview 1, reviewed.Books.Drafts["D-1"].Review)
    // A change after review returns it to Editing at a new version.
    let edited = { reviewed with Books = saveDraft context { reviewed.Books.Drafts["D-1"] with Adjustments = [ usd 500L ] } reviewed.Books |> ok }
    Assert.Equal((Editing, 2), (edited.Books.Drafts["D-1"].Review, edited.Books.Drafts["D-1"].Version))
    Assert.Equal<string list>([ "not-reviewed" ], issueInvoice context { request with ExpectedVersion = Some 1 } edited |> refused |> codes)
    let again = submitForReview context request edited |> ok
    Assert.Equal<string list>([ "not-reviewed" ], issueInvoice context { request with ExpectedVersion = Some 1 } again |> refused |> codes)
    let _, invoice = issueInvoice context { request with ExpectedVersion = Some 2 } again |> ok
    Assert.Equal(2, invoice.Approval.DraftVersion)
    // Returning to draft is explicit too.
    Assert.Equal(Editing, (returnToDraft context "D-1" reviewed |> ok).Books.Drafts["D-1"].Review)

[<Fact>]
let ``issuing commits the invoice, its snapshots, approval and artifacts together`` () =
    let approver = { context with Who = "github:7"; When = DateTimeOffset(2026, 10, 8, 15, 30, 0, TimeSpan.Zero); CorrelationId = Some "corr-9" }
    let issued, invoice = issueInvoice approver request (submitted ()) |> ok
    Assert.Equal("EF-2026-0001", invoice.Number)
    Assert.Equal({ By = "github:7"; At = approver.When; DraftVersion = 1; CorrelationId = Some "corr-9" }, invoice.Approval)
    Assert.Equal("ABC Corp Accounts Payable", invoice.Customer.BillingName)
    Assert.Equal(Documents.currentTemplate, invoice.Template)
    Assert.Equal(Some "PO-7781", invoice.Details.PurchaseOrder)
    let artifacts = issued.Books.Artifacts |> Map.toList |> List.map snd |> List.sortBy _.Id
    Assert.Equal<string list>([ "INV-001-html"; "INV-001-json"; "INV-001-pdf" ], artifacts |> List.map _.Id)
    Assert.Equal<ArtifactStatus list>([ Generated; Generated; Pending ], artifacts |> List.map _.Status)
    Assert.All(artifacts |> List.take 2, fun a -> Assert.Equal(64, a.Sha256.Value.Length))
    Assert.Equal(Some "summa:invoice/INV-001/document.html", artifacts |> List.tryHead |> Option.map _.Reference)
    // The journal entry and obligation came with it; retrying returns the same invoice.
    Assert.True(issued.Books.Ledger.Entries.ContainsKey "JE-000001")
    Assert.Equal(invoice, issueInvoice approver request issued |> ok |> snd)

[<Fact>]
let ``the preview is the document that will be issued`` () =
    let r = submitted ()
    let previewDoc, previewHtml = preview r request |> ok
    let issued, invoice = issueInvoice context request r |> ok
    let doc = Documents.ofInvoice invoice
    Assert.Equal(previewHtml, Documents.render doc |> ok)
    Assert.Equal<Documents.DocumentLine list>(previewDoc.Lines, doc.Lines)
    Assert.Equal(Some(fst (Documents.digest previewHtml)), issued.Books.Artifacts["INV-001-html"].Sha256)

[<Fact>]
let ``the HTML is semantic, uses Folio's print primitives and shows what the customer needs`` () =
    let _, invoice = issueInvoice context request (submitted ()) |> ok
    let html = Documents.render (Documents.ofInvoice invoice) |> ok

    for expected in
        [ "<ef-print-document>"
          "<ef-print-header repeat=\"page\">"
          "<ef-print-table><table><caption>Invoice lines</caption>"
          "<ef-print-keep>"
          "<ef-print-page-number format=\"page-of-pages\">"
          "<h1>Invoice EF-2026-0001</h1>"
          "Echelon Foundry LLC"
          "1 Foundry Way<br>Springfield"
          "<dt>Issue date</dt><dd>October 7, 2026</dd>"
          "<dt>Due date</dt><dd>November 6, 2026</dd>"
          "<dt>Payment terms</dt><dd>Net 30</dd>"
          "<dt>Purchase order</dt><dd>PO-7781</dd>"
          "<dt>Service period</dt><dd>September 1, 2026 to September 30, 2026</dd>"
          "ABC Corp Accounts Payable"
          "<td>Workshop &lt;prep&gt; &amp; notes</td>"
          "<td class=\"summa-number\">34.5</td><td class=\"summa-number\">175.00</td>"
          "<td class=\"summa-number\">6,037.50</td>"
          "Amount due</th><td class=\"summa-number\">6,217.50 USD</td>"
          "ACH to account ending 6789"
          "Thank you for your business." ] do
        Assert.Contains(expected, html)

    // Internal notes never reach the customer (INV-DATA-006).
    Assert.DoesNotContain("Discounted travel internally", html)

[<Fact>]
let ``the JSON is versioned, deterministic and keeps each line's source`` () =
    let _, invoice = issueInvoice context request (submitted ()) |> ok
    let doc = Documents.ofInvoice invoice
    let text = Documents.toJson doc
    Assert.Equal(text, Documents.toJson (Documents.ofInvoice invoice))
    use parsed = JsonDocument.Parse text
    let root = parsed.RootElement
    Assert.Equal("summa.invoice-document", root.GetProperty("schema").GetString())
    Assert.Equal(1, root.GetProperty("version").GetInt32())
    Assert.Equal(621750L, root.GetProperty("total").GetProperty("minor").GetInt64())
    Assert.Equal("manual", root.GetProperty("lines").[0].GetProperty("source").GetProperty("kind").GetString())
    Assert.Equal("PO-7781", root.GetProperty("purchaseOrder").GetString())

[<Fact>]
let ``an issued document never changes when the customer, company or defaults do`` () =
    let issued, invoice = issueInvoice context request (submitted ()) |> ok
    let moved = { issued with Books = issued.Books |> saveCustomer context { abc with BillingAddress = "99 New Street"; Name = "ABC Holdings" } }
    Assert.Equal<(ArtifactKind * ArtifactCheck) list>([ InvoiceHtml, Intact; InvoiceJson, Intact ], verify moved invoice.InvoiceId)
    Assert.DoesNotContain("99 New Street", Documents.render (Documents.ofInvoice moved.Books.Invoices[invoice.InvoiceId]) |> ok)
    // Altered stored content is detected.
    let tampered = { moved with Books = { moved.Books with Invoices = moved.Books.Invoices.Add(invoice.InvoiceId, { invoice with Number = "EF-2026-9999" }) } }

    match verify tampered invoice.InvoiceId with
    | [ InvoiceHtml, Altered _; InvoiceJson, Altered _ ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``an unknown template blocks issue before anything is committed`` () =
    let r = drafted detailed
    let missing = { request with Template = { Documents.currentTemplate with Version = "9.9.9" } }
    Assert.Equal<string list>([ "render-failed" ], blockers r missing |> codes)

[<Fact>]
let ``the PDF is finished after issue; failing it never undoes the issue, and a retry never makes a second one`` () =
    let issued, invoice = issueInvoice context request (submitted ()) |> ok
    let failed = pdfFailed context invoice.InvoiceId "renderer unavailable" issued |> ok
    Assert.Equal(Failed "renderer unavailable", failed.Books.Artifacts["INV-001-pdf"].Status)
    Assert.True(failed.Books.Invoices.ContainsKey invoice.InvoiceId)
    let bytes = Text.Encoding.ASCII.GetBytes "%PDF-1.7 test"
    let sha = Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()
    let file = { Reference = "artifacts://production/org_acme/INV-001.pdf"; Sha256 = sha; Size = int64 bytes.Length; Renderer = "folio-p0/chromium-128" }
    let stored = recordPdf context invoice.InvoiceId file failed |> ok
    let pdf = stored.Books.Artifacts["INV-001-pdf"]
    Assert.Equal(Generated, pdf.Status)
    Assert.Equal(Intact, checkBytes pdf bytes)
    Assert.True(match checkBytes pdf (Text.Encoding.ASCII.GetBytes "%PDF-1.7 other") with Altered _ -> true | _ -> false)
    Assert.Equal(stored, recordPdf context invoice.InvoiceId file stored |> ok)
    Assert.Equal(Error(AlreadyGenerated "INV-001-pdf"), recordPdf context invoice.InvoiceId { file with Sha256 = String('a', 64) } stored)

[<Fact>]
let ``numbers follow the policy, are unique and are never reused, even after a void`` () =
    let continuous = { request with Numbering = { Prefix = "EF"; Scope = Continuous; Digits = 6 } }
    let issued, first = issueInvoice context continuous (submitted ()) |> ok
    Assert.Equal("EF-000001", first.Number)
    let voidRequest: Summa.Ledger.Corrections.VoidRequest = { InvoiceId = first.InvoiceId; Reason = "Duplicate"; Date = DateOnly(2026, 10, 8); JournalEntryId = "JE-VOID-1" }
    let voided = Summa.Ledger.Corrections.voidInvoice context voidRequest issued |> ok
    let second = { continuous with DraftId = "D-2"; InvoiceId = "INV-002"; JournalEntryId = "JE-000002"; ObligationId = "OBL-002" }
    let withSecond = { voided with Books = saveDraft context { detailed with DraftId = "D-2" } voided.Books |> ok } |> submitForReview context second |> ok
    let _, next = issueInvoice context second withSecond |> ok
    Assert.Equal("EF-000002", next.Number)
    // A chosen number is checked for collisions, including with voided invoices.
    let clash = { second with NumberOverride = Some "EF-000001" }
    Assert.Equal<string list>([ "duplicate-number" ], blockers withSecond clash |> codes)

[<Fact>]
let ``a draft billing a source that is reserved elsewhere cannot be issued`` () =
    let time: SourceTime =
        { PublicationId = "pub-1"; OrganizationId = "org-1"; ActivityId = "act-1"; Revision = 1; PerformerId = "github:1"; BusinessDate = DateOnly(2026, 10, 6)
          ProjectId = "PRJ-A"; ClientId = Some abc.Id; EngagementId = None; ActivityTypeId = "consulting"; Description = "Work"; ExactMinutes = 60; BillableMinutes = 60
          Approved = true; RateReference = None; Origin = OriginUnknown; Lineage = []; WorkItem = None; Supersedes = None; PublishedAt = DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero) }

    let r = start books |> Summa.Ledger.Billing.importTime context time |> ok |> fst
    let accounts: Summa.Ledger.Billing.BillingAccounts = { TimeRevenue = "revenue"; FeeRevenue = "revenue"; ReimbursedExpenses = "revenue" }
    let proposalRequest: Summa.Ledger.Billing.ProposalRequest = { ProposalId = "P-1"; CustomerId = abc.Id; EngagementId = None; Currency = "USD"; Time = [ "pub-1" ]; Grouping = []; FixedFee = false; Milestones = []; Expenses = []; Manual = []; Accounts = accounts }
    let reserved = Summa.Ledger.Billing.propose context proposalRequest r |> ok
    let sneaky = draftFor [ { consulting 1000L 10000L with Source = TimeSource [ { PublicationId = "pub-1"; ActivityId = "act-1"; Revision = 1; Minutes = 60 } ] } ]
    let withDraft = { reserved with Books = saveDraft context sneaky reserved.Books |> ok }
    Assert.Equal<string list>([ "source-unavailable" ], blockers withDraft request |> codes)
