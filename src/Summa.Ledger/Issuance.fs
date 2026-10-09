/// From draft to issued invoice (INV-DRAFT-006, INV-DRAFT-007, INV-REV,
/// INV-ISS, INV-DOC-006..015).
///
/// A draft is checked for readiness. Every blocker says what is wrong and
/// what resolves it. Once nothing blocks, the draft is submitted for review
/// at its current version; any later change returns it to Editing. A person
/// then issues exactly the version that was reviewed. Issuing is one
/// transition that commits all of these together:
/// - the invoice with its number and snapshots;
/// - its journal entry and obligation;
/// - the consumption of its sources;
/// - the document artifacts.
///
/// The HTML and JSON documents are reproduced from the snapshot and pinned
/// by hash. The PDF is produced after issue by Folio's print path; until
/// then it is a pending artifact, and its failure never undoes the issue.
module Summa.Ledger.Issuance

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

/// One reason a draft cannot be issued yet: a stable code, what is wrong,
/// and what resolves it (INV-DRAFT-007).
type Blocker =
    { Code: string
      Explanation: string
      Resolution: string }

let private blocker code explanation resolution =
    { Code = code
      Explanation = explanation
      Resolution = resolution }

let private ofLedgerProblem =
    function
    | PeriodNotOpen(year, month) ->
        blocker "period-closed" $"{year:D4}-{month:D2} is closed or locked." "Choose an issue date in an open period, or have an accountant reopen the period."
    | InactiveAccount account
    | UnknownAccount account -> blocker "invalid-account" $"Account {account} cannot be posted to." "Choose an active account."
    | other -> blocker "posting" $"The journal entry would be invalid: %A{other}." "Correct the lines so the entry balances."

let private ofInvoiceProblem =
    function
    | NoLines -> [ blocker "no-lines" "The invoice has no lines." "Add at least one line." ]
    | NoSubstantiveLine -> [ blocker "no-substantive-line" "No line charges anything." "Add a line with an amount." ]
    | UnknownCustomer c -> [ blocker "unknown-customer" $"Customer {c} does not exist." "Choose an existing customer." ]
    | InactiveCustomer c -> [ blocker "inactive-customer" $"Customer {c} is inactive." "Reactivate the customer or choose another." ]
    | TotalNotPositive -> [ blocker "total-not-positive" "The total is not positive." "Correct the lines; issue a credit memo for a negative correction." ]
    | InvalidRevenueAccount a -> [ blocker "invalid-revenue-account" $"{a} is not an active revenue account." "Choose an active revenue account for the line." ]
    | DuplicateInvoiceNumber n -> [ blocker "duplicate-number" $"Invoice number {n} is already used." "Choose another number or let Summa assign one." ]
    | UnknownDraft d -> [ blocker "unknown-draft" $"Draft {d} does not exist." "Open an existing draft." ]
    | UnknownInvoice i -> [ blocker "unknown-invoice" $"Invoice {i} does not exist." "Choose an issued invoice." ]
    | AlreadyIssued i -> [ blocker "already-issued" $"The draft was issued as {i}." "Open the issued invoice." ]
    | LedgerProblems problems -> problems |> List.map ofLedgerProblem
    | InvalidDiscount why -> [ blocker "invalid-discount" $"A discount is invalid: {why}." "Correct or remove the discount." ]
    | InvalidCorrection why -> [ blocker "invalid-correction" $"The correction is invalid: {why}." "Name an issued invoice of the same customer." ]
    | InvalidEngagement why -> [ blocker "invalid-engagement" $"The engagement is invalid: {why}." "Choose an engagement of this customer." ]
    | StaleDraft current -> [ blocker "stale-version" $"The draft changed; it is now version {current}." "Review the current version." ]
    | NegativeLine line -> [ blocker "negative-line" $"'{line}' has a negative quantity or price." "Use a credit memo or a discount instead of a negative line." ]
    | InvalidAdjustment why -> [ blocker "invalid-adjustment" $"An adjustment is invalid: {why}." "Correct the adjustment." ]

