/// An organization's books kept in one browser, for a local or demo
/// deployment that names no data location (WI-0030).
///
/// The snapshot is the organization manifest's canonical text plus every
/// financial record's canonical text, by the same paths Arca would store
/// them under. Reading it back runs the same checks as reading a folder:
/// - each record is validated;
/// - its path must be the one its content names;
/// - the books must satisfy every invariant.
///
/// The browser's copy is untrusted like any stored data. A GitHub-backed
/// deployment replaces this with Arca (WI-0037).
///
/// Pure.
module Summa.Storage.LocalSnapshot

open System
open System.IO
open System.Text
open System.Text.Json
open Arca
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Storage.Diagnostics

[<Literal>]
let Version = 1

/// The local organization's id; a local deployment serves one organization.
[<Literal>]
let OrganizationId = "org_local"

/// The chart a new local organization starts with. Account ids are their
/// codes, which is how the manifest names its default accounts.
let defaultChart =
    [ "1000", "Operating Cash", Asset
      "1100", "Accounts Receivable", Asset
      "2100", "Customer Credits", Liability
      "2200", "Customer Deposits", Liability
      "2300", "Sales Tax Payable", Liability
      "3000", "Owner Equity", Equity
      "3100", "Retained Earnings", Equity
      "4000", "Consulting Revenue", Revenue
      "4100", "Reimbursed Expenses", Revenue
      "6100", "Software", Expense
      "6200", "Travel", Expense
      "6500", "Bad Debt", Expense ]
    |> List.map (fun (code, name, kind) ->
        { Id = code
          Code = code
          Name = name
          Type = kind
          Active = true })

/// The accounts a local organization's receivable commands post to: the
/// manifest's cash and receivables, and the default chart's customer
/// credits, deposits and bad debt. The manifest does not name those yet;
/// books on GitHub (WI-0037) need it to.
let receivableAccounts (manifest: Organization.OrganizationManifest) (ledger: Ledger) : Summa.Ledger.Credits.ReceivableAccounts =
    let byCode = Organization.accountByCode ledger

    { Cash = byCode manifest.Accounting.CashAccount
      Receivable = byCode manifest.Accounting.ReceivablesAccount
      CustomerCredits = byCode "2100"
      CustomerDeposits = byCode "2200"
      BadDebt = byCode "6500" }

/// A new local organization: its manifest and books with the default chart.
let start (context: Context) (displayName: string) (company: Organization.CompanyInformation) (paymentInstructions: string) =
    let created = Organization.create OrganizationId displayName "local" "USD" "UTC" context.When

    let manifest =
        { created with
            Company = company
            Invoices = { created.Invoices with PaymentInstructions = paymentInstructions } }

    let ledger =
        defaultChart
        |> List.fold (fun ledger account -> ledger |> Result.bind (addAccount context account)) (Ok empty)

    ledger |> Result.map (fun l -> manifest, start (openBooks l))

let private storedObject (path: string) (content: string) : Result<StoredObject, string> =
    RelativePath.parse path
    |> Result.mapError LocationError.describe
    |> Result.map (fun p ->
        { Path = p
          Content = content
          Revision = Revision("local:" + path) })

/// The snapshot's text, or why the books cannot be stored.
let encode (manifest: Organization.OrganizationManifest) (r: Receivables) : Result<string, Diagnostic list> =
    Organization.encode manifest
    |> Result.bind (fun manifestText ->
        FinancialRecords.toRecords r
        |> Result.bind FinancialRecords.contents
        |> Result.map (fun records ->
            use stream = new MemoryStream()

            do
                use w = new Utf8JsonWriter(stream)
                w.WriteStartObject()
                w.WriteNumber("version", Version)
                w.WriteString("manifest", manifestText)
                w.WriteStartObject "records"

                for KeyValue(path, (_, content)) in records do
                    w.WriteString(path, content)

                w.WriteEndObject()
                w.WriteEndObject()

            Encoding.UTF8.GetString(stream.ToArray())))

/// What a snapshot holds, after every check.
[<NoComparison; NoEquality>]
type Restored =
    { Manifest: Organization.OrganizationManifest
      Books: Receivables
      /// Integrity problems; the books must not be used when there are any.
      Problems: Diagnostic list }

/// Reads a snapshot back. A snapshot that is not one at all is an error;
/// one whose records fail checks is restored with its problems listed.
let decode (text: string) : Result<Restored, string> =
    try
        use document = JsonDocument.Parse text
        let root = document.RootElement

        match root.TryGetProperty "version", root.TryGetProperty "manifest", root.TryGetProperty "records" with
        | (true, v), (true, m), (true, records) when v.ValueKind = JsonValueKind.Number && v.GetInt32() = Version && m.ValueKind = JsonValueKind.String && records.ValueKind = JsonValueKind.Object ->
            let objects =
                records.EnumerateObject()
                |> Seq.map (fun p ->
                    match p.Value.ValueKind with
                    | JsonValueKind.String -> storedObject p.Name (p.Value.GetString() |> Option.ofObj |> Option.defaultValue "")
                    | _ -> Error $"record {p.Name} is not text")
                |> List.ofSeq

            match objects |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some problem -> Error problem
            | None ->
                let manifestText = m.GetString() |> Option.ofObj |> Option.defaultValue ""

                Organization.path OrganizationId
                |> Result.mapError Diagnostics.describe
                |> Result.bind (fun path ->
                    Organization.decode OrganizationId { Path = path; Content = manifestText; Revision = Revision "local:manifest" }
                    |> Result.mapError Diagnostics.describe)
                |> Result.map (fun manifest ->
                    let loaded = FinancialRecords.load (objects |> List.choose Result.toOption)

                    { Manifest = manifest
                      Books = loaded.State
                      Problems = loaded.Problems })
        | _ -> Error $"this is not a version {Version} Summa snapshot"
    with :? JsonException as e ->
        Error $"the snapshot is not JSON ({e.Message})"
