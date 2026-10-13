/// Performance and international presentation (INV-PERF, INV-INTL): the
/// large-invoice scenario, and documents that do not depend on locale or
/// claim compliance.
module Summa.Tests.InvoiceScaleTests

open System
open System.Diagnostics
open System.Globalization
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

/// The defined large-invoice scenario (INV-PERF-002): 2,000 lines over 40
/// projects, each a quarter hour to ten hours at 95.00 to 294.00.
let largeDraft () =
    draftFor
        [ for i in 1..2000 ->
              { consulting (int64 (250 + (i * 37) % 9750)) (int64 (9500 + (i * 113) % 19900)) with
                  Description = $"Work item {i}"
                  Project = Some $"PRJ-{i % 40}" } ]

[<Fact>]
let ``INV-PERF-001: a typical draft is checked and issued well within interactive time`` () =
    let typical = draftFor [ for i in 1..12 -> { consulting 7500L 17500L with Description = $"Task {i}" } ]
    issue context request (withDraft typical) |> ok |> ignore // warm up
    let clock = Stopwatch.StartNew()
    let _, invoice = issue context request (withDraft typical) |> ok
    Documents.render (Documents.ofInvoice invoice) |> ok |> ignore
    clock.Stop()
    Assert.True(clock.ElapsedMilliseconds < 1000L, $"took {clock.ElapsedMilliseconds} ms")

[<Fact>]
let ``INV-PERF-002: a 2,000-line invoice issues, posts, renders and totals exactly`` () =
    let draft = largeDraft ()
    let clock = Stopwatch.StartNew()
    let books, invoice = issue context request (withDraft draft) |> ok
    let html = Documents.render (Documents.ofInvoice invoice) |> ok
    let json = Documents.toJson (Documents.ofInvoice invoice)
    clock.Stop()
    Assert.Equal(2000, invoice.Lines.Length)
    Assert.Equal(draft.Lines |> List.map lineAmount |> sum "USD", invoice.Total)
    let entry = books.Ledger.Entries[invoice.JournalEntryId]
    Assert.Equal(2001, entry.Lines.Length) // receivables, then one revenue line per invoice line
    Assert.Equal(invoice.Total, balance books.Ledger "USD" request.IssueDate "ar")
    Assert.Contains("Work item 2000", html)
    Assert.Contains("Work item 2000", json)
    Assert.True(clock.ElapsedMilliseconds < 20000L, $"took {clock.ElapsedMilliseconds} ms")

[<Fact>]
let ``INV-INTL-002: the locale changes nothing on the invoice: amounts and dates are the same under any culture`` () =
    let _, invoice = issued ()
    let under (culture: string) =
        let before = CultureInfo.CurrentCulture
        CultureInfo.CurrentCulture <- CultureInfo culture
        try
            Documents.render (Documents.ofInvoice invoice) |> ok, Documents.toJson (Documents.ofInvoice invoice)
        finally
            CultureInfo.CurrentCulture <- before

    let reference = under "en-US"

    for culture in [ "de-DE"; "fr-FR"; "ja-JP"; "ar-SA" ] do
        Assert.Equal(reference, under culture)

    Assert.Equal(605000L, invoice.Total.Minor)
    Assert.Equal(DateOnly(2026, 10, 7), invoice.IssueDate)

[<Fact>]
let ``INV-INTL-003: no invoice claims compliance with a jurisdiction's tax or e-invoicing law`` () =
    let _, invoice = issued ()
    let text = (Documents.render (Documents.ofInvoice invoice) |> ok) + Documents.toJson (Documents.ofInvoice invoice)

    for claim in [ "complian"; "compliant"; "e-invoic"; "VAT invoice"; "tax invoice"; "certified"; "fiscal" ] do
        Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase)

// ---- Configurable legal fields (INV-INTL-001) -------------------------------------------------

let private vat: LegalField = { Kind = VatId; Jurisdiction = "GB"; Value = "GB123456789" }
let private registration: LegalField = { Kind = BusinessRegistrationNumber; Jurisdiction = "GB"; Value = "01234567" }
let private wording: LegalField = { Kind = StatutoryWording; Jurisdiction = "GB"; Value = "Registered in England and Wales." }
let private customerTax: LegalField = { Kind = CustomerTaxId; Jurisdiction = "US-NY"; Value = "NY-998877" }