/// A context for checks that are never committed.
let private dryRun = { Who = "readiness"; When = DateTimeOffset.UnixEpoch; Source = "summa"; CorrelationId = None; Provenance = None }

// ---- Payment instructions profiles (INV-PAYINST) -----------------------------------

/// The newest version of a payment profile.
let latestProfile (books: Books) (profileId: string) =
    books.PaymentProfiles |> Map.toList |> List.map snd |> List.filter (fun p -> p.Id = profileId) |> List.sortBy _.Version |> List.tryLast

/// Whether text holds something that must not be stored in ordinary
/// records: a credential, or a full account number (nine or more digits in
/// a row). Masked references such as "account ending 6789" are fine
/// (INV-PAYINST-002).
let holdsSecret (text: string) =
    let longestDigits =
        text |> Seq.fold (fun (run, best) c -> if Char.IsAsciiDigit c then run + 1, max best (run + 1) else 0, best) (0, 0) |> snd

    longestDigits >= 9
    || text.Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries) |> Array.exists Summa.Contracts.ChronaBilling.V1.Validate.looksLikeCredential

/// Saves a new version of a payment profile; earlier versions stay as they
/// were, so issued invoices keep what they showed (INV-PAYINST-003).
let savePaymentProfile (context: Context) (profile: PaymentProfile) (books: Books) =
    let version = (latestProfile books profile.Id |> Option.map _.Version |> Option.defaultValue 0) + 1

    let problems =
        [ if String.IsNullOrWhiteSpace profile.Instructions then "the instructions are empty"
          if holdsSecret profile.Instructions || profile.Methods |> List.exists holdsSecret then
              "the instructions hold a credential or a full account number; show a masked reference instead" ]

    match latestProfile books profile.Id with
    | Some latest when { latest with Version = 0 } = { profile with Version = 0 } -> Ok books
    | _ when not problems.IsEmpty -> Error problems
    | _ ->
        let saved = { profile with Version = version }

        Ok
            { books with
                PaymentProfiles = books.PaymentProfiles.Add($"{saved.Id}@{saved.Version}", saved)
                Ledger = audit context "payment-profile-saved" $"{saved.Id}@{saved.Version}" books.Ledger }

/// The request with the customer's payment profile, when it has one, in
/// place of the organization's instructions.
let private resolvePayment (r: Receivables) (request: IssueRequest) =
    let profileId =
        r.Books.Drafts.TryFind request.DraftId
        |> Option.bind (fun d -> r.Books.Customers.TryFind d.CustomerId)
        |> Option.bind _.PaymentProfileId

    match profileId with
    | None -> Ok request
    | Some id ->
        match latestProfile r.Books id with
        | Some p ->
            Ok
                { request with
                    Issuer =
                        { request.Issuer with
                            PaymentInstructions = p.Instructions
                            PaymentMethods = p.Methods
                            PaymentProfile = Some $"{p.Id}@{p.Version}" } }
        | None -> Error [ blocker "unknown-payment-profile" $"Payment profile {id} does not exist." "Create the profile or clear it on the customer." ]

/// What issuing the draft would produce, without committing anything.
let private attempt (context: Context) (request: IssueRequest) (r: Receivables) =
    resolvePayment r request
    |> Result.bind (fun resolved -> issue context resolved r.Books |> Result.mapError (List.collect ofInvoiceProblem))

