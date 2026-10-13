/// Invoice export and portability (INV-EXP-001..003), one test per
/// requirement.
module Summa.Storage.Tests.InvoiceExportTests

open System
open System.Text.Json
open Xunit
open Summa.Ledger
open Summa.Ledger.Payments
open Summa.Storage
open Summa.Storage.Tests.Books

let private at = DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)

let private exportOf (r: Receivables) (invoiceId: string) : Exports.InvoiceExport =
    { GeneratedAt = at
      DataVersion = Exports.dataVersion r |> ok
      Invoice = r.Books.Invoices[invoiceId]
      RecordSchemaVersion = Exports.invoiceRecordSchema r invoiceId |> Option.get }

let private str (e: JsonElement) (name: string) = string (e.GetProperty(name).GetString())

[<Fact>]
let ``INV-EXP-001: invoices export as CSV, versioned JSON and HTML; PDF is the printed document`` () =
    let r = full ()
    let invoices = r.Books.Invoices |> Map.toList |> List.map snd
    let csv = Reports.invoicesCsv r invoices
    let rows = csv.TrimEnd('\n').Split '\n'
    Assert.Equal("Invoice,Number,Version,Customer,BillingName,IssueDate,DueDate,Currency,Subtotal,Total,Paid,WrittenOff,Outstanding,Status,Template,Rounding,JournalEntry", rows[0])
    Assert.Equal(invoices.Length, rows.Length - 1)
    let first = invoices |> List.sortBy (fun i -> i.IssueDate, i.Number) |> List.head
    Assert.StartsWith($"{first.InvoiceId},{first.Number},", rows[1])
    let owed = (decimal (outstanding r first).Minor / 100m).ToString("0.00", Globalization.CultureInfo.InvariantCulture)
    Assert.Contains("," + owed + ",", rows[1])

    let export = exportOf r first.InvoiceId
    let doc = Documents.ofInvoice first
    use json = JsonDocument.Parse(Exports.invoiceJson export (Documents.toJson doc))
    Assert.Equal("summa.invoice-export/1", str json.RootElement "schema")
    Assert.Equal(Documents.toJson doc, json.RootElement.GetProperty("document").GetRawText())
    let html = Exports.invoiceHtml export (Documents.render doc |> Result.defaultWith failwith)
    Assert.StartsWith("<!-- summa.invoice-export/1; ", html)
    Assert.EndsWith(Documents.render doc |> Result.defaultWith failwith, html)

[<Fact>]
let ``INV-EXP-002: an invoice export says when, from which data, which invoice, version, schema and template`` () =
    let r = full ()
    let invoice = r.Books.Invoices |> Map.toList |> List.head |> snd
    let export = exportOf r invoice.InvoiceId
    use json = JsonDocument.Parse(Exports.invoiceJson export (Documents.toJson (Documents.ofInvoice invoice)))
    let root = json.RootElement
    Assert.Equal("2026-10-13T12:00:00Z", str root "generatedAt")
    Assert.Matches("^sha256:[0-9a-f]{64}$", str root "dataVersion")
    Assert.Equal(invoice.InvoiceId, str root "invoiceId")
    Assert.Equal(invoice.Number, str root "number")
    Assert.Equal(invoice.Approval.DraftVersion, root.GetProperty("invoiceVersion").GetInt32())
    Assert.Equal(export.RecordSchemaVersion, root.GetProperty("recordSchemaVersion").GetInt32())
    Assert.Equal(invoice.Template.Id, str (root.GetProperty "template") "id")
    Assert.Equal(invoice.Template.Version, str (root.GetProperty "template") "version")
    Assert.Equal(invoice.Rounding, str root "rounding")
    // The multi-invoice CSV names its filters through the export header (SUM3-026).
    let csv = Exports.withHeader { Kind = "invoices"; GeneratedAt = at; Period = DateOnly(2026, 1, 1), DateOnly(2026, 12, 31); DataVersion = export.DataVersion; Filters = [ "year", "2026" ] } (Reports.invoicesCsv r [ invoice ])
    Assert.Contains("# Export: invoices\n", csv)
    Assert.Contains("# Filters: year=2026\n", csv)

[<Fact>]
let ``INV-EXP-003: an invoice's history can be rebuilt from open formats alone`` () =
    let r = full ()
    let invoice = r.Books.Invoices |> Map.toList |> List.head |> snd
    use json = JsonDocument.Parse(Exports.invoiceJson (exportOf r invoice.InvoiceId) (Documents.toJson (Documents.ofInvoice invoice)))
    let doc = json.RootElement.GetProperty "document"
    // The export alone gives the invoice's identity, its lines and its total.
    Assert.Equal(invoice.Number, str doc "number")
    Assert.Equal(invoice.Lines.Length, doc.GetProperty("lines").GetArrayLength())
    // And every authoritative record is plain JSON text, readable without Summa.
    for path, (_, content) in FinancialRecords.toRecords r |> Result.bind FinancialRecords.contents |> ok |> Map.toList do
        use record = JsonDocument.Parse(content: string)
        Assert.True(record.RootElement.ValueKind = JsonValueKind.Object, path)
