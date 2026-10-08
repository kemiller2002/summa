/// Binary artifacts in their environment's store (set 0 §0.35, §0.38,
/// INV-DOC-011, DF-SUMMA-2026-0007): the PDF of an issued invoice, printed
/// through Folio and kept outside the financial records, which hold only its
/// reference, SHA-256, size and renderer.
///
/// A reference names the store it lives in, so an artifact recorded in one
/// environment is never taken for another's.
///
/// Pure: the bytes arrive from the application; storing them is an effect.
module Summa.Storage.Artifacts

open System
open System.Security.Cryptography
open Summa.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Sources
open Summa.Storage.Deployment

/// The largest PDF accepted: 10 MiB.
[<Literal>]
let MaxPdfBytes = 10485760L

/// How the PDF was produced: the browser's print dialog on Folio's portable profile.
[<Literal>]
let BrowserPrintRenderer = "folio/P0 browser print"

/// The browser database this deployment keeps its artifacts in.
let database (config: DeploymentConfig) =
    match config.Artifacts with
    | BrowserDatabase name -> name

/// An artifact's reference in this deployment's store: `browser-db:<database>/<sha256>`.
let reference (config: DeploymentConfig) (sha256: string) = $"browser-db:{database config}/{sha256}"

/// Whether a reference is in this deployment's own store.
let isOwn (config: DeploymentConfig) (reference: string) =
    reference.StartsWith($"browser-db:{database config}/", StringComparison.Ordinal)

/// The SHA-256 a reference in this store names, if it is one of this store's.
let shaOf (config: DeploymentConfig) (reference: string) =
    if isOwn config reference then Some(reference.Substring($"browser-db:{database config}/".Length)) else None

type PdfProblem =
    | Empty
    | TooLarge of limit: int64
    /// The bytes do not start with the PDF signature `%PDF-`.
    | NotAPdf
    /// The reference is in another environment's store.
    | ForeignStore of reference: string
    | Recording of Issuance.ArtifactProblem

let describe =
    function
    | Empty -> "The file is empty."
    | TooLarge limit -> $"The file is larger than {limit / 1048576L} MB."
    | NotAPdf -> "The file is not a PDF. Save the invoice as PDF from the print dialog, then attach that file."
    | ForeignStore reference -> $"The artifact {reference} belongs to another environment's store."
    | Recording(Issuance.UnknownArtifact id) -> $"There is no artifact {id}."
    | Recording(Issuance.AlreadyGenerated id) -> $"A different PDF is already recorded for {id}; an issued document never changes."
    | Recording(Issuance.InvalidArtifact why) -> why

/// The size a file may have before it is read at all.
let checkSize (size: int64) =
    if size <= 0L then Error Empty
    elif size > MaxPdfBytes then Error(TooLarge MaxPdfBytes)
    else Ok size

/// A PDF's lower-case SHA-256 and size, or why the bytes are not one.
let fingerprint (bytes: byte[]) =
    checkSize (int64 bytes.Length)
    |> Result.bind (fun size ->
        if bytes.Length < 5 || Text.Encoding.ASCII.GetString(bytes, 0, 5) <> "%PDF-" then Error NotAPdf
        else Ok(Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant(), size))

/// Records an invoice's PDF, stored in this deployment's store under its
/// SHA-256 (INV-DOC-011). Recording the same file again changes nothing.
let recordPdf (context: Ledger.Context) (config: DeploymentConfig) (invoiceId: string) (sha256: string, size: int64) (r: Payments.Receivables) =
    let file: Issuance.StoredFile =
        { Reference = reference config sha256
          Sha256 = sha256
          Size = size
          Renderer = BrowserPrintRenderer }

    Issuance.recordPdf context invoiceId file r |> Result.mapError Recording

/// An invoice's PDF artifact, if one is generated in this deployment's store:
/// its SHA-256. A PDF recorded in another environment's store is refused.
let storedPdf (config: DeploymentConfig) (r: Payments.Receivables) (invoiceId: string) =
    match r.Books.Artifacts.TryFind(Issuance.artifactId invoiceId InvoicePdf) with
    | Some a when a.Status = Generated ->
        match shaOf config a.Reference with
        | Some sha -> Ok(Some sha)
        | None -> Error(ForeignStore a.Reference)
    | _ -> Ok None