/// Every reason the draft cannot be issued now, in a stable order
/// (INV-DRAFT-006). Empty when it is ready.
let blockers (r: Receivables) (request: IssueRequest) : Blocker list =
    match r.Books.Drafts.TryFind request.DraftId with
    | None -> ofInvoiceProblem (UnknownDraft request.DraftId)
    | Some draft ->
        let issuer =
            [ if String.IsNullOrWhiteSpace request.Issuer.LegalName then
                  blocker "issuer-incomplete" "The issuer's legal name is missing." "Complete the company information in the organization settings."
              if String.IsNullOrWhiteSpace request.Issuer.Address then
                  blocker "issuer-incomplete" "The issuer's address is missing." "Complete the company information in the organization settings." ]

        let dates =
            match draft.DueDate with
            | Some due when due < request.IssueDate -> [ blocker "due-before-issue" "The due date is before the issue date." "Choose a later due date or clear it to use the terms." ]
            | _ -> []

        let sources =
            Billing.sourceProblems r draft.DraftId draft.Lines
            |> List.map (fun why -> blocker "source-unavailable" $"A billed source is not available: {why}." "Remove the line, or review its source and propose it again.")

        let rendering =
            match attempt dryRun request r with
            | Error found -> found
            | Ok(_, invoice) ->
                match Documents.render (Documents.ofInvoice invoice) with
                | Ok _ -> []
                | Error why -> [ blocker "render-failed" $"The document cannot be rendered: {why}." "Choose an available template." ]

        issuer @ dates @ sources @ rendering

/// The customer-facing document the draft would be issued as, from the same
/// snapshot and template issuing uses (INV-REV-001).
let preview (r: Receivables) (request: IssueRequest) =
    match blockers r request with
    | [] ->
        attempt dryRun request r
        |> Result.bind (fun (_, invoice) ->
            let doc = Documents.ofInvoice invoice

            Documents.render doc
            |> Result.mapError (fun why -> [ blocker "render-failed" why "Choose an available template." ])
            |> Result.map (fun html -> doc, html))
    | found -> Error found

/// What review shows besides the document (INV-REV-002, INV-REV-004):
/// where each line came from, and who changed the draft and when.
type Review =
    { Lines: (string * LineSource) list
      /// What an agent or integration assumed (INV-REV-003).
      Assumptions: string list
      Changes: AuditRecord list
      Version: int }

let review (r: Receivables) (draftId: string) =
    r.Books.Drafts.TryFind draftId
    |> Option.map (fun draft ->
        { Lines = draft.Lines |> List.map (fun l -> l.Description, l.Source)
          Assumptions = draft.Assumptions
          Changes = r.Books.Ledger.Audit |> List.filter (fun a -> a.Subject = draftId)
          Version = draft.Version })

let private setReview (context: Context) what (draft: DraftInvoice) (state: DraftReview) (r: Receivables) =
    { r with
        Books =
            { r.Books with
                Drafts = r.Books.Drafts.Add(draft.DraftId, { draft with Review = state })
                Ledger = auditChange context what draft.DraftId (applied (Some draft.Version) []) r.Books.Ledger } }

/// Submits the current version for review once nothing blocks it.
/// Idempotent for the same version.
let submitForReview (context: Context) (request: IssueRequest) (r: Receivables) =
    match r.Books.Drafts.TryFind request.DraftId with
    | None -> Error(ofInvoiceProblem (UnknownDraft request.DraftId))
    | Some draft when draft.Review = SubmittedForReview draft.Version -> Ok r
    | Some draft ->
        match blockers r request with
        | [] -> Ok(setReview context "invoice-ready-for-review" draft (SubmittedForReview draft.Version) r)
        | found -> Error found

/// Takes a draft back to Editing.
let returnToDraft (context: Context) (draftId: string) (r: Receivables) =
    match r.Books.Drafts.TryFind draftId with
    | None -> Error(ofInvoiceProblem (UnknownDraft draftId))
    | Some draft when draft.Review = Editing -> Ok r
    | Some draft -> Ok(setReview context "invoice-returned-to-draft" draft Editing r)

// ---- Artifacts ------------------------------------------------------------------------

let artifactId (invoiceId: string) (kind: ArtifactKind) =
    match kind with
    | InvoiceHtml -> $"{invoiceId}-html"
    | InvoiceJson -> $"{invoiceId}-json"
    | InvoicePdf -> $"{invoiceId}-pdf"

