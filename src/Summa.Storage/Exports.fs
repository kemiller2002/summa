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
/// header do.
[<Literal>]
let SchemaVersion = "summa.export/1"

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
      "# Filters: " + (if m.Filters.IsEmpty then "none" else m.Filters |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat "; ") ]

/// The CSV with its header.
let withHeader (m: Metadata) (csv: string) = String.concat "\n" (header m) + "\n" + csv
