/// Exports that say what they are (SUM3-026, INV-EXP-002): when they were
/// made, the accounting period, the version of the data, the filters and
/// the export's own schema, so a CPA can tell which dataset a file holds
/// and the same books always give the same file.
///
/// Pure.
module Summa.Storage.Exports

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Summa.Ledger.Payments

/// The export format's own version: it changes when the columns or the
/// header do. Version 2 adds the `Classification` line (SUM3-024).
[<Literal>]
let SchemaVersion = "summa.export/2"

/// What every financial export says it is (SUM3-024): a sensitive file, to
/// be kept and shared as confidential financial data.
[<Literal>]
let Classification = "confidential financial data"

type Metadata =
    { /// What the file holds, such as `trial-balance` or `journal`.
      Kind: string
      GeneratedAt: DateTimeOffset
      /// The accounting period, first and last business day.
      Period: DateOnly * DateOnly
      /// The digest of every authoritative record the books hold.
      DataVersion: string
      /// The filters applied, as `name=value`, in a stable order.
      Filters: (string * string) list }

/// The digest of the books' authoritative records: their stored paths and
/// canonical contents, in path order. The same books always give the same
/// version; any change to a record gives another.
let dataVersion (r: Receivables) : Result<string, Diagnostics.Diagnostic list> =
    FinancialRecords.toRecords r
    |> Result.bind FinancialRecords.contents
    |> Result.map (fun contents ->
        let canonical =
            contents
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (path, (_, content)) -> $"{path}\n{content}")
            |> String.concat "\n"

        "sha256:" + (SHA256.HashData(Encoding.UTF8.GetBytes canonical) |> Convert.ToHexString).ToLowerInvariant())

let private iso (d: DateOnly) = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

/// The header lines a CSV export starts with, each `# name: value`.
let header (m: Metadata) =
    let first, last = m.Period

    [ $"# Export: {m.Kind}"
      $"# SchemaVersion: {SchemaVersion}"
      "# GeneratedAt: " + m.GeneratedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
      $"# AccountingPeriod: {iso first}..{iso last}"
      $"# DataVersion: {m.DataVersion}"
      "# Filters: " + (if m.Filters.IsEmpty then "none" else m.Filters |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat "; ")
      $"# Classification: {Classification}" ]

/// The CSV with its header.
let withHeader (m: Metadata) (csv: string) = String.concat "\n" (header m) + "\n" + csv

/// The invoice export's own schema: it changes when its envelope does.
/// Version 2 adds `classification` (SUM3-024).
[<Literal>]
let InvoiceExportSchema = "summa.invoice-export/2"

/// What one invoice's export says about itself (INV-EXP-002).
type InvoiceExport =
    { GeneratedAt: DateTimeOffset
      DataVersion: string
      Invoice: Summa.Ledger.Invoicing.IssuedInvoice
      /// The schema version the invoice's record is stored at.
      RecordSchemaVersion: int }

/// The schema version an invoice's record is stored at, or None when the
/// books hold no such invoice.
let invoiceRecordSchema (r: Receivables) (invoiceId: string) =
    FinancialRecords.toRecords r
    |> Result.toOption
    |> Option.bind (List.tryFind (fun x -> Arca.RecordType.value x.Key.Type = "summa.invoice" && Arca.RecordId.value x.Key.Id = invoiceId))
    |> Option.map FinancialRecords.schemaVersionOf

// A JSON string literal. Written with Utf8JsonWriter, not JsonSerializer: the
// browser build trims reflection-based serialization away, so
// JsonSerializer.Serialize threw JsonSerializerIsReflectionDisabled there and
// "Download (JSON)" did nothing (summa#90, ULI-A01). The escaping is the same
// default encoder either way.
let private quoted (text: string) =
    use stream = new IO.MemoryStream()

    do
        use writer = new Text.Json.Utf8JsonWriter(stream)
        writer.WriteStringValue text

    Text.Encoding.UTF8.GetString(stream.ToArray())

let private stamp (at: DateTimeOffset) =
    at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

/// One invoice as versioned JSON (INV-EXP-001, INV-EXP-002): the export's
/// metadata, then the canonical customer document unchanged.
let invoiceJson (e: InvoiceExport) (documentJson: string) =
    let i = e.Invoice

    String.concat
        ""
        [ "{"
          $"\"schema\":{quoted InvoiceExportSchema},"
          $"\"classification\":{quoted Classification},"
          $"\"generatedAt\":{quoted (stamp e.GeneratedAt)},"
          $"\"dataVersion\":{quoted e.DataVersion},"
          $"\"invoiceId\":{quoted i.InvoiceId},"
          $"\"number\":{quoted i.Number},"
          $"\"invoiceVersion\":{i.Approval.DraftVersion},"
          $"\"recordSchemaVersion\":{e.RecordSchemaVersion},"
          $"\"template\":{{\"id\":{quoted i.Template.Id},\"version\":{quoted i.Template.Version}}},"
          $"\"rounding\":{quoted i.Rounding},"
          $"\"document\":{documentJson}"
          "}" ]

/// One invoice as HTML: the canonical customer document, after a comment
/// that says what the file is (INV-EXP-002).
let invoiceHtml (e: InvoiceExport) (documentHtml: string) =
    let i = e.Invoice

    $"<!-- {InvoiceExportSchema}; classification={Classification}; generatedAt={stamp e.GeneratedAt}; dataVersion={e.DataVersion}; invoiceId={i.InvoiceId}; number={i.Number}; invoiceVersion={i.Approval.DraftVersion}; recordSchemaVersion={e.RecordSchemaVersion}; template={i.Template.Id}@{i.Template.Version}; rounding={i.Rounding} -->\n"
    + documentHtml
