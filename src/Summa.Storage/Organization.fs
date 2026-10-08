/// The organization manifest (SUM0-010, SUM0-011): who an organization is
/// and how its books are kept, stored as one Arca record in the
/// organization's own folder, which is named by its immutable OrganizationId
/// (SUM0-008, SUM0-010). It is the canonical entry point for
/// organization-level configuration.
///
/// Pure. Stored content is untrusted input: decoding checks the record type,
/// the schema version, the organization it belongs to and every field.
module Summa.Storage.Organization

open System
open Arca
open Summa.Storage.Diagnostics

/// The organization's immutable id as an Arca dataset id.
let dataset (organizationId: string) : Result<DatasetId, Diagnostic> =
    DatasetId.create organizationId |> Result.mapError (fun _ -> InvalidOrganizationId organizationId)

/// Accrual or cash basis for the organization's default reporting (v0.2 §24).
type AccountingBasis =
    | Accrual
    | Cash

/// When the fiscal year starts.
type FiscalConfiguration =
    { /// 1 (January) to 12.
      YearStartMonth: int }

/// How the organization's invoices are numbered and what they default to.
type InvoiceConfiguration =
    { /// Prefix of invoice numbers, for example `INV` gives `INV-2026-0001`.
      NumberPrefix: string
      /// Payment terms, in days, when an invoice names none.
      DefaultTermsDays: int
      /// How customers pay, printed on invoices (bank details, payment link).
      PaymentInstructions: string }

/// Who issues the invoices: the organization's legal identity.
type CompanyInformation =
    { LegalName: string
      Address: string
      TaxId: string option
      Email: string }

/// The default accounts postings use (v0.1 §1, §3, §11).
type AccountingDefaults =
    { Basis: AccountingBasis
      ReceivablesAccount: string
      RevenueAccount: string
      CashAccount: string }

type OrganizationManifest =
    { /// Immutable. It names the organization's folder.
      OrganizationId: string
      /// Lower case, for URLs and display; mutable.
      Slug: string
      DisplayName: string
      /// The version of Summa's storage layout the organization's data uses.
      StorageVersion: int
      CreatedAt: DateTimeOffset
      /// ISO 4217.
      DefaultCurrency: string
      /// The organization's business time zone (IANA id).
      TimeZone: string
      Fiscal: FiscalConfiguration
      Company: CompanyInformation
      Invoices: InvoiceConfiguration
      Accounting: AccountingDefaults }

/// The storage layout version this Summa writes.
[<Literal>]
let StorageVersion = 1

let private recordType text =
    match RecordType.create text with
    | Ok recordType -> recordType
    | Error _ -> invalidOp ("internal: not a record type: " + text)

/// The record type of an organization manifest.
let manifestType = recordType "summa.organization"

/// The organization manifest's schema versions this Summa reads and writes.
let schema =
    { Type = manifestType
      OldestReadable = 1
      Current = 1 }

let private validSlug (slug: string) =
    not (String.IsNullOrEmpty slug)
    && slug.Length <= 64
    && Char.IsAsciiLetterLower slug[0]
    && slug |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')
    && not (slug.EndsWith '-')

let private validCurrency (code: string) =
    code.Length = 3 && code |> Seq.forall Char.IsAsciiLetterUpper

/// The identity checks shared by configuration and the manifest.
let validateIdentity (id: string) (displayName: string) (slug: string) (currency: string) (zone: string) : Result<unit, Diagnostic> =
    match dataset id with
    | Error diagnostic -> Error diagnostic
    | Ok _ when String.IsNullOrWhiteSpace displayName -> Error(MissingField "displayName")
    | Ok _ when not (validSlug slug) -> Error(InvalidSlug slug)
    | Ok _ when not (validCurrency currency) -> Error(InvalidCurrency currency)
    | Ok _ when String.IsNullOrWhiteSpace zone -> Error(MissingField "timeZone")
    | Ok _ -> Ok()

/// A new organization's manifest with Summa's defaults: calendar fiscal year,
/// `INV` numbering, 30-day terms, accrual basis and the default chart's
/// receivables (1100), revenue (4000) and cash (1000) accounts.
let create (organizationId: string) (displayName: string) (slug: string) (currency: string) (zone: string) (createdAt: DateTimeOffset) =
    { OrganizationId = organizationId
      Slug = slug
      DisplayName = displayName
      StorageVersion = StorageVersion
      CreatedAt = createdAt
      DefaultCurrency = currency
      TimeZone = zone
      Fiscal = { YearStartMonth = 1 }
      Company =
        { LegalName = displayName
          Address = ""
          TaxId = None
          Email = "" }
      Invoices =
        { NumberPrefix = "INV"
          DefaultTermsDays = 30
          PaymentInstructions = "" }
      Accounting =
        { Basis = Accrual
          ReceivablesAccount = "1100"
          RevenueAccount = "4000"
          CashAccount = "1000" } }