[<Fact>]
let ``INV-INTL-001: legal fields are typed, recorded as given, and checked for jurisdiction and party`` () =
    Assert.Empty(legalFieldProblems false [ vat; registration; wording ])
    Assert.Empty(legalFieldProblems true [ customerTax; { vat with Value = "DE999" ; Jurisdiction = "DE" } ])
    Assert.Equal<LegalFieldProblem list>([ InvalidJurisdiction "England" ], legalFieldProblems false [ { vat with Jurisdiction = "England" } ])
    Assert.Equal<LegalFieldProblem list>([ BlankLegalField VatId ], legalFieldProblems false [ { vat with Value = " " } ])
    Assert.Equal<LegalFieldProblem list>([ NotForThisParty CustomerTaxId ], legalFieldProblems false [ customerTax ])
    Assert.Equal<LegalFieldProblem list>([ NotForThisParty StatutoryWording ], legalFieldProblems true [ wording ])
    Assert.Equal<LegalFieldProblem list>([ DuplicateLegalField(VatId, "GB") ], legalFieldProblems false [ vat; { vat with Value = "GB000" } ])
    // The organization's are saved through the books, audited; refused ones change nothing.
    let saved = saveLegalFields context [ vat; wording ] books |> ok
    Assert.Equal<LegalField list>([ vat; wording ], organizationLegalFields saved)
    Assert.Contains(saved.Ledger.Audit, fun a -> a.What = "legal-fields-saved")
    Assert.True(Result.isError (saveLegalFields context [ customerTax ] saved))

[<Fact>]
let ``INV-INTL-001: an invoice shows the organization's and the customer's legal fields as they were at issue`` () =
    let withFields =
        saveLegalFields context [ vat; registration; wording ] books
        |> ok
        |> saveCustomer context { abc with LegalFields = [ customerTax ] }

    let issuing = { request with Issuer = { request.Issuer with LegalFields = organizationLegalFields withFields } }
    let after, invoice = issue context issuing (saveDraft context (draftFor [ consulting 10000L 15000L ]) withFields |> ok) |> ok
    Assert.Equal<LegalField list>([ vat; registration; wording ], invoice.Issuer.LegalFields)
    Assert.Equal<LegalField list>([ customerTax ], invoice.Customer.LegalFields)

    let html = Documents.render (Documents.ofInvoice invoice) |> ok
    Assert.Contains("VAT ID (GB): GB123456789", html)
    Assert.Contains("Business registration number (GB): 01234567", html)
    Assert.Contains("Customer tax ID (US-NY): NY-998877", html)
    Assert.Contains("<h2 id=\"summa-statutory\">Statutory information</h2><p>Registered in England and Wales.</p>", html)
    let json = Documents.toJson (Documents.ofInvoice invoice)
    Assert.Contains("\"kind\":\"vat-id\",\"label\":\"VAT ID\",\"jurisdiction\":\"GB\",\"value\":\"GB123456789\"", json)
    Assert.Contains("\"kind\":\"customer-tax-id\"", json)
    // Later changes never reach the issued invoice.
    let changed = saveCustomer context { abc with LegalFields = [] } (saveLegalFields context [] after |> ok)
    Assert.Equal<LegalField list>([ customerTax ], changed.Invoices[invoice.InvoiceId].Customer.LegalFields)
    Assert.Equal<LegalField list>([ vat; registration; wording ], changed.Invoices[invoice.InvoiceId].Issuer.LegalFields)

    // Showing them claims no compliance (INV-INTL-003).
    for claim in [ "complian"; "e-invoic"; "VAT invoice"; "tax invoice"; "certified"; "fiscal" ] do
        Assert.DoesNotContain(claim, html + json, StringComparison.OrdinalIgnoreCase)

[<Fact>]
let ``INV-INTL-001: an invoice without legal fields renders exactly as before`` () =
    let _, invoice = issued ()
    let html = Documents.render (Documents.ofInvoice invoice) |> ok
    Assert.DoesNotContain("Statutory information", html)
    Assert.DoesNotContain("legalFields", Documents.toJson (Documents.ofInvoice invoice))