/// The artifacts an issued invoice starts with: its HTML and JSON
/// documents, pinned by hash, and a pending PDF.
let initialArtifacts (invoice: IssuedInvoice) : Result<InvoiceArtifact list, string> =
    let doc = Documents.ofInvoice invoice

    Documents.render doc
    |> Result.map (fun html ->
        let reproducible kind media (text: string) suffix =
            let sha, size = Documents.digest text

            { Id = artifactId invoice.InvoiceId kind
              InvoiceId = invoice.InvoiceId
              Kind = kind
              MediaType = media
              Reference = $"summa:invoice/{invoice.InvoiceId}/{suffix}"
              Sha256 = Some sha
              Size = Some size
              CreatedAt = invoice.IssuedAt
              TemplateId = invoice.Template.Id
              TemplateVersion = invoice.Template.Version
              Renderer = Some $"summa-documents/{invoice.Template.Version}"
              Status = Generated }

        [ reproducible InvoiceHtml "text/html; charset=utf-8" html "document.html"
          reproducible InvoiceJson "application/json" (Documents.toJson doc) "document.json"
          { Id = artifactId invoice.InvoiceId InvoicePdf
            InvoiceId = invoice.InvoiceId
            Kind = InvoicePdf
            MediaType = "application/pdf"
            Reference = ""
            Sha256 = None
            Size = None
            CreatedAt = invoice.IssuedAt
            TemplateId = invoice.Template.Id
            TemplateVersion = invoice.Template.Version
            Renderer = None
            Status = Pending } ])

let private withArtifacts (invoice: IssuedInvoice) (r: Receivables) =
    if r.Books.Artifacts.ContainsKey(artifactId invoice.InvoiceId InvoiceHtml) then
        Ok r
    else
        initialArtifacts invoice
        |> Result.mapError (fun why -> [ blocker "render-failed" why "Choose an available template." ])
        |> Result.map (fun artifacts ->
            { r with Books = { r.Books with Artifacts = artifacts |> List.fold (fun m a -> Map.add a.Id a m) r.Books.Artifacts } })

/// Issues the reviewed draft (INV-ISS-001). The request must name the
/// reviewed version, or the draft must still be at it. Retrying returns
/// the invoice already issued (INV-ISS-005).
let issueInvoice (context: Context) (request: IssueRequest) (r: Receivables) : Result<Receivables * IssuedInvoice, Blocker list> =
    match r.Books.IssuedFrom.TryFind request.DraftId with
    | Some invoiceId -> Ok(r, r.Books.Invoices[invoiceId])
    | None ->
        match r.Books.Drafts.TryFind request.DraftId with
        | None -> Error(ofInvoiceProblem (UnknownDraft request.DraftId))
        | Some draft ->
            let reviewed =
                match draft.Review, request.ExpectedVersion with
                | SubmittedForReview v, Some expected when v = expected && v = draft.Version -> true
                | SubmittedForReview v, None when v = draft.Version -> true
                | _ -> false

            if not reviewed then
                Error
                    [ blocker
                          "not-reviewed"
                          $"Version {draft.Version} of the draft has not been submitted for review."
                          "Submit the current version for review, then issue it." ]
            else
                match blockers r request with
                | _ :: _ as found -> Error found
                | [] ->
                    attempt context request r
                    |> Result.bind (fun (books, invoice) -> withArtifacts invoice { r with Books = books } |> Result.map (fun next -> next, invoice))

/// Accepts a reviewed proposal (`Billing.accept`) and gives the invoice its
/// artifacts in the same transition.
let acceptProposal (context: Context) (proposalId: string) (request: IssueRequest) (r: Receivables) =
    Billing.accept context proposalId request r
    |> Result.mapError (fun problems -> problems |> List.map (fun p -> blocker "proposal" $"%A{p}" "Review the proposal."))
    |> Result.bind (fun (next, invoice) -> withArtifacts invoice next |> Result.map (fun withDocs -> withDocs, invoice))