/// Every reason a manifest cannot be stored; empty when it can.
let problems (manifest: OrganizationManifest) =
    let blank = String.IsNullOrWhiteSpace

    [ match validateIdentity manifest.OrganizationId manifest.DisplayName manifest.Slug manifest.DefaultCurrency manifest.TimeZone with
      | Error diagnostic -> diagnostic
      | Ok() -> ()
      if manifest.StorageVersion <> StorageVersion then
          UnsupportedStorageVersion(manifest.StorageVersion, StorageVersion)
      if manifest.Fiscal.YearStartMonth < 1 || manifest.Fiscal.YearStartMonth > 12 then
          InvalidOrganizationManifest "the fiscal year must start in a month from 1 to 12"
      if blank manifest.Invoices.NumberPrefix || manifest.Invoices.NumberPrefix |> Seq.exists (fun c -> not (Char.IsAsciiLetterOrDigit c)) then
          InvalidOrganizationManifest "the invoice number prefix must be letters and digits"
      if blank manifest.Company.LegalName then
          InvalidOrganizationManifest "the company's legal name is required"
      if manifest.Invoices.DefaultTermsDays < 0 || manifest.Invoices.DefaultTermsDays > 365 then
          InvalidOrganizationManifest "default payment terms must be 0 to 365 days"
      if [ manifest.Accounting.ReceivablesAccount; manifest.Accounting.RevenueAccount; manifest.Accounting.CashAccount ] |> List.exists blank then
          InvalidOrganizationManifest "every default account is required" ]

let private basisWire =
    function
    | Accrual -> "accrual"
    | Cash -> "cash"

/// The manifest's record body.
let body (manifest: OrganizationManifest) =
    Json.objectOf
        [ "organizationId", Json.String manifest.OrganizationId
          "slug", Json.String manifest.Slug
          "displayName", Json.String manifest.DisplayName
          "storageVersion", Codec.number manifest.StorageVersion
          "createdAt", Json.String(Codec.timestamp manifest.CreatedAt)
          "defaultCurrency", Json.String manifest.DefaultCurrency
          "timeZone", Json.String manifest.TimeZone
          "fiscal", Json.objectOf [ "yearStartMonth", Codec.number manifest.Fiscal.YearStartMonth ]
          "company",
          Json.objectOf
              [ "legalName", Json.String manifest.Company.LegalName
                "address", Json.String manifest.Company.Address
                "taxId", Codec.optionalString manifest.Company.TaxId
                "email", Json.String manifest.Company.Email ]
          "invoices",
          Json.objectOf
              [ "numberPrefix", Json.String manifest.Invoices.NumberPrefix
                "defaultTermsDays", Codec.number manifest.Invoices.DefaultTermsDays
                "paymentInstructions", Json.String manifest.Invoices.PaymentInstructions ]
          "accounting",
          Json.objectOf
              [ "basis", Json.String(basisWire manifest.Accounting.Basis)
                "receivablesAccount", Json.String manifest.Accounting.ReceivablesAccount
                "revenueAccount", Json.String manifest.Accounting.RevenueAccount
                "cashAccount", Json.String manifest.Accounting.CashAccount ] ]

let private recordId (organizationId: string) =
    RecordId.create organizationId |> Result.mapError (fun _ -> InvalidOrganizationId organizationId)

/// The manifest as a record, or every reason it cannot be stored.
let toRecord (manifest: OrganizationManifest) : Result<Record, Diagnostic list> =
    match problems manifest with
    | _ :: _ as found -> Error found
    | [] ->
        recordId manifest.OrganizationId
        |> Result.mapError List.singleton
        |> Result.map (fun id ->
            { Id = id
              Type = manifestType
              SchemaVersion = schema.Current
              Mutability = Mutability.Mutable
              Body = body manifest })

/// The manifest's canonical stored text.
let encode (manifest: OrganizationManifest) : Result<string, Diagnostic list> =
    toRecord manifest
    |> Result.bind (fun record ->
        Record.encode Record.DefaultMaxBytes record
        |> Result.mapError (fun _ -> [ InvalidOrganizationManifest "the manifest is too large" ]))

/// The manifest's path inside the organization's folder:
/// `records/summa.organization/<id>.json`.
let path (organizationId: string) : Result<RelativePath, Diagnostic> =
    recordId organizationId
    |> Result.bind (fun id ->
        Layout.recordPath
            { Type = manifestType
              Partition = []
              Id = id }
        |> Result.mapError (fun _ -> InvalidOrganizationId organizationId))

let private basisOf =
    function
    | "accrual" -> Ok Accrual
    | "cash" -> Ok Cash
    | other -> Error $"'{other}' is not accrual or cash"

/// A manifest from a record body. Everything is checked: the fields, then
/// the same rules as a manifest about to be stored.
let ofBody (value: Json) : Result<OrganizationManifest, string> =
    let section name names (read: Json -> Codec.Decoded<'a>) =
        Codec.field name value |> Result.bind (fun inner -> Codec.closed names inner |> Result.bind (fun () -> read inner))

    Codec.closed
        [ "accounting"
          "company"
          "createdAt"
          "defaultCurrency"
          "displayName"
          "fiscal"
          "invoices"
          "organizationId"
          "slug"
          "storageVersion"
          "timeZone" ]
        value
    |> Result.bind (fun () ->
        match
            Codec.text "organizationId" value,
            Codec.text "slug" value,
            Codec.text "displayName" value,
            Codec.integer "storageVersion" value,
            Codec.instant "createdAt" value,
            Codec.text "defaultCurrency" value,
            Codec.text "timeZone" value
        with
        | Ok id, Ok slug, Ok name, Ok storage, Ok created, Ok currency, Ok zone ->
            let fiscal = section "fiscal" [ "yearStartMonth" ] (fun f -> Codec.integer "yearStartMonth" f |> Result.map (fun m -> { YearStartMonth = m }))

            let invoices =
                section "invoices" [ "defaultTermsDays"; "numberPrefix"; "paymentInstructions" ] (fun i ->
                    match Codec.text "numberPrefix" i, Codec.integer "defaultTermsDays" i, Codec.text "paymentInstructions" i with
                    | Ok prefix, Ok days, Ok instructions ->
                        Ok
                            { NumberPrefix = prefix
                              DefaultTermsDays = days
                              PaymentInstructions = instructions }
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e)

            let company =
                section "company" [ "address"; "email"; "legalName"; "taxId" ] (fun c ->
                    match Codec.text "legalName" c, Codec.text "address" c, Codec.optionalText "taxId" c, Codec.text "email" c with
                    | Ok name, Ok address, Ok taxId, Ok email ->
                        Ok
                            { LegalName = name
                              Address = address
                              TaxId = taxId
                              Email = email }
                    | Error e, _, _, _
                    | _, Error e, _, _
                    | _, _, Error e, _
                    | _, _, _, Error e -> Error e)

            let accounting =
                section "accounting" [ "basis"; "cashAccount"; "receivablesAccount"; "revenueAccount" ] (fun a ->
                    match Codec.text "basis" a |> Result.bind basisOf, Codec.text "receivablesAccount" a, Codec.text "revenueAccount" a, Codec.text "cashAccount" a with
                    | Ok basis, Ok receivables, Ok revenue, Ok cash ->
                        Ok
                            { Basis = basis
                              ReceivablesAccount = receivables
                              RevenueAccount = revenue
                              CashAccount = cash }
                    | Error e, _, _, _
                    | _, Error e, _, _
                    | _, _, Error e, _
                    | _, _, _, Error e -> Error e)

            match fiscal, company, invoices, accounting with
            | Ok fiscal, Ok company, Ok invoices, Ok accounting ->
                let manifest =
                    { OrganizationId = id
                      Slug = slug
                      DisplayName = name
                      StorageVersion = storage
                      CreatedAt = created
                      DefaultCurrency = currency
                      TimeZone = zone
                      Fiscal = fiscal
                      Company = company
                      Invoices = invoices
                      Accounting = accounting }

                match problems manifest with
                | [] -> Ok manifest
                | first :: _ -> Error(describe first)
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e
        | Error e, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _
        | _, _, Error e, _, _, _, _
        | _, _, _, Error e, _, _, _
        | _, _, _, _, Error e, _, _
        | _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, Error e -> Error e)

/// Why a stored object failed Arca's integrity checks, in words.
let describeIntegrity =
    function
    | IntegrityFailure.Invalid error -> $"not a canonical record (%A{error})"
    | IntegrityFailure.IdentityMismatch(expected, found) -> $"the record says it is '{found}' but its file names '{expected}'"
    | IntegrityFailure.TypeMismatch(expected, found) -> $"a {found} record where a {expected} belongs"
    | IntegrityFailure.UnsupportedSchema access -> $"the schema version is not readable (%A{access})"
    | IntegrityFailure.HashMismatch _ -> "the record changed since it was read"
    | IntegrityFailure.ImmutableChanged _ -> "an immutable record changed"

/// The manifest stored at `stored`, checked as the manifest of
/// `organizationId`: a canonical record of the right type, id and readable
/// schema version (Arca's integrity checks), at the path that id names, whose
/// body is a valid manifest of that organization.
let decode (organizationId: string) (stored: StoredObject) : Result<OrganizationManifest, Diagnostic> =
    let where = RelativePath.render stored.Path

    recordId organizationId
    |> Result.bind (fun id ->
        let key =
            { Type = manifestType
              Partition = []
              Id = id }

        if Layout.recordPath key <> Ok stored.Path then
            Error(InvalidStoredRecord(where, $"not the manifest path of '{organizationId}'"))
        else
            Integrity.validate key schema Record.DefaultMaxBytes stored
            |> Result.mapError (describeIntegrity >> fun detail -> InvalidStoredRecord(where, detail))
            |> Result.bind (fun valid ->
                ofBody valid.Record.Body
                |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail))
                |> Result.bind (fun manifest ->
                    if manifest.OrganizationId = organizationId then
                        Ok manifest
                    else
                        Error(InvalidStoredRecord(where, $"the manifest belongs to '{manifest.OrganizationId}'")))))