type ArtifactProblem =
    | UnknownArtifact of string
    | AlreadyGenerated of string
    | InvalidArtifact of string

/// What artifact storage returned for a PDF.
type StoredFile =
    { Reference: string
      Sha256: string
      Size: int64
      Renderer: string }

/// Records the PDF produced after issue. The same file again changes
/// nothing; a different file for a generated PDF is refused, so a retry
/// never makes a second document (INV-DOC-015).
let recordPdf (context: Context) (invoiceId: string) (file: StoredFile) (r: Receivables) =
    let id = artifactId invoiceId InvoicePdf

    match r.Books.Artifacts.TryFind id with
    | None -> Error(UnknownArtifact id)
    | Some a when a.Status = Generated && a.Sha256 = Some file.Sha256 && a.Reference = file.Reference -> Ok r
    | Some a when a.Status = Generated -> Error(AlreadyGenerated id)
    | Some _ when String.IsNullOrWhiteSpace file.Reference || file.Sha256.Length <> 64 || file.Size <= 0L -> Error(InvalidArtifact "a stored PDF needs its reference, SHA-256 and size")
    | Some a ->
        let generated =
            { a with
                Reference = file.Reference
                Sha256 = Some(file.Sha256.ToLowerInvariant())
                Size = Some file.Size
                Renderer = Some file.Renderer
                Status = Generated }

        Ok
            { r with
                Books =
                    { r.Books with
                        Artifacts = r.Books.Artifacts.Add(id, generated)
                        Ledger = audit context "invoice-pdf-generated" invoiceId r.Books.Ledger } }

/// Records that producing the PDF failed. The invoice stays issued, and the
/// pending artifact is the obligation to try again (INV-DOC-015).
let pdfFailed (context: Context) (invoiceId: string) (reason: string) (r: Receivables) =
    let id = artifactId invoiceId InvoicePdf

    match r.Books.Artifacts.TryFind id with
    | None -> Error(UnknownArtifact id)
    | Some a when a.Status = Generated -> Error(AlreadyGenerated id)
    | Some a ->
        Ok
            { r with
                Books =
                    { r.Books with
                        Artifacts = r.Books.Artifacts.Add(id, { a with Status = Failed reason })
                        Ledger = audit context "invoice-pdf-failed" invoiceId r.Books.Ledger } }

/// Whether an artifact still matches what was issued.
type ArtifactCheck =
    | Intact
    | Altered of expected: string * actual: string
    | NotYetGenerated

/// Re-renders an invoice's reproducible documents and compares them with
/// the hashes recorded at issue (INV-DOC-006, INV-DOC-008).
let verify (r: Receivables) (invoiceId: string) =
    match r.Books.Invoices.TryFind invoiceId with
    | None -> []
    | Some invoice ->
        let doc = Documents.ofInvoice invoice

        [ for kind in [ InvoiceHtml; InvoiceJson ] do
              match r.Books.Artifacts.TryFind(artifactId invoiceId kind) with
              | Some { Sha256 = Some expected } ->
                  let text =
                      match kind with
                      | InvoiceJson -> Ok(Documents.toJson doc)
                      | _ -> Documents.render doc

                  match text with
                  | Ok t ->
                      let actual = fst (Documents.digest t)
                      kind, (if actual = expected then Intact else Altered(expected, actual))
                  | Error why -> kind, Altered(expected, why)
              | _ -> kind, NotYetGenerated ]

/// Checks bytes fetched from artifact storage against the recorded hash.
let checkBytes (artifact: InvoiceArtifact) (bytes: byte array) =
    let actual = Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()

    match artifact.Sha256 with
    | Some expected when expected = actual -> Intact
    | Some expected -> Altered(expected, actual)
    | None -> NotYetGenerated
