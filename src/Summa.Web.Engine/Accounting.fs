/// The Summa accounting application (WI-0030) as a state machine.
///
/// `update` takes the model and one input and returns the next model plus
/// the effects to perform. An input is a person's event, a stored snapshot
/// read back, or the outcome of saving one. `view` projects the model into
/// the named values app/index.html binds to.
///
/// Every financial decision is a domain command from Summa.Ledger: the same
/// readiness, issue, payment and allocation rules the storage tests prove.
/// The page only describes them.
///
/// Pure. The clock arrives with each input, ids come from the books
/// themselves, and storage is an effect the kernel performs.
module Summa.Web.Engine.Accounting

open System
open System.Globalization
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Storage
open Summa.Web.Engine.Common

/// Where the books are kept in this browser.
[<Literal>]
let StorageKey = "summa.local.books"

/// The deployment's configuration document, beside the page (SUM0-037,
/// docs/deployment-configuration.md). A deployment replaces the
/// repository's copy.
[<Literal>]
let ConfigurationUrl = "./summa.deployment.json"

/// Where the page stands with its deployment's configuration.
[<NoComparison; NoEquality>]
type Configuration =
    | Configuring
    | Configured of Deployment.DeploymentConfig
    /// The configuration cannot be used; nothing runs.
    | Misconfigured of reason: string

/// Where a sign-in keeps the session (Fides retention): this page only, the
/// default, or this tab until it closes.
type KeepSignIn =
    | ThisPage
    | ThisTab

/// What Fides' client reported (WI-0035). The edge turns the client's
/// outcomes into these; no token is ever part of one.
type IdentityChange =
    /// The provider resolved who signed in: provider, stable subject, login.
    | IdentitySignedIn of provider: string * subject: string * login: string
    | IdentitySigningIn
    /// Signed out, with Fides' reason code when there is one.
    | IdentitySignedOut of code: string option
    | IdentityProviderUnavailable

/// Who signed in: the actor is the provider's stable subject
/// (`github:<numeric id>`); the login is only a display name (SUM0-004).
type SignedInPerson = { ActorId: string; Login: string }

/// Where to go after a sign-in that left for GitHub (WI-0043): the page
/// comes back at the registered redirect address, without its fragment, so
/// the target was kept in this tab before leaving.
type ReturnTarget =
    /// The page did not come back from GitHub: the address says where to go.
    | NotReturning
    /// Back from GitHub; this tab's kept target has not been read yet.
    | AwaitingTarget
    | TargetRead of location: string option

/// Sign-in, for a deployment whose books live on GitHub (WI-0035).
type SignInState =
    /// Local books: no one signs in.
    | NotRequired
    /// Restoring a kept session, or completing the provider's callback.
    | Restoring
    | SignedOut of notice: string option
    /// Leaving for the provider's sign-in page.
    | LeavingForProvider
    | SignedInAs of SignedInPerson
    | ProviderUnavailable

type CustomerForm =
    { Name: string
      BillingName: string
      Address: string
      Email: string
      TermsDays: string
      /// not-assessed, taxable or exempt (INV-ADJ-005): what the person says.
      TaxStatus: string
      TaxJurisdiction: string
      /// The exemption certificate or reference, for an exempt customer.
      TaxEvidence: string }

let emptyCustomer =
    { Name = ""
      BillingName = ""
      Address = ""
      Email = ""
      TermsDays = ""
      TaxStatus = "not-assessed"
      TaxJurisdiction = ""
      TaxEvidence = "" }

type LineForm =
    { Key: string
      Description: string
      Hours: string
      Rate: string
      /// The person marked the line taxable (INV-ADJ-005).
      Taxable: bool }

/// A tax the person enters on the invoice (INV-ADJ-005, WI-0044). Summa
/// never works it out: the amount, the code and where the rate came from
/// are all what the person typed.
type TaxForm =
    { Code: string
      Amount: string
      /// The liability account's code the tax posts to.
      AccountCode: string
      Jurisdiction: string
      /// The rate as a percentage, such as 8.875, if the person gives one.
      Rate: string
      RateSource: string
      Evidence: string
      /// Already inside the line prices, rather than added to them.
      Inclusive: bool }

let noTax =
    { Code = ""
      Amount = ""
      AccountCode = "2300"
      Jurisdiction = ""
      Rate = ""
      RateSource = ""
      Evidence = ""
      Inclusive = false }

/// A rate a person changes on a proposal's line, with the reason (INV-RATE-004, v0.4 §7).
type RateForm = { Line: string; Rate: string; Reason: string }

type DraftForm =
    { DraftId: string option
      CustomerId: string
      Lines: LineForm list
      PurchaseOrder: string
      Notes: string
      Tax: TaxForm }

type PaymentForm =
    { InvoiceId: string
      Amount: string
      Date: string
      Method: string
      Reference: string }

/// A credit memo raised against the invoice shown, and applied to it.
type CreditForm = { Amount: string; Reason: string }

type EngagementForm =
    { CustomerId: string
      Name: string
      FixedFee: string }

/// A payment received on account, before it is applied (v0.4 §13).
type ReceiptForm =
    { CustomerId: string
      Amount: string
      Date: string
      Method: string
      Reference: string }

type CompanyForm =
    { LegalName: string
      Address: string
      Email: string
      PaymentInstructions: string }

/// The `data-files-input` the page's PDF picker carries.
[<Literal>]
let PdfInput = "invoicePdf"

/// The files pack's largest single read (Limen MAX_READ_BYTES).
[<Literal>]
let ChunkBytes = 1048576

/// The optional packs the kernel offered and the engine selected.
type Packs = { Files: bool; Store: bool }

/// This environment's artifact store in the browser (Limen's store pack).
type ArtifactStore =
    | StoreClosed
    | StoreOpening
    | StoreOpen
    | StoreUnavailable of reason: string

/// A file the person picked, as the files pack describes it.
type PickedFile =
    { Id: string
      Name: string
      Size: int64
      Type: string }

/// Storing or fetching an invoice's PDF (INV-DOC-011).
type PdfWork =
    | PdfIdle
    /// Reading the picked file, one slice at a time.
    | PdfReading of invoiceId: string * file: string * size: int64 * chunks: byte[] list
    /// The checked bytes are being written to the artifact store.
    | PdfStoring of invoiceId: string * sha256: string * size: int64
    /// The stored PDF is being read back to offer as a download.
    | PdfFetching of fileName: string

/// Where the books stand in this browser.
type Storage =
    /// Asked the browser for the stored snapshot, or the store for the books.
    | Loading
    | Ready
    /// The stored snapshot failed its checks; nothing runs on it.
    | Untrustworthy of problems: string list
    /// Books on GitHub (WI-0037): the organization is not set up. Whether
    /// this person may set it up, or why not.
    | NotSetUp of mayFound: bool * reason: string option
    /// No listed administrator on the roster: nothing is granted until one
    /// confirms themself (`canConfirm`: this person may).
    | AwaitingAdministrator of canConfirm: bool
    /// The organization manifest is at an older schema; an administrator
    /// migrates it first.
    | Outdated of mayMigrate: bool
    /// Signed in, but not on the organization's roster.
    | NotAMember
    /// The store could not be reached or refused; why, in words.
    | Unreachable of reason: string

/// The books as the store opened them for the signed-in person.
type StoredBooks =
    { Manifest: Organization.OrganizationManifest
      Books: Receivables
      Capabilities: Set<Summa.Access.Access.Capability>
      /// Why the books may only be read, if they may.
      ReadOnly: string list }

/// Changes made here that GitHub does not have yet (WI-0037): how many wait
/// to be sent, the one that needs the person (and why), and anything the
/// person should know about where they are kept.
type Unsent =
    { Waiting: int
      /// The change that stops the others: its place in line, what it was,
      /// and why it was not sent.
      Blocked: (int64 * string * string) option
      Note: string option
      /// Unsent changes this browser holds that another sign-in made, or
      /// that were kept before Summa recorded who made them. They are held,
      /// never sent as this person or dropped, until the person chooses
      /// (Arca 0.4.0, LCP-070).
      Foreign: int }

let noneUnsent = { Waiting = 0; Blocked = None; Note = None; Foreign = 0 }

/// Checking the books on GitHub against their history (WI-0037): records
/// edited outside Summa are shown, never trusted or put right silently.
type BooksCheck =
    | NotChecked
    | Checking
    | Checked of findings: string list

/// Why the store did not open the books.
type BooksProblem =
    | BooksNotSetUp of mayFound: bool * reason: string option
    | BooksAwaitAdministrator of canConfirm: bool
    | BooksOutdated of mayMigrate: bool
    | BooksNotForYou
    | BooksUnreachable of reason: string
    | BooksUnusable of problems: string list


[<NoComparison; NoEquality>]
type Model =
    { Configuration: Configuration
      Storage: Storage
      Manifest: Organization.OrganizationManifest option
      Books: Receivables option
      /// Where the person is (WI-0041): the place the URL names.
      Place: Routes.Place
      /// Why the URL names no place the person can open, if it does not.
      Unrouted: Limen.Routing.RouteError option
      /// The canonical location the browser shows.
      Router: Limen.Routing.RouterState
      /// The page's own address, for "Copy link".
      Page: Limen.Routing.PageLocation option
      /// The link to copy by hand when the browser refused to copy it.
      CopyFallback: string option
      Today: DateOnly
      Customer: CustomerForm
      Draft: DraftForm
      Payment: PaymentForm
      Company: CompanyForm
      Credit: CreditForm
      Engagement: EngagementForm
      Packs: Packs
      Artifacts: ArtifactStore
      /// The reason or resolution typed on an invoice's follow-up.
      FollowUpNote: string
      Receipt: ReceiptForm
      Pdf: PdfWork
      Blockers: Issuance.Blocker list
      Notice: string option
      Error: string option
      /// Draws keys for form rows, so a sequence of inputs always gives the same keys.
      Counter: int
      /// Saves the browser has not confirmed.
      Unsaved: int
      SignIn: SignInState
      /// Keep the session in this tab rather than this page only.
      KeepInTab: bool
      Return: ReturnTarget
      /// What the signed-in person may do in books on GitHub.
      Capabilities: Set<Summa.Access.Access.Capability>
      /// Why books on GitHub may only be read, if they may.
      ReadOnly: string list
      Unsent: Unsent
      Check: BooksCheck
      /// The editor's unsaved form as this tab kept it, waiting for its
      /// draft (or the new invoice) to be opened again (WI-0044).
      KeptEditor: DraftForm option
      ProposalRate: RateForm }

let private newLine (counter: int) =
    let key, next = nextKey "line" counter
    { Key = key; Description = ""; Hours = "1"; Rate = ""; Taxable = false }, next

let private emptyDraft counter =
    let line, next = newLine counter

    { DraftId = None
      CustomerId = ""
      Lines = [ line ]
      PurchaseOrder = ""
      Notes = ""
      Tax = noTax },
    next

let initial =
    let draft, counter = emptyDraft 0

    { Configuration = Configuring
      Storage = Loading
      Manifest = None
      Books = None
      Place = Routes.Home
      Unrouted = None
      Router = Limen.Routing.Navigation.initial
      Page = None
      CopyFallback = None
      Today = DateOnly(2000, 1, 1)
      Customer = emptyCustomer
      Draft = draft
      Payment = { InvoiceId = ""; Amount = ""; Date = ""; Method = "ach"; Reference = "" }
      Company = { LegalName = ""; Address = ""; Email = ""; PaymentInstructions = "" }
      Credit = { Amount = ""; Reason = "" }
      Engagement = { CustomerId = ""; Name = ""; FixedFee = "" }
      Packs = { Files = false; Store = false }
      Artifacts = StoreClosed
      FollowUpNote = ""
      Receipt = { CustomerId = ""; Amount = ""; Date = ""; Method = "ach"; Reference = "" }
      Pdf = PdfIdle
      SignIn = NotRequired
      KeepInTab = false
      Return = NotReturning
      Capabilities = Set.empty
      ReadOnly = []
      Unsent = noneUnsent
      Check = NotChecked
      KeptEditor = None
      ProposalRate = { Line = "0"; Rate = ""; Reason = "" }
      Blockers = []
      Notice = None
      Error = None
      Counter = counter
      Unsaved = 0 }

/// Who acts and when, for every command.
type Ctx = { Now: DateTimeOffset; Actor: string }

type Msg =
    /// The kernel started, at this address (a deep link, or the home page).
    | Started of Limen.Routing.PageLocation
    /// Back, Forward or a followed link moved the browser to this address.
    | LocationChanged of Limen.Routing.PageLocation
    /// The configuration document's text, or why it could not be read.
    | ConfigurationRead of Result<string, string>
    | IdentityChanged of IdentityChange
    | SignInRequested
    | SignOutRequested
    | KeepSignInToggled
    /// The return target this tab kept across the sign-in (WI-0043).
    | ReturnTargetRead of string option
    /// The store opened the books on GitHub (WI-0037).
    | BooksOpened of StoredBooks
    | BooksNotOpened of BooksProblem
    | FoundRequested
    | ConfirmRequested
    | MigrateRequested
    /// A command was committed; the books as they stand after it.
    | BooksCommitted of Receivables
    | ManifestCommitted of Organization.OrganizationManifest
    /// A command was not committed: why, and the books as they stand now,
    /// when the store could read them.
    | StoreRefused of reason: string * latest: (Organization.OrganizationManifest * Receivables) option
    /// A command could not reach GitHub: it is kept, to be sent later.
    | ChangeKept
    /// Everything waiting reached GitHub; the books as GitHub now holds them.
    | BooksSynchronized of Receivables
    /// What is still to be sent to GitHub.
    | UnsentChanged of Unsent
    | SendUnsentRequested
    | CheckRequested
    /// What checking the books against their history found, in words.
    | BooksChecked of findings: string list
    /// Give up the change that stops the others.
    | AbandonUnsentRequested
    /// Send the held changes another sign-in made, as this person.
    | ForeignUnsentSendRequested
    /// Discard the held changes another sign-in made.
    | ForeignUnsentDiscardRequested
    /// The stored snapshot, or None when this browser has none yet.
    | Loaded of string option
    | Saved of ok: bool
    | LinkCopyRequested
    | LinkCopied of ok: bool
    | InvoiceSearchChanged of string
    | InvoiceStatusToggled of string
    | InvoiceCustomerChosen of string
    | InvoiceFromChanged of string
    | InvoiceToChanged of string
    | InvoiceOverdueToggled
    | InvoiceSortChosen of string
    | InvoiceFiltersCleared
    | CustomerSearchChanged of string
    | CustomerSortChosen of string
    | CustomerInactiveToggled
    | ReceivablesAsOfChanged of string
    | ReceivablesCustomerChosen of string
    | InvoiceTabChosen of string
    | CustomerNameChanged of string
    | CustomerBillingNameChanged of string
    | CustomerAddressChanged of string
    | CustomerEmailChanged of string
    | CustomerTermsChanged of string
    | CustomerAdded
    | DraftCustomerChanged of string
    | LineDescriptionChanged of key: string * string
    | LineHoursChanged of key: string * string
    | LineRateChanged of key: string * string
    | LineAdded
    | LineRemoved of key: string
    | DraftPurchaseOrderChanged of string
    | DraftNotesChanged of string
    | DraftSubmitted
    | DraftIssued
    | PrintRequested
    | PaymentAmountChanged of string
    | PaymentDateChanged of string
    | PaymentMethodChanged of string
    | PaymentReferenceChanged of string
    | PaymentRecorded
    | CompanyLegalNameChanged of string
    | CompanyAddressChanged of string
    | CompanyEmailChanged of string
    | CompanyPaymentChanged of string
    | CompanySaved
    | ResetConfirmed
    | CreditAmountChanged of string
    | CreditReasonChanged of string
    | CreditMemoIssued
    | PaymentSearchChanged of string
    | PaymentCustomerChosen of string
    | PaymentFromChanged of string
    | PaymentToChanged of string
    | PaymentUnappliedToggled
    | PaymentSortChosen of string
    | PaymentFiltersCleared
    | CustomerTabChosen of string
    | CreditMemosCustomerChosen of string
    | EngagementsCustomerChosen of string
    | EngagementCustomerChanged of string
    | EngagementNameChanged of string
    | EngagementFeeChanged of string
    | EngagementAdded
    | LedgerAccountChosen of string
    | LedgerFromChanged of string
    | LedgerToChanged of string
    | ReportAsOfChanged of string
    | IncomeFromChanged of string
    | IncomeToChanged of string
    | IncomeBasisChosen of string
    /// A common date range (v0.4 §24): this-month, last-month, this-quarter,
    /// last-quarter, year-to-date or last-year.
    | IncomeRangeChosen of string
    | PeriodClosed
    | PeriodLocked
    | PeriodReopened
    /// Which optional packs the kernel offered (files, store).
    | PacksNegotiated of Packs
    | ArtifactStoreOpened of Result<unit, string>
    /// The person picked a file in the PDF input, or cleared it.
    | PdfPicked of PickedFile option
    /// A slice of the picked file, base64, and whether it was the last.
    | PdfChunkRead of Result<string * bool, string>
    | PdfStored of Result<unit, string>
    | PdfDownloadRequested
    /// The stored PDF's bytes (base64), or None when this browser no longer has them.
    | PdfFetched of Result<string option, string>
    | FollowUpShowChosen of string
    /// A reminder was sent for this invoice, by whatever means.
    | ReminderRecorded of invoiceId: string
    | FollowUpNoteChanged of string
    | DisputeMarked
    | DisputeSettled
    | InboxCustomerChosen of string
    /// Apply the payment's unapplied amount to its customer's open invoices, oldest due first.
    | InboxApplied of paymentId: string
    /// Keep the payment's unapplied amount as the customer's credit.
    | InboxCredited of paymentId: string
    | ReceiptCustomerChanged of string
    | ReceiptAmountChanged of string
    | ReceiptDateChanged of string
    | ReceiptMethodChanged of string
    | ReceiptReferenceChanged of string
    | ReceiptRecorded
    /// trial-balance or journal, as CSV for the year shown.
    | CpaExportRequested of string
    | LineTaxableChanged of key: string * taxable: bool
    | DraftTaxCodeChanged of string
    | DraftTaxAmountChanged of string
    | DraftTaxAccountChosen of string
    | DraftTaxJurisdictionChanged of string
    | DraftTaxRateChanged of string
    | DraftTaxRateSourceChanged of string
    | DraftTaxEvidenceChanged of string
    | DraftTaxInclusiveChanged of inclusive: bool
    | CustomerTaxStatusChosen of string
    | CustomerTaxJurisdictionChanged of string
    | CustomerTaxEvidenceChanged of string
    /// The editor's form this tab kept, or None (WI-0044).
    | EditorRead of string option
    /// Give up the editor's unsaved changes: the draft as saved, or an empty invoice.
    | EditorDiscarded
    /// A person reviewed the proposal and issues it as an invoice (SUM4-042).
    | ProposalApproved of proposalId: string
    | ProposalAbandoned of proposalId: string
    | ProposalLineChosen of string
    | ProposalRateChanged of string
    | ProposalReasonChanged of string
    /// Change the chosen line's rate, with the reason (INV-RATE-004).
    | ProposalRateOverridden of proposalId: string

/// A change to the books to commit on the store: the message that made it,
/// which the store runs again on the books as they stand (`replay`), and
/// the capability it needs.
type BooksCommand =
    { Capability: Summa.Access.Access.Capability
      Summary: string
      Msg: Msg }

type AppEffect =
    | LoadConfiguration
    | LoadBooks
    | SaveBooks of snapshot: string
    | PrintPage
    /// Push or replace the browser's location (Limen Core Navigation).
    | Navigate of Limen.Routing.NavigationEffect
    /// Write text to the clipboard (Limen Core Clipboard).
    | CopyText of string
    /// Open (or create) this environment's artifact database.
    | OpenArtifactStore of database: string
    | ReadFileSlice of file: string * offset: int64 * length: int
    | ReleaseFile of file: string
    /// Put a PDF, base64, into the artifact database under its SHA-256.
    | PutArtifact of database: string * sha256: string * data: string * size: int64
    | GetArtifact of database: string * sha256: string
    /// Offer bytes (base64) to the person as a download.
    | OfferDownload of fileName: string * mediaType: string * data: string
    /// Set up Fides' client and complete the provider's callback (when the
    /// query carries one) or restore a kept session.
    | BeginIdentity of Deployment.IdentityConfig * query: (string * string) list
    | StartSignIn of KeepSignIn
    | EndSignIn
    /// Keep (or, with None, forget) the location to return to, in this tab
    /// only: it must survive the round trip to GitHub and nothing longer.
    | KeepReturnTarget of string option
    | ReadReturnTarget
    /// Open the organization's books on GitHub for the signed-in person.
    | OpenStoredBooks
    /// Set the organization up, with this person as its first administrator.
    | FoundStoredBooks
    | ConfirmAdministrator
    | MigrateStoredBooks
    | CommitBooks of BooksCommand
    | CommitManifest of Organization.OrganizationManifest
    /// Try now to send the changes GitHub does not have yet.
    | SendUnsent
    | AbandonUnsent of sequence: int64
    | SendForeignUnsent
    | DiscardForeignUnsent
    /// Check every financial record against its history on GitHub.
    | CheckBooks
    /// Keep (or, with None, forget) the editor's unsaved form in this tab,
    /// so a refresh does not lose it (WI-0044).
    | KeepEditor of string option
    | ReadEditor

// ---- Helpers ---------------------------------------------------------------------------

/// Every change made in the application is a person's (INV-PROV-001): the
/// application has no agent of its own.
let private context (ctx: Ctx) =
    { Who = ctx.Actor
      When = ctx.Now
      Source = "summa-app"
      CorrelationId = None
      Provenance =
        Some
            { ActorKind = "human"
              Agent = None
              ExecutionId = None
              SourceSystem = Some "summa-app"
              SourceId = None
              Reason = None } }

let private today (ctx: Ctx) = DateOnly.FromDateTime ctx.Now.UtcDateTime

let private parseAmount (text: string) = tryParse "USD" text

let private parseHours (text: string) =
    match Decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
    | true, hours when hours >= 0m && Decimal.Round(hours, 3) = hours -> Some(int64 (hours * 1000m))
    | _ -> None

let private parseDate (text: string) =
    match DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, d -> Some d
    | _ -> None

let private nextId (prefix: string) (taken: string -> bool) =
    Seq.initInfinite (fun i -> $"{prefix}-{i + 1:D4}") |> Seq.find (taken >> not)

let private companyForm (manifest: Organization.OrganizationManifest) =
    { LegalName = manifest.Company.LegalName
      Address = manifest.Company.Address
      Email = manifest.Company.Email
      PaymentInstructions = manifest.Invoices.PaymentInstructions }

/// Saves the books and manifest after a change: the snapshot or the reason
/// it cannot be stored.
let private save (model: Model) =
    match model.Manifest, model.Books with
    | Some manifest, Some books ->
        match LocalSnapshot.encode manifest books with
        | Ok text -> { model with Unsaved = model.Unsaved + 1 }, [ SaveBooks text ]
        | Error problems -> { model with Error = Some(problems |> List.map Diagnostics.describe |> String.concat "; ") }, []
    | _ -> model, []

/// Runs a command on the books; on success saves them and shows `notice`.
let private command (model: Model) (notice: string) (run: Receivables -> Result<Receivables, string>) =
    match model.Storage, model.Books with
    | Ready, Some books ->
        match run books with
        | Ok next -> save { model with Books = Some next; Notice = Some notice; Error = None }
        | Error why -> { model with Error = Some why; Notice = None }, []
    | _ -> { model with Error = Some "The books are not available."; Notice = None }, []

let private describe (problems: 'a list) = problems |> List.map (fun p -> $"%A{p}") |> String.concat "; "

/// The issue request for a draft, with the organization's defaults.
let private issueRequest (manifest: Organization.OrganizationManifest) (books: Receivables) (draftId: string) (issueDate: DateOnly) =
    let invoiceId = nextId "INV" books.Books.Invoices.ContainsKey
    let entryId = nextId "JE-INV" books.Books.Ledger.Entries.ContainsKey
    let obligationId = nextId "OBL" books.Books.Obligations.ContainsKey
    Organization.issueRequest manifest books.Books.Ledger draftId issueDate invoiceId entryId obligationId

let private nonEmptyText (text: string) = if text.Trim() = "" then None else Some(text.Trim())

/// A rate typed as a percentage (8.875), in hundredths of a basis point (88750).
let private parseRate (text: string) =
    match Decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) with
    | true, percent when percent >= 0m && percent <= 100m && Decimal.Round(percent * 10000m) = percent * 10000m -> Some(int (percent * 10000m))
    | _ -> None

let private rateText (hundredths: int) =
    (decimal hundredths / 10000m).ToString("0.####", CultureInfo.InvariantCulture)

/// The tax the editor describes (INV-ADJ-005): None when the person entered
/// none, or what is wrong with what they entered.
let private taxOf (books: Receivables) (tax: TaxForm) : Result<Adjustment option, string> =
    let code = tax.Code.Trim()

    match code, tax.Amount.Trim() with
    | "", "" -> Ok None
    | "", _ -> Error "A tax needs its code, such as NY-8.875 or VAT-STD."
    | _, amount ->
        let account = books.Books.Ledger.Accounts |> Map.tryFindKey (fun _ a -> a.Code = tax.AccountCode)

        match parseAmount amount, account, (if tax.Rate.Trim() = "" then Ok None else parseRate tax.Rate |> Option.map (Some >> Ok) |> Option.defaultValue (Error())) with
        | None, _, _
        | Some { Minor = 0L }, _, _ -> Error $"Tax {code} needs an amount such as 12.50. Summa does not work tax out."
        | Some money, _, _ when money.Minor < 0L -> Error $"Tax {code} cannot be negative."
        | _, None, _ -> Error $"Tax {code} needs a liability account; account {tax.AccountCode} is not in these books."
        | _, _, Error() -> Error $"Tax {code}'s rate is a percentage such as 8.875."
        | Some money, Some accountId, Ok rate ->
            Ok(
                Some
                    { Kind =
                        Tax
                            { Code = code
                              AccountId = accountId
                              Jurisdiction = nonEmptyText tax.Jurisdiction
                              RateSource = nonEmptyText tax.RateSource
                              RateHundredthBasisPoints = rate
                              Evidence = nonEmptyText tax.Evidence
                              Pricing = (if tax.Inclusive then TaxInclusive else TaxExclusive) }
                      Label = $"Tax {code}"
                      Amount = money }
            )

/// The draft the editor describes, or what is wrong with it.
let private draftOf (manifest: Organization.OrganizationManifest) (books: Receivables) (form: DraftForm) =
    let revenue = Organization.accountByCode books.Books.Ledger manifest.Accounting.RevenueAccount
    let taxCode = if form.Tax.Code.Trim() = "" then "taxable" else form.Tax.Code.Trim()

    let lines =
        form.Lines
        |> List.filter (fun l -> l.Description.Trim() <> "" || l.Rate.Trim() <> "")
        |> List.map (fun l ->
            match parseHours l.Hours, parseAmount l.Rate with
            | Some hours, Some rate when l.Description.Trim() <> "" ->
                Ok
                    { Description = l.Description.Trim()
                      QuantityThousandths = hours
                      UnitPrice = rate
                      RevenueAccountId = revenue
                      Project = None
                      WorkItem = None
                      Discount = None
                      Source = ManualLine
                      Rate = None
                      Tax = (if l.Taxable then Taxable taxCode else NotAssessed) }
            | _ -> Error $"'{l.Description}' needs a description, a quantity and a rate such as 150.00")

    match lines |> List.tryPick (function Error e -> Some e | Ok _ -> None), taxOf books form.Tax with
    | Some why, _
    | None, Error why -> Error why
    | None, Ok _ when form.CustomerId = "" -> Error "Choose a customer."
    | None, Ok tax ->
        let draftId = form.DraftId |> Option.defaultWith (fun () -> nextId "D" (fun id -> books.Books.Drafts.ContainsKey id || books.Books.IssuedFrom.ContainsKey id))
        let stored = books.Books.Drafts.TryFind draftId
        // The editor shows one tax; any other adjustment the draft holds stays.
        let kept = stored |> Option.map (fun d -> d.Adjustments |> List.filter (fun a -> match a.Kind with Tax _ -> false | _ -> true)) |> Option.defaultValue []

        Ok
            { DraftId = draftId
              CustomerId = form.CustomerId
              Currency = "USD"
              Lines = lines |> List.choose Result.toOption
              Adjustments = kept @ Option.toList tax
              Discounts = []
              Terms = None
              DueDate = None
              Corrects = None
              EngagementId = None
              Details =
                { noDetails with
                    PurchaseOrder = (if form.PurchaseOrder.Trim() = "" then None else Some(form.PurchaseOrder.Trim()))
                    CustomerNotes = (if form.Notes.Trim() = "" then None else Some(form.Notes.Trim())) }
              Assumptions = []
              Recipients = None
              Version = stored |> Option.map _.Version |> Option.defaultValue 0
              Review = stored |> Option.map _.Review |> Option.defaultValue Editing }

let private formOf (ledger: Summa.Ledger.Ledger.Ledger) (counter: int) (draft: DraftInvoice) =
    let lines, next =
        draft.Lines
        |> List.mapFold
            (fun c l ->
                let key, n = nextKey "line" c

                { Key = key
                  Description = l.Description
                  Hours = Documents.quantityText l.QuantityThousandths
                  Rate = Documents.amountText l.UnitPrice |> fun t -> t.Replace(",", "")
                  Taxable = (match l.Tax with Taxable _ -> true | _ -> false) },
                n)
            counter

    let tax =
        draft.Adjustments
        |> List.tryPick (fun a ->
            match a.Kind with
            | Tax t ->
                Some
                    { Code = t.Code
                      Amount = Documents.amountText a.Amount |> fun text -> text.Replace(",", "")
                      AccountCode = ledger.Accounts.TryFind t.AccountId |> Option.map _.Code |> Option.defaultValue t.AccountId
                      Jurisdiction = t.Jurisdiction |> Option.defaultValue ""
                      Rate = t.RateHundredthBasisPoints |> Option.map rateText |> Option.defaultValue ""
                      RateSource = t.RateSource |> Option.defaultValue ""
                      Evidence = t.Evidence |> Option.defaultValue ""
                      Inclusive = (t.Pricing = TaxInclusive) }
            | _ -> None)
        |> Option.defaultValue noTax

    { DraftId = Some draft.DraftId
      CustomerId = draft.CustomerId
      Lines = lines
      PurchaseOrder = draft.Details.PurchaseOrder |> Option.defaultValue ""
      Notes = draft.Details.CustomerNotes |> Option.defaultValue ""
      Tax = tax },
    next

// ---- The editor's unsaved form, kept in the tab (WI-0044) ------------------------------

/// The tab storage key the editor's unsaved form is kept under.
[<Literal>]
let EditorKey = "summa.editor"

/// The editor's form as text, with the counter its row keys were drawn from.
let encodeEditor (counter: int) (form: DraftForm) =
    let str (v: string) = Arca.Json.String v

    Arca.Json.objectOf
        [ "counter", Codec.number counter
          "draftId", Codec.optionalString form.DraftId
          "customerId", str form.CustomerId
          "purchaseOrder", str form.PurchaseOrder
          "notes", str form.Notes
          "lines",
          Arca.Json.Array(
              form.Lines
              |> List.map (fun l ->
                  Arca.Json.objectOf
                      [ "key", str l.Key
                        "description", str l.Description
                        "hours", str l.Hours
                        "rate", str l.Rate
                        "taxable", Arca.Json.Bool l.Taxable ])
          )
          "tax",
          Arca.Json.objectOf
              [ "code", str form.Tax.Code
                "amount", str form.Tax.Amount
                "accountCode", str form.Tax.AccountCode
                "jurisdiction", str form.Tax.Jurisdiction
                "rate", str form.Tax.Rate
                "rateSource", str form.Tax.RateSource
                "evidence", str form.Tax.Evidence
                "inclusive", Arca.Json.Bool form.Tax.Inclusive ] ]
    |> Arca.Json.canonicalText

/// The kept form and its counter, or None when the text is not one (a
/// kept form is only a convenience: one that cannot be read is dropped).
let decodeEditor (text: string) : (int * DraftForm) option =
    let line json =
        Codec.decode {
            let! key = Codec.text "key" json
            let! description = Codec.text "description" json
            let! hours = Codec.text "hours" json
            let! rate = Codec.text "rate" json
            let! taxable = Codec.flag "taxable" json
            return { Key = key; Description = description; Hours = hours; Rate = rate; Taxable = taxable }
        }

    let tax json =
        Codec.decode {
            let! code = Codec.text "code" json
            let! amount = Codec.text "amount" json
            let! account = Codec.text "accountCode" json
            let! jurisdiction = Codec.text "jurisdiction" json
            let! rate = Codec.text "rate" json
            let! rateSource = Codec.text "rateSource" json
            let! evidence = Codec.text "evidence" json
            let! inclusive = Codec.flag "inclusive" json

            return
                { Code = code
                  Amount = amount
                  AccountCode = account
                  Jurisdiction = jurisdiction
                  Rate = rate
                  RateSource = rateSource
                  Evidence = evidence
                  Inclusive = inclusive }
        }

    Arca.Json.parse text
    |> Result.mapError (fun _ -> "not JSON")
    |> Result.bind (fun json ->
        Codec.decode {
            let! counter = Codec.integer "counter" json
            let! draftId = Codec.optionalText "draftId" json
            let! customerId = Codec.text "customerId" json
            let! purchaseOrder = Codec.text "purchaseOrder" json
            let! notes = Codec.text "notes" json
            let! lines = Codec.list "lines" line json
            let! taxJson = Codec.field "tax" json
            let! tax = tax taxJson

            return
                counter,
                { DraftId = draftId
                  CustomerId = customerId
                  Lines = lines
                  PurchaseOrder = purchaseOrder
                  Notes = notes
                  Tax = tax }
        })
    |> Result.toOption
    |> Option.filter (fun (_, form) -> not form.Lines.IsEmpty)

let private editLine (key: string) (change: LineForm -> LineForm) (model: Model) =
    { model with Draft = { model.Draft with Lines = model.Draft.Lines |> List.map (fun l -> if l.Key = key then change l else l) } }, []

// ---- Places (WI-0041) -------------------------------------------------------------------

/// Whether the books live on GitHub (WI-0037).
let private onGitHub (model: Model) =
    match model.Configuration with
    | Configured config -> config.Location.IsSome
    | _ -> false

let private everything =
    Routes.Member(Summa.Access.Access.allCapabilities |> List.map Summa.Access.Access.capabilityName |> Set.ofList)

/// Who is looking. Local books are their owner's own: every capability.
/// Where sign-in is required, no one is anyone until signed in. A signed-in
/// person's capabilities come from the organization's roster, which the
/// GitHub store reads (WI-0037); until it does, the books are not open.
let viewerOf (model: Model) =
    match model.SignIn with
    | NotRequired -> everything
    // Books on GitHub: what the roster grants, once the store has opened them.
    | SignedInAs _ when model.Storage = Ready ->
        Routes.Member(model.Capabilities |> Set.map Summa.Access.Access.capabilityName)
    | SignedInAs _ -> everything
    | Restoring
    | SignedOut _
    | LeavingForProvider
    | ProviderUnavailable -> Routes.Anonymous

/// A fragment that is not a route ("#main", the skip link's target) is an
/// in-page anchor: the place stays, and the address is put back.
let private isAnchor (page: Limen.Routing.PageLocation) = page.Hash.Length > 1 && not (page.Hash.StartsWith "#/")

let private navigation (effect: Limen.Routing.NavigationEffect option) = effect |> Option.map Navigate |> Option.toList

/// Whether a kept editor form belongs to this place: its draft, still in
/// the books, or a new invoice (for the same customer, if the link names one).
let private fits (place: Routes.Place) (books: Receivables option) (form: DraftForm) =
    match place with
    | Routes.Draft draftId -> form.DraftId = Some draftId && books |> Option.exists (fun b -> b.Books.Drafts.ContainsKey draftId)
    | Routes.NewInvoice customerId -> form.DraftId.IsNone && (customerId.IsNone || customerId = Some form.CustomerId)
    | _ -> false

/// Prepares what a place shows when the person arrives at it: the draft it
/// opens, the invoice a payment is for, an empty new invoice.
let private enter (place: Routes.Place) (model: Model) =
    // The editor's unsaved form, kept across a refresh, for this place (WI-0044).
    let restore (form: DraftForm) =
        { model with
            Draft = form
            Blockers = []
            KeptEditor = None
            Notice = Some "Your unsaved changes were restored." }

    match place, model.KeptEditor with
    | Routes.Invoice(invoiceId, _), _ when model.Payment.InvoiceId <> invoiceId ->
        { model with
            Payment = { model.Payment with InvoiceId = invoiceId; Amount = ""; Reference = ""; Date = "" }
            Credit = { Amount = ""; Reason = "" } }
    | (Routes.Draft _ | Routes.NewInvoice _), Some kept when fits place model.Books kept -> restore kept
    | Routes.Draft draftId, _ when model.Draft.DraftId <> Some draftId ->
        match model.Books |> Option.bind (fun b -> b.Books.Drafts.TryFind draftId |> Option.map (fun d -> b, d)) with
        | Some(books, draft) ->
            let form, counter = formOf books.Books.Ledger model.Counter draft
            { model with Draft = form; Counter = counter; Blockers = [] }
        | None -> model
    | Routes.Proposal _, _ -> { model with ProposalRate = { Line = "0"; Rate = ""; Reason = "" } }
    // Back at a new invoice from elsewhere: the one being typed is kept (v0.4 §39).
    | Routes.NewInvoice customerId, _ when model.Draft.DraftId.IsNone && (customerId.IsNone || customerId = Some model.Draft.CustomerId) -> model
    | Routes.NewInvoice customerId, _ ->
        let draft, counter = emptyDraft model.Counter
        { model with Draft = { draft with CustomerId = defaultArg customerId "" }; Counter = counter; Blockers = [] }
    | _ -> model

/// Goes to a place the engine chose: `operation` is RouteCodec.navigate (a
/// push, for another place) or RouteCodec.refine (a replace, for the same
/// view refined).
let private move operation (place: Routes.Place) (model: Model) =
    match operation Routes.codec model.Router place with
    | Ok(router, effect) ->
        let moved = { model with Router = router; Place = place; Unrouted = None; CopyFallback = None }
        (if place <> model.Place then enter place moved else moved), navigation effect
    // A place built from the books' own ids always formats; stay put if not.
    | Error _ -> model, []

let private goTo place model = move Limen.Routing.RouteCodec.navigate place model
let private refineTo place model = move Limen.Routing.RouteCodec.refine place model

/// Adopts the address the browser reports (a deep link, Back, Forward, a
/// followed link). Never a push: at most a replace to its canonical form.
let private adopt (page: Limen.Routing.PageLocation) (model: Model) =
    let model = { model with Page = Some page; CopyFallback = None }

    match model.Router.Current with
    | Some current when isAnchor page -> model, [ Navigate(Limen.Routing.NavigationEffect.Replace current) ]
    | _ ->
        let location = if isAnchor page then "/" else Routes.locationOf page
        let viewer = viewerOf model
        let router, resolution, effect = Limen.Routing.RouteCodec.adopt Routes.codec (Routes.guard viewer) model.Router location

        let arrive (place: Routes.Place) router effects =
            let changed = place <> model.Place || model.Unrouted.IsSome

            let arrived =
                { model with
                    Router = router
                    Place = place
                    Unrouted = None
                    Notice = (if changed then None else model.Notice)
                    Error = (if changed then None else model.Error) }

            (if changed then enter place arrived else arrived), effects

        match resolution with
        // Not signed in: the sign-in page, keeping where the link pointed.
        | Ok(Routes.SignIn _ as place) when viewer = Routes.Anonymous -> arrive place router (navigation effect)
        // Signed in, or no sign-in needed: go straight to where the link pointed.
        | Ok(Routes.SignIn returnTo) ->
            let target = Routes.resume viewer returnTo
            let resumed, replace = Limen.Routing.Navigation.replace router target

            match Routes.parse viewer target with
            | Ok place -> arrive place resumed (navigation replace)
            | Error _ -> arrive Routes.Home resumed (navigation replace)
        | Ok place -> arrive place router (navigation effect)
        | Error problem -> { model with Router = router; Unrouted = Some problem; Notice = None; Error = None }, navigation effect

/// The address the browser shows, adopted again: who may see what changed.
/// That is the router's location once the engine has moved the browser
/// itself (the page reports only moves it did not make), else the page's.
let private readopt (model: Model) =
    match model.Page, model.Router.Current with
    | Some page, Some current -> adopt { page with Query = ""; Hash = Limen.Routing.Location.href Routes.mode current } model
    | Some page, None -> adopt page model
    | None, _ -> model, []

/// A query string as name and value pairs, decoded.
let queryPairs (query: string) =
    query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun pair ->
        let decode (text: string) = Uri.UnescapeDataString(text.Replace('+', ' '))

        match pair.IndexOf '=' with
        | -1 -> decode pair, ""
        | at -> decode (pair.Substring(0, at)), decode (pair.Substring(at + 1)))
    |> List.ofArray

/// What the sign-in page says for Fides' reason code.
let signInNotice (code: string) =
    match code with
    | "signed_out" -> "You signed out. This page no longer holds your GitHub token."
    | "expired" -> "Your session ended. Sign in again to continue."
    | "revoked" -> "Your session was ended, here or in another tab. Sign in again to continue."
    | "provider_denied" -> "GitHub did not sign you in: the request was declined there."
    | "state_invalid" -> "That sign-in did not start in this tab, so it was refused. Sign in again here."
    | "state_expired" -> "That sign-in took too long and expired. Sign in again."
    | "code_rejected" -> "GitHub refused the sign-in code. Sign in again."
    | "sign_in_failed" -> "Sign-in could not start. Try again."
    | other -> $"Sign-in did not complete ({other}). Try again."

/// Signed out: nothing of the person stays in memory. The page starts again
/// from its configuration, at the sign-in page for where it was.
let private signedOut (state: SignInState) (model: Model) =
    readopt
        { initial with
            Configuration = model.Configuration
            Page = model.Page
            Router = model.Router
            Place = model.Place
            Packs = model.Packs
            Today = model.Today
            SignIn = state }

/// Whether a page was opened with the provider's callback.
let isCallback (query: (string * string) list) =
    query |> List.exists (fun (name, _) -> name = "state" || name = "code" || name = "error")

/// Signed in: go where the person was going. Back from GitHub, that is the
/// target this tab kept, once it has been read; it is checked again against
/// what the person may now see, replaces the sign-in in history, and is
/// forgotten (SUM-LINK-008). Otherwise the address itself says.
let private returnAfterSignIn (model: Model) =
    match model.SignIn, model.Return with
    | SignedInAs _, TargetRead target ->
        let viewer = viewerOf model
        let destination = Routes.resume viewer target
        let router, replace = Limen.Routing.Navigation.replace model.Router destination

        let place =
            match Routes.parse viewer destination with
            | Ok place -> place
            | Error _ -> Routes.Home

        let arrived = { model with Router = router; Place = place; Unrouted = None; Return = NotReturning }
        (if place <> model.Place then enter place arrived else arrived), navigation replace @ [ KeepReturnTarget None ]
    | _, AwaitingTarget -> model, []
    | _ -> readopt model

let private invoiceList (model: Model) =
    match model.Place with
    | Routes.Invoices list -> list
    | _ -> Routes.allInvoices

let private customerList (model: Model) =
    match model.Place with
    | Routes.Customers list -> list
    | _ -> Routes.allCustomers

let private nonEmpty (text: string) = if text.Trim() = "" then None else Some(text.Trim())

let private paymentList (model: Model) =
    match model.Place with
    | Routes.Payments list -> list
    | _ -> Routes.allPayments

/// The common report ranges (v0.4 §24), relative to today.
let dateRange (today: DateOnly) (key: string) : (DateOnly * DateOnly) option =
    let monthStart (d: DateOnly) = DateOnly(d.Year, d.Month, 1)
    let quarterStart (d: DateOnly) = DateOnly(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1)
    let thisMonth = monthStart today
    let thisQuarter = quarterStart today

    match key with
    | "this-month" -> Some(thisMonth, today)
    | "last-month" -> Some(thisMonth.AddMonths -1, thisMonth.AddDays -1)
    | "this-quarter" -> Some(thisQuarter, today)
    | "last-quarter" -> Some(thisQuarter.AddMonths -3, thisQuarter.AddDays -1)
    | "year-to-date" -> Some(DateOnly(today.Year, 1, 1), today)
    | "last-year" -> Some(DateOnly(today.Year - 1, 1, 1), DateOnly(today.Year - 1, 12, 31))
    | _ -> None

/// What a period close is blocked by (v0.4 §26): the trial balance at the
/// month's end must balance, and every payment received in the month must
/// be applied. Drafts are reported but do not block.
let periodBlockers (r: Receivables) (year: int, month: int) =
    let monthEnd = DateOnly(year, month, DateTime.DaysInMonth(year, month))
    let tb = Reports.trialBalance "USD" monthEnd r.Books.Ledger

    let unapplied =
        r.Payments |> Map.toList |> List.map snd |> List.filter (fun p -> p.DateReceived.Year = year && p.DateReceived.Month = month && (unallocated r p).Minor > 0L)

    [ if tb.TotalDebits <> tb.TotalCredits then "The trial balance at the month's end does not balance."
      match unapplied.Length with
      | 0 -> ()
      | 1 -> "One payment received this month is not fully applied."
      | n -> $"{n} payments received this month are not fully applied." ]

let private methodOf =
    function
    | "check" -> Check
    | "wire" -> Wire
    | "credit-card" -> CreditCard
    | "cash" -> PaymentMethod.Cash
    | "other" -> PaymentMethod.Other
    | _ -> Ach

/// An invoice's open balance and how far past due it is.
let private openInvoices (today: DateOnly) (r: Receivables) =
    r.Books.Invoices
    |> Map.toList
    |> List.map snd
    |> List.filter (fun i -> (outstanding r i).Minor > 0L)
    |> List.sortBy (fun i -> i.DueDate, i.Number)

/// Everything that needs someone's attention (v0.4 §31), each with where to act.
let workItems (today: DateOnly) (r: Receivables) =
    let invoice id = Routes.href (Routes.Invoice(id, Routes.InvoicePayments))
    let number id = r.Books.Invoices.TryFind id |> Option.map _.Number |> Option.defaultValue id

    [ for KeyValue(id, d) in r.Books.Drafts do
          match d.Review with
          | SubmittedForReview _ -> "Ready to issue", $"Draft {id} is reviewed and ready to issue.", Routes.href (Routes.Draft id)
          | Editing -> "Draft", $"Draft {id} is not yet submitted for review.", Routes.href (Routes.Draft id)
      for i in openInvoices today r do
          match (Lifecycle.followUp r i.InvoiceId).Dispute with
          | Disputed(reason, _) -> "Disputed", $"Invoice {i.Number} is disputed: {reason}", invoice i.InvoiceId
          | _ when isOverdue today r i -> "Overdue", $"Invoice {i.Number} is overdue by {today.DayNumber - i.DueDate.DayNumber} days.", invoice i.InvoiceId
          | _ -> ()
      for KeyValue(_, p) in r.Payments do
          if (unallocated r p).Minor > 0L then
              "Unapplied payment", $"Payment {p.Id} has {Documents.moneyText (unallocated r p)} not yet applied.", Routes.href (Routes.Inbox(Some p.CustomerId))
      for id, why in Lifecycle.attention r do
          "Delivery", $"Invoice {number id}: {why}.", invoice id
      for KeyValue(id, a) in r.Books.Artifacts do
          if a.Kind = Sources.InvoicePdf && a.Status = Sources.Pending then
              "PDF", $"Invoice {number a.InvoiceId} has no stored PDF yet.", Routes.href (Routes.Invoice(a.InvoiceId, Routes.Document))
      let lastMonth = DateOnly(today.Year, today.Month, 1).AddMonths -1

      let lastMonthName = lastMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture)

      if r.Books.Ledger.Periods.TryFind((lastMonth.Year, lastMonth.Month)) |> Option.defaultValue PeriodState.Open = PeriodState.Open then
          "Period", $"{lastMonthName} is still open.", Routes.href (Routes.Period(lastMonth.Year, lastMonth.Month)) ]

// ---- Update ----------------------------------------------------------------------------

let rec private apply (ctx: Ctx) (msg: Msg) (model: Model) : Model * AppEffect list =
    let model = { model with Today = today ctx }

    match msg with
    | Started page ->
        let started, effects = adopt page { model with Configuration = Configuring; Storage = Loading }
        started, LoadConfiguration :: ReadEditor :: effects
    | LocationChanged page -> adopt page model
    | ConfigurationRead(Error why) ->
        { model with Configuration = Misconfigured $"The deployment's configuration could not be read ({why})." }, []
    | ConfigurationRead(Ok document) ->
        match Deployment.parse document with
        | Error problem -> { model with Configuration = Misconfigured(Diagnostics.describe problem) }, []
        | Ok config when config.Location.IsSome ->
            match config.Identity with
            // Books on GitHub: who signs in comes first (WI-0035).
            | Some identity ->
                let query = model.Page |> Option.map (fun p -> queryPairs p.Query) |> Option.defaultValue []
                let returning = isCallback query

                let artifacts, opening =
                    if model.Packs.Store then StoreOpening, [ OpenArtifactStore(Artifacts.database config) ]
                    else StoreUnavailable "this browser offers no artifact store", []

                let restoring, effects =
                    readopt { model with Configuration = Configured config; SignIn = Restoring; Return = (if returning then AwaitingTarget else NotReturning); Artifacts = artifacts }

                restoring, BeginIdentity(identity, query) :: (if returning then [ ReadReturnTarget ] else []) @ opening @ effects
            | None -> { model with Configuration = Misconfigured "This deployment names a data location on GitHub but no way to sign in." }, []
        | Ok config when model.Packs.Store ->
            { model with Configuration = Configured config; Artifacts = StoreOpening }, [ LoadBooks; OpenArtifactStore(Artifacts.database config) ]
        | Ok config -> { model with Configuration = Configured config; Artifacts = StoreUnavailable "this browser offers no artifact store" }, [ LoadBooks ]
    | IdentityChanged change ->
        match model.SignIn, change with
        // Local books: Fides is not running, so nothing it says applies.
        | NotRequired, _ -> model, []
        | _, IdentitySignedIn(provider, subject, login) ->
            let signedIn, effects = returnAfterSignIn { model with SignIn = SignedInAs { ActorId = $"{provider}:{subject}"; Login = login } }
            // Signed in: open the books on GitHub (WI-0037), once.
            signedIn, effects @ (if model.SignIn = Restoring || model.SignIn = LeavingForProvider || (match model.SignIn with SignedOut _ | ProviderUnavailable -> true | _ -> false) then [ OpenStoredBooks ] else [])
        | _, IdentitySigningIn -> { model with SignIn = LeavingForProvider }, []
        | _, IdentitySignedOut code -> signedOut (SignedOut(code |> Option.map signInNotice)) model
        // Signed in: the session stays; GitHub is only unreachable for now.
        | SignedInAs _, IdentityProviderUnavailable ->
            { model with Notice = Some "GitHub sign-in is not reachable right now. Your session is kept." }, []
        | _, IdentityProviderUnavailable -> signedOut ProviderUnavailable model
    | SignInRequested ->
        match model.SignIn with
        | SignedOut _
        | ProviderUnavailable ->
            // The page leaves for GitHub and comes back without its address:
            // keep where to return in this tab first (SUM-LINK-008).
            let target =
                match model.Place with
                | Routes.SignIn returnTo -> returnTo
                | _ -> None

            { model with SignIn = LeavingForProvider }, [ KeepReturnTarget target; StartSignIn(if model.KeepInTab then ThisTab else ThisPage) ]
        | _ -> model, []
    | SignOutRequested ->
        match model.SignIn with
        | SignedInAs _ -> model, [ EndSignIn ]
        | _ -> model, []
    | KeepSignInToggled -> { model with KeepInTab = not model.KeepInTab }, []
    | BooksOpened opened ->
        readopt
            { model with
                Storage = Ready
                Manifest = Some opened.Manifest
                Books = Some opened.Books
                Company = companyForm opened.Manifest
                Capabilities = opened.Capabilities
                ReadOnly = opened.ReadOnly }
    | BooksNotOpened problem ->
        { model with
            Storage =
                match problem with
                | BooksNotSetUp(mayFound, reason) -> NotSetUp(mayFound, reason)
                | BooksAwaitAdministrator canConfirm -> AwaitingAdministrator canConfirm
                | BooksOutdated mayMigrate -> Outdated mayMigrate
                | BooksNotForYou -> NotAMember
                | BooksUnreachable reason -> Unreachable reason
                | BooksUnusable problems -> Untrustworthy problems },
        []
    | FoundRequested ->
        match model.Storage with
        | NotSetUp(true, _) -> { model with Storage = Loading }, [ FoundStoredBooks ]
        | _ -> model, []
    | ConfirmRequested ->
        match model.Storage with
        | AwaitingAdministrator true -> { model with Storage = Loading }, [ ConfirmAdministrator ]
        | _ -> model, []
    | MigrateRequested ->
        match model.Storage with
        | Outdated true -> { model with Storage = Loading }, [ MigrateStoredBooks ]
        | _ -> model, []
    | BooksCommitted books ->
        let unsaved = max 0 (model.Unsaved - 1)
        // While later changes are still on their way, the page keeps showing them.
        { model with Unsaved = unsaved; Books = (if unsaved = 0 then Some books else model.Books) }, []
    | ManifestCommitted manifest ->
        { model with Unsaved = max 0 (model.Unsaved - 1); Manifest = Some manifest }, []
    | StoreRefused(reason, latest) ->
        let model =
            match latest with
            | Some(manifest, books) -> { model with Manifest = Some manifest; Books = Some books; Company = companyForm manifest }
            | None -> model

        { model with Unsaved = max 0 (model.Unsaved - 1); Error = Some reason; Notice = None }, []
    | UnsentChanged unsent -> { model with Unsent = unsent }, []
    // The page keeps showing it; it is sent when GitHub can be reached.
    | ChangeKept -> { model with Unsaved = max 0 (model.Unsaved - 1) }, []
    | BooksSynchronized books -> { model with Books = (if model.Unsaved = 0 then Some books else model.Books) }, []
    | SendUnsentRequested -> model, (if model.Unsent.Waiting > 0 then [ SendUnsent ] else [])
    | CheckRequested when onGitHub model && model.Storage = Ready && model.Check <> Checking -> { model with Check = Checking }, [ CheckBooks ]
    | CheckRequested -> model, []
    | BooksChecked findings -> { model with Check = Checked findings }, []
    // The page offers to give up only the change that is blocked.
    | ForeignUnsentSendRequested -> model, (if model.Unsent.Foreign > 0 then [ SendForeignUnsent ] else [])
    | ForeignUnsentDiscardRequested -> model, (if model.Unsent.Foreign > 0 then [ DiscardForeignUnsent ] else [])
    | AbandonUnsentRequested ->
        match model.Unsent.Blocked with
        | Some(sequence, _, _) -> model, [ AbandonUnsent sequence ]
        | None -> model, []
    | ReturnTargetRead target ->
        match model.Return with
        | AwaitingTarget -> returnAfterSignIn { model with Return = TargetRead target }
        | _ -> model, []
    | Loaded None ->
        let company: Organization.CompanyInformation =
            { LegalName = "Demo Consulting LLC"
              Address = "1 Example Street\nSpringfield"
              TaxId = None
              Email = "billing@demo.example" }

        match LocalSnapshot.start (context ctx) "Demo Consulting" company "ACH to account ending 0000 (demo)" with
        | Ok(manifest, books) ->
            save (
                enter
                    model.Place
                    { model with
                        Storage = Ready
                        Manifest = Some manifest
                        Books = Some books
                        Company = companyForm manifest
                        Notice = Some "A new set of books was started in this browser." }
            )
        | Error problems -> { model with Storage = Untrustworthy [ describe problems ] }, []
    | Loaded(Some text) ->
        match LocalSnapshot.decode text with
        | Ok restored when restored.Problems.IsEmpty ->
            let opened =
                { model with
                    Storage = Ready
                    Manifest = Some restored.Manifest
                    Books = Some restored.Books
                    Company = companyForm restored.Manifest }

            // A manifest migrated as it was read is saved at once, so the
            // migration is written once and said once.
            if restored.Migrated then
                save (enter model.Place { opened with Notice = Some "These books were updated to name their credit, deposit and bad-debt accounts." })
            else
                enter model.Place opened, []
        | Ok restored -> { model with Storage = Untrustworthy(restored.Problems |> List.map Diagnostics.describe) }, []
        | Error why -> { model with Storage = Untrustworthy [ why ] }, []
    | Saved true -> { model with Unsaved = max 0 (model.Unsaved - 1) }, []
    | Saved false ->
        { model with
            Unsaved = max 0 (model.Unsaved - 1)
            Error = Some "This browser could not save the books. The last change is shown but may be lost on reload." },
        []
    | LinkCopyRequested ->
        match model.Page, model.Router.Current with
        | Some page, Some location -> model, [ CopyText(Limen.Routing.Link.share Routes.mode page location) ]
        | _ -> model, []
    | LinkCopied true -> { model with Notice = Some "Link copied."; Error = None; CopyFallback = None }, []
    | LinkCopied false ->
        { model with
            Notice = None
            Error = Some "This browser did not let Summa copy the link. Copy it from here:"
            CopyFallback =
                Option.map2 (Limen.Routing.Link.share Routes.mode) model.Page model.Router.Current },
        []
    | InvoiceSearchChanged v -> refineTo (Routes.Invoices { invoiceList model with Search = nonEmpty v }) model
    | InvoiceStatusToggled status when List.contains status Routes.invoiceStatuses ->
        let list = invoiceList model
        let toggled = if list.Status.Contains status then list.Status.Remove status else list.Status.Add status
        refineTo (Routes.Invoices { list with Status = toggled }) model
    | InvoiceStatusToggled _ -> model, []
    | InvoiceCustomerChosen v -> refineTo (Routes.Invoices { invoiceList model with Customer = nonEmpty v }) model
    | InvoiceFromChanged v -> refineTo (Routes.Invoices { invoiceList model with From = parseDate v }) model
    | InvoiceToChanged v -> refineTo (Routes.Invoices { invoiceList model with To = parseDate v }) model
    | InvoiceOverdueToggled -> refineTo (Routes.Invoices { invoiceList model with Overdue = not (invoiceList model).Overdue }) model
    | InvoiceSortChosen v ->
        match Routes.invoiceSortOf v with
        | Some sort -> refineTo (Routes.Invoices { invoiceList model with Sort = sort }) model
        | None -> model, []
    | InvoiceFiltersCleared -> refineTo (Routes.Invoices Routes.allInvoices) model
    | CustomerSearchChanged v -> refineTo (Routes.Customers { customerList model with Search = nonEmpty v }) model
    | CustomerSortChosen v ->
        match Routes.customerSortOf v with
        | Some sort -> refineTo (Routes.Customers { customerList model with Sort = sort }) model
        | None -> model, []
    | CustomerInactiveToggled -> refineTo (Routes.Customers { customerList model with Inactive = not (customerList model).Inactive }) model
    | ReceivablesAsOfChanged v ->
        match model.Place with
        | Routes.Receivables(_, customerId) -> refineTo (Routes.Receivables(parseDate v, customerId)) model
        | _ -> model, []
    | ReceivablesCustomerChosen v ->
        match model.Place with
        | Routes.Receivables(asOf, _) -> refineTo (Routes.Receivables(asOf, nonEmpty v)) model
        | _ -> model, []
    | InvoiceTabChosen v ->
        match model.Place, Routes.invoiceTabOf v with
        | Routes.Invoice(invoiceId, _), Some tab -> refineTo (Routes.Invoice(invoiceId, tab)) model
        | _ -> model, []
    | CustomerNameChanged v -> { model with Customer = { model.Customer with Name = v } }, []
    | CustomerBillingNameChanged v -> { model with Customer = { model.Customer with BillingName = v } }, []
    | CustomerAddressChanged v -> { model with Customer = { model.Customer with Address = v } }, []
    | CustomerEmailChanged v -> { model with Customer = { model.Customer with Email = v } }, []
    | CustomerTermsChanged v -> { model with Customer = { model.Customer with TermsDays = v } }, []
    | CustomerTaxStatusChosen v when List.contains v [ "not-assessed"; "taxable"; "exempt" ] -> { model with Customer = { model.Customer with TaxStatus = v } }, []
    | CustomerTaxStatusChosen _ -> model, []
    | CustomerTaxJurisdictionChanged v -> { model with Customer = { model.Customer with TaxJurisdiction = v } }, []
    | CustomerTaxEvidenceChanged v -> { model with Customer = { model.Customer with TaxEvidence = v } }, []
    | CustomerAdded ->
        let form = model.Customer

        let terms =
            match form.TermsDays.Trim() with
            | "" -> Ok None
            | "0" -> Ok(Some DueOnReceipt)
            | text ->
                match Int32.TryParse text with
                | true, days when days > 0 && days <= 365 -> Ok(Some(Net days))
                | _ -> Error "Terms are a number of days between 0 and 365, or empty for the organization's default."

        // What the person says of the customer's tax, and nothing assumed (INV-ADJ-005).
        let tax =
            match form.TaxStatus with
            | "taxable" -> Ok(SubjectToTax(nonEmpty form.TaxJurisdiction))
            | "exempt" ->
                match nonEmpty form.TaxEvidence with
                | Some evidence -> Ok(TaxExempt(evidence, nonEmpty form.TaxJurisdiction))
                | None -> Error "An exempt customer needs the certificate or reference that shows it."
            | _ -> Ok TaxNotAssessed

        match terms, tax with
        | Error why, _
        | _, Error why -> { model with Error = Some why }, []
        | Ok _, _ when form.Name.Trim() = "" -> { model with Error = Some "A customer needs a name." }, []
        | Ok terms, Ok tax ->
            let added, effects =
                command model $"Customer {form.Name.Trim()} added." (fun books ->
                    let id = nextId "CUST" books.Books.Customers.ContainsKey

                    let customer =
                        { Id = id
                          Name = form.Name.Trim()
                          BillingName = (if form.BillingName.Trim() = "" then form.Name.Trim() else form.BillingName.Trim())
                          BillingAddress = form.Address.Trim()
                          Email = form.Email.Trim()
                          DefaultTerms = terms
                          PaymentProfileId = None
                          Active = true
                          Tax = tax }

                    Ok { books with Books = saveCustomer (context ctx) customer books.Books })

            (if added.Error.IsNone then { added with Customer = emptyCustomer } else added), effects
    | DraftCustomerChanged v -> { model with Draft = { model.Draft with CustomerId = v } }, []
    | LineDescriptionChanged(key, v) -> editLine key (fun l -> { l with Description = v }) model
    | LineHoursChanged(key, v) -> editLine key (fun l -> { l with Hours = v }) model
    | LineRateChanged(key, v) -> editLine key (fun l -> { l with Rate = v }) model
    | LineAdded ->
        let line, counter = newLine model.Counter
        { model with Draft = { model.Draft with Lines = model.Draft.Lines @ [ line ] }; Counter = counter }, []
    | LineRemoved key ->
        let rest = model.Draft.Lines |> List.filter (fun l -> l.Key <> key)

        if rest.IsEmpty then
            let line, counter = newLine model.Counter
            { model with Draft = { model.Draft with Lines = [ line ] }; Counter = counter }, []
        else
            { model with Draft = { model.Draft with Lines = rest } }, []
    | DraftPurchaseOrderChanged v -> { model with Draft = { model.Draft with PurchaseOrder = v } }, []
    | DraftNotesChanged v -> { model with Draft = { model.Draft with Notes = v } }, []
    | LineTaxableChanged(key, taxable) -> editLine key (fun l -> { l with Taxable = taxable }) model
    | DraftTaxCodeChanged v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with Code = v } } }, []
    | DraftTaxAmountChanged v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with Amount = v } } }, []
    | DraftTaxAccountChosen v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with AccountCode = v } } }, []
    | DraftTaxJurisdictionChanged v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with Jurisdiction = v } } }, []
    | DraftTaxRateChanged v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with Rate = v } } }, []
    | DraftTaxRateSourceChanged v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with RateSource = v } } }, []
    | DraftTaxEvidenceChanged v -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with Evidence = v } } }, []
    | DraftTaxInclusiveChanged inclusive -> { model with Draft = { model.Draft with Tax = { model.Draft.Tax with Inclusive = inclusive } } }, []
    | EditorRead None -> model, []
    | EditorRead(Some text) ->
        match decodeEditor text with
        // Not a form this Summa can read: forget it.
        | None -> model, [ KeepEditor None ]
        | Some(counter, form) ->
            let kept = { model with KeptEditor = Some form; Counter = max counter model.Counter }
            // Before the books are read it waits: reading them enters the place.
            (if model.Books.IsSome && fits model.Place model.Books form then enter model.Place kept else kept), []
    | EditorDiscarded ->
        let fresh, counter = emptyDraft model.Counter

        let draft, counter =
            match model.Place, model.Books with
            | Routes.Draft draftId, Some books ->
                books.Books.Drafts.TryFind draftId
                |> Option.map (formOf books.Books.Ledger counter)
                |> Option.defaultValue (fresh, counter)
            | Routes.NewInvoice customerId, _ -> { fresh with CustomerId = defaultArg customerId "" }, counter
            | _ -> fresh, counter

        { model with Draft = draft; Counter = counter; KeptEditor = None; Blockers = []; Error = None; Notice = Some "Your unsaved changes were discarded." },
        [ KeepEditor None ]
    | DraftSubmitted ->
        match model.Manifest, model.Books with
        | Some manifest, Some books ->
            match draftOf manifest books model.Draft with
            | Error why -> { model with Error = Some why; Notice = None }, []
            | Ok draft ->
                let request = issueRequest manifest books draft.DraftId (today ctx)

                match saveDraft (context ctx) draft books.Books with
                | Error problems -> { model with Error = Some(describe problems); Notice = None }, []
                | Ok saved ->
                    let withDraft = { books with Books = saved }

                    let reviewed =
                        match Issuance.submitForReview (context ctx) request withDraft with
                        | Ok reviewed ->
                            save
                                { model with
                                    Books = Some reviewed
                                    Draft = { model.Draft with DraftId = Some draft.DraftId }
                                    Blockers = []
                                    Notice = Some "Ready to issue. Check the preview, then issue it."
                                    Error = None }
                        | Error blockers ->
                            save
                                { model with
                                    Books = Some withDraft
                                    Draft = { model.Draft with DraftId = Some draft.DraftId }
                                    Blockers = blockers
                                    Notice = Some "Saved as a draft. Resolve the items below before issuing."
                                    Error = None }

                    // A new invoice, once saved, is its draft: replace the address.
                    match reviewed, model.Place with
                    | (saved, effects), Routes.NewInvoice _ ->
                        let moved, more = refineTo (Routes.Draft draft.DraftId) saved
                        moved, effects @ more
                    | result, _ -> result
        | _ -> model, []
    | DraftIssued ->
        match model.Manifest, model.Books, model.Draft.DraftId with
        | Some manifest, Some books, Some draftId ->
            let request = issueRequest manifest books draftId (today ctx)

            match Issuance.issueInvoice (context ctx) request books with
            | Ok(issued, invoice) ->
                let draft, counter = emptyDraft model.Counter

                let saved, effects =
                    save
                        { model with
                            Books = Some issued
                            Draft = draft
                            Counter = counter
                            Blockers = []
                            Notice = Some $"Invoice {invoice.Number} issued."
                            Error = None }

                let moved, more = goTo (Routes.Invoice(invoice.InvoiceId, Routes.Document)) saved
                moved, effects @ more
            | Error blockers -> { model with Blockers = blockers; Error = Some "The invoice cannot be issued yet." }, []
        | _ -> { model with Error = Some "Save and review the draft first." }, []
    | PrintRequested -> model, [ PrintPage ]
    // A person approves: the proposal is checked again and issued as one
    // invoice in the same change (INV-CHR-006, SUM4-042). The application
    // has no agent of its own, so the approval is always a person's.
    | ProposalApproved proposalId ->
        match model.Manifest, model.Books with
        | Some manifest, Some books ->
            let draftId = nextId "D" (fun id -> books.Books.Drafts.ContainsKey id || books.Books.IssuedFrom.ContainsKey id)
            let request = issueRequest manifest books draftId (today ctx)

            match
                Billing.markReady (context ctx) false proposalId books
                |> Result.bind (Billing.accept (context ctx) proposalId request)
            with
            | Ok(issued, invoice) ->
                let saved, effects =
                    save { model with Books = Some issued; Notice = Some $"Invoice {invoice.Number} issued from proposal {proposalId}."; Error = None }

                let moved, more = goTo (Routes.Invoice(invoice.InvoiceId, Routes.Document)) saved
                moved, effects @ more
            | Error problems -> { model with Error = Some $"Proposal {proposalId} cannot be approved: {describe problems}"; Notice = None }, []
        | _ -> model, []
    | ProposalLineChosen v -> { model with ProposalRate = { model.ProposalRate with Line = v } }, []
    | ProposalRateChanged v -> { model with ProposalRate = { model.ProposalRate with Rate = v } }, []
    | ProposalReasonChanged v -> { model with ProposalRate = { model.ProposalRate with Reason = v } }, []
    | ProposalRateOverridden proposalId ->
        let form = model.ProposalRate

        match Int32.TryParse form.Line, parseAmount form.Rate with
        | (false, _), _ -> { model with Error = Some "Choose the line whose rate changes."; Notice = None }, []
        | _, None -> { model with Error = Some "The new rate is an amount such as 175.00."; Notice = None }, []
        | _, Some _ when form.Reason.Trim() = "" -> { model with Error = Some "Say why the rate changes; the reason is kept with the proposal."; Notice = None }, []
        | (true, index), Some rate ->
            let changed, effects =
                command model $"The rate was changed on proposal {proposalId}. It needs review again before it is issued." (fun books ->
                    Billing.overrideRate (context ctx) proposalId index rate (form.Reason.Trim()) books |> Result.mapError describe)

            (if changed.Error.IsNone then { changed with ProposalRate = { Line = "0"; Rate = ""; Reason = "" } } else changed), effects
    | ProposalAbandoned proposalId ->
        command model $"Proposal {proposalId} abandoned; its time and expenses are free to bill again." (fun books ->
            Billing.abandon (context ctx) proposalId books |> Result.mapError describe)
    | PaymentAmountChanged v -> { model with Payment = { model.Payment with Amount = v } }, []
    | PaymentDateChanged v -> { model with Payment = { model.Payment with Date = v } }, []
    | PaymentMethodChanged v -> { model with Payment = { model.Payment with Method = v } }, []
    | PaymentReferenceChanged v -> { model with Payment = { model.Payment with Reference = v } }, []
    | PaymentRecorded ->
        let form = model.Payment

        let received =
            if form.Date.Trim() = "" then Some(today ctx) else parseDate form.Date

        let method =
            match form.Method with
            | "check" -> Check
            | "wire" -> Wire
            | "credit-card" -> CreditCard
            | "cash" -> PaymentMethod.Cash
            | "other" -> PaymentMethod.Other
            | _ -> Ach

        match parseAmount form.Amount, received with
        | None, _ -> { model with Error = Some "Enter the amount received, such as 1250.00." }, []
        | _, None -> { model with Error = Some "Enter the date received as YYYY-MM-DD." }, []
        | Some amount, Some date ->
            let recorded, effects =
                command model $"Payment of {Documents.moneyText amount} recorded." (fun books ->
                    let shown =
                        match model.Place with
                        | Routes.Invoice(id, _) -> id
                        | _ -> form.InvoiceId

                    match books.Books.Invoices.TryFind shown, model.Manifest with
                    | None, _
                    | _, None -> Error "Open the invoice the payment is for."
                    | Some invoice, Some manifest ->
                        let paymentId = nextId "PAY" books.Payments.ContainsKey

                        let payment =
                            { Id = paymentId
                              CustomerId = invoice.CustomerId
                              DateReceived = date
                              Amount = amount
                              Method = method
                              Reference = form.Reference.Trim()
                              Memo = None }

                        let allocation =
                            { AllocationId = nextId "AL" (fun id -> books.Allocations |> List.exists (fun a -> a.Id = id))
                              PaymentId = paymentId
                              InvoiceId = invoice.InvoiceId
                              Amount = amount
                              JournalEntryId = nextId "JE-PAY" books.Books.Ledger.Entries.ContainsKey
                              CashAccountId = Organization.accountByCode books.Books.Ledger manifest.Accounting.CashAccount
                              ReceivableAccountId = Organization.accountByCode books.Books.Ledger manifest.Accounting.ReceivablesAccount }

                        recordPayment (context ctx) payment books
                        |> Result.bind (allocate (context ctx) allocation)
                        |> Result.mapError (fun problems ->
                            match problems with
                            | [ ExceedsOutstanding owed ] -> $"That is more than the {Documents.moneyText owed} still owed."
                            | _ -> describe problems))

            (if recorded.Error.IsNone then { recorded with Payment = { recorded.Payment with Amount = ""; Reference = ""; Date = "" } } else recorded), effects
    | CompanyLegalNameChanged v -> { model with Company = { model.Company with LegalName = v } }, []
    | CompanyAddressChanged v -> { model with Company = { model.Company with Address = v } }, []
    | CompanyEmailChanged v -> { model with Company = { model.Company with Email = v } }, []
    | CompanyPaymentChanged v -> { model with Company = { model.Company with PaymentInstructions = v } }, []
    | CompanySaved ->
        match model.Manifest with
        | Some manifest ->
            let changed =
                { manifest with
                    Company = { manifest.Company with LegalName = model.Company.LegalName.Trim(); Address = model.Company.Address.Trim(); Email = model.Company.Email.Trim() }
                    Invoices = { manifest.Invoices with PaymentInstructions = model.Company.PaymentInstructions.Trim() } }

            match Organization.problems changed with
            | [] -> save { model with Manifest = Some changed; Notice = Some "Company details saved. New invoices will use them."; Error = None }
            | problems -> { model with Error = Some(problems |> List.map Diagnostics.describe |> String.concat "; ") }, []
        | None -> model, []
    | CreditAmountChanged v -> { model with Credit = { model.Credit with Amount = v } }, []
    | CreditReasonChanged v -> { model with Credit = { model.Credit with Reason = v } }, []
    | CreditMemoIssued ->
        match model.Place, model.Manifest, model.Books, parseAmount model.Credit.Amount with
        | Routes.Invoice(invoiceId, _), Some manifest, Some current, Some amount when model.Credit.Reason.Trim() <> "" ->
            let memoId = nextId "CM" current.CreditMemos.ContainsKey

            let issued, effects =
                command model $"Credit memo {memoId} issued and applied." (fun books ->
                    match books.Books.Invoices.TryFind invoiceId with
                    | None -> Error "Open the invoice the credit is for."
                    | Some invoice ->
                        let accounts = Organization.receivableAccounts manifest books.Books.Ledger
                        let entry prefix = nextId prefix books.Books.Ledger.Entries.ContainsKey

                        let memo: CreditMemo =
                            { Id = memoId
                              CustomerId = invoice.CustomerId
                              InvoiceId = Some invoiceId
                              Amount = amount
                              RevenueAccountId = Organization.accountByCode books.Books.Ledger manifest.Accounting.RevenueAccount
                              Reason = model.Credit.Reason.Trim()
                              IssueDate = today ctx
                              JournalEntryId = entry "JE-CM"
                              Lines = [] }

                        let application: Credits.ApplicationRequest =
                            { ApplicationId = nextId "AP" (fun id -> books.Applications |> List.exists (fun a -> a.Id = id))
                              Source = FromCreditMemo memoId
                              InvoiceId = invoiceId
                              Amount = amount
                              Date = today ctx
                              JournalEntryId = entry "JE-AP" }

                        Credits.issueCreditMemo (context ctx) accounts memo books
                        |> Result.bind (Credits.apply (context ctx) accounts application)
                        |> Result.mapError (fun problems ->
                            match problems with
                            | [ Credits.ExceedsOutstandingBalance owed ] -> $"That is more than the {Documents.moneyText owed} still owed."
                            | _ -> describe problems))

            (if issued.Error.IsNone then { issued with Credit = { Amount = ""; Reason = "" } } else issued), effects
        | Routes.Invoice _, _, _, None -> { model with Error = Some "Enter the amount to credit, such as 150.00."; Notice = None }, []
        | Routes.Invoice _, _, _, Some _ -> { model with Error = Some "A credit memo needs a reason the customer will read."; Notice = None }, []
        | _ -> model, []
    | PaymentSearchChanged v -> refineTo (Routes.Payments { paymentList model with Search = nonEmpty v }) model
    | PaymentCustomerChosen v -> refineTo (Routes.Payments { paymentList model with Customer = nonEmpty v }) model
    | PaymentFromChanged v -> refineTo (Routes.Payments { paymentList model with From = parseDate v }) model
    | PaymentToChanged v -> refineTo (Routes.Payments { paymentList model with To = parseDate v }) model
    | PaymentUnappliedToggled -> refineTo (Routes.Payments { paymentList model with Unapplied = not (paymentList model).Unapplied }) model
    | PaymentSortChosen v ->
        match Routes.paymentSortOf v with
        | Some sort -> refineTo (Routes.Payments { paymentList model with Sort = sort }) model
        | None -> model, []
    | PaymentFiltersCleared -> refineTo (Routes.Payments Routes.allPayments) model
    | CustomerTabChosen v ->
        match model.Place, Routes.customerTabOf v with
        | Routes.Customer(customerId, _), Some tab -> refineTo (Routes.Customer(customerId, tab)) model
        | _ -> model, []
    | CreditMemosCustomerChosen v -> refineTo (Routes.CreditMemos(nonEmpty v)) model
    | EngagementsCustomerChosen v -> refineTo (Routes.Engagements(nonEmpty v)) model
    | EngagementCustomerChanged v -> { model with Engagement = { model.Engagement with CustomerId = v } }, []
    | EngagementNameChanged v -> { model with Engagement = { model.Engagement with Name = v } }, []
    | EngagementFeeChanged v -> { model with Engagement = { model.Engagement with FixedFee = v } }, []
    | EngagementAdded ->
        let form = model.Engagement

        let fee =
            match form.FixedFee.Trim() with
            | "" -> Ok None
            | text -> parseAmount text |> Option.map (Some >> Ok) |> Option.defaultValue (Error "A fixed fee is an amount such as 5000.00, or empty for hourly work.")

        match fee, model.Books with
        | Error why, _ -> { model with Error = Some why; Notice = None }, []
        | Ok _, _ when form.CustomerId = "" -> { model with Error = Some "Choose the customer the engagement is with."; Notice = None }, []
        | Ok _, _ when form.Name.Trim() = "" -> { model with Error = Some "An engagement needs a name."; Notice = None }, []
        | Ok fixedFee, Some current ->
            let engagementId = nextId "ENG" current.Books.Engagements.ContainsKey

            let added, effects =
                command model $"Engagement {form.Name.Trim()} added." (fun books ->
                    let engagement: Engagement =
                        { Id = engagementId
                          CustomerId = form.CustomerId
                          Name = form.Name.Trim()
                          Currency = "USD"
                          FixedFee = fixedFee
                          Milestones = []
                          Terms = None }

                    Billing.saveEngagement (context ctx) engagement books.Books
                    |> Result.map (fun saved -> { books with Books = saved })
                    |> Result.mapError describe)

            if added.Error.IsNone then
                let moved, more = goTo (Routes.Engagement engagementId) { added with Engagement = { CustomerId = ""; Name = ""; FixedFee = "" } }
                moved, effects @ more
            else
                added, effects
        | Ok _, None -> model, []
    | LedgerAccountChosen v ->
        match model.Place with
        | Routes.Ledger(_, from, until) -> refineTo (Routes.Ledger(nonEmpty v, from, until)) model
        | _ -> model, []
    | LedgerFromChanged v ->
        match model.Place with
        | Routes.Ledger(account, _, until) -> refineTo (Routes.Ledger(account, parseDate v, until)) model
        | _ -> model, []
    | LedgerToChanged v ->
        match model.Place with
        | Routes.Ledger(account, from, _) -> refineTo (Routes.Ledger(account, from, parseDate v)) model
        | _ -> model, []
    | ReportAsOfChanged v ->
        match model.Place with
        | Routes.TrialBalance _ -> refineTo (Routes.TrialBalance(parseDate v)) model
        | Routes.BalanceSheet _ -> refineTo (Routes.BalanceSheet(parseDate v)) model
        | _ -> model, []
    | IncomeFromChanged v ->
        match model.Place with
        | Routes.IncomeStatement(_, until, basis) -> refineTo (Routes.IncomeStatement(parseDate v, until, basis)) model
        | _ -> model, []
    | IncomeToChanged v ->
        match model.Place with
        | Routes.IncomeStatement(from, _, basis) -> refineTo (Routes.IncomeStatement(from, parseDate v, basis)) model
        | _ -> model, []
    | IncomeBasisChosen v ->
        match model.Place, Routes.basisOf v with
        | Routes.IncomeStatement(from, until, _), Some basis -> refineTo (Routes.IncomeStatement(from, until, basis)) model
        | _ -> model, []
    | IncomeRangeChosen key ->
        match model.Place, dateRange (today ctx) key with
        | Routes.IncomeStatement(_, _, basis), Some(from, until) -> refineTo (Routes.IncomeStatement(Some from, Some until, basis)) model
        | _ -> model, []
    | PeriodClosed when (match model.Place, model.Books with
                         | Routes.Period(y, m), Some b -> not (periodBlockers b (y, m)).IsEmpty
                         | _ -> false) ->
        { model with Error = Some "This period is not ready to close. Resolve what is listed first."; Notice = None }, []
    | PeriodClosed
    | PeriodLocked
    | PeriodReopened ->
        match model.Place with
        | Routes.Period(year, month) ->
            let label = Documents.dateText(DateOnly(year, month, 1)).Replace(" 1,", "")

            let notice, change =
                match msg with
                | PeriodClosed -> $"{label} is closed. Nothing can be posted to it until it is reopened.", (fun ledger -> Ok(closePeriod (context ctx) (year, month) ledger))
                | PeriodLocked -> $"{label} is locked.", (fun ledger -> Ok(lockPeriod (context ctx) (year, month) ledger))
                // Local books are their owner's own, so reopening is permitted.
                | _ -> $"{label} is open again.", reopenPeriod (context ctx) true (year, month)

            command model notice (fun books ->
                change books.Books.Ledger
                |> Result.map (fun ledger -> { books with Books = { books.Books with Ledger = ledger } })
                |> Result.mapError describe)
        | _ -> model, []
    | PacksNegotiated packs -> { model with Packs = packs }, []
    | ArtifactStoreOpened(Ok()) -> { model with Artifacts = StoreOpen }, []
    | ArtifactStoreOpened(Error why) -> { model with Artifacts = StoreUnavailable why }, []
    | PdfPicked None -> model, []
    | PdfPicked(Some file) ->
        let release = [ ReleaseFile file.Id ]

        match model.Place, model.Pdf, model.Artifacts with
        | Routes.Invoice(invoiceId, _), PdfIdle, StoreOpen ->
            match Artifacts.checkSize file.Size with
            | Error problem -> { model with Error = Some(Artifacts.describe problem); Notice = None }, release
            | Ok size ->
                { model with Pdf = PdfReading(invoiceId, file.Id, size, []); Notice = Some $"Reading {file.Name}…"; Error = None },
                [ ReadFileSlice(file.Id, 0L, int (min size (int64 ChunkBytes))) ]
        | _ -> { model with Error = Some "Open the invoice the PDF is for, and wait for the one being stored."; Notice = None }, release
    | PdfChunkRead result ->
        match model.Pdf, result, model.Configuration with
        | PdfReading(invoiceId, file, size, chunks), Ok(data, eof), Configured config ->
            let read = chunks @ [ Convert.FromBase64String data ]
            let offset = read |> List.sumBy (fun c -> int64 c.Length)

            if not eof && offset < size then
                { model with Pdf = PdfReading(invoiceId, file, size, read) }, [ ReadFileSlice(file, offset, int (min (size - offset) (int64 ChunkBytes))) ]
            else
                let bytes = Array.concat read

                match Artifacts.fingerprint bytes with
                | Ok(sha, length) ->
                    { model with Pdf = PdfStoring(invoiceId, sha, length); Notice = Some "Storing the PDF…" },
                    [ ReleaseFile file; PutArtifact(Artifacts.database config, sha, Convert.ToBase64String bytes, length) ]
                | Error problem -> { model with Pdf = PdfIdle; Error = Some(Artifacts.describe problem); Notice = None }, [ ReleaseFile file ]
        | PdfReading(_, file, _, _), Error why, _ ->
            { model with Pdf = PdfIdle; Error = Some $"The file could not be read ({why})."; Notice = None }, [ ReleaseFile file ]
        | _ -> model, []
    | PdfStored result ->
        match model.Pdf, result, model.Configuration with
        | PdfStoring(invoiceId, sha, size), Ok(), Configured config ->
            let recorded, effects =
                command { model with Pdf = PdfIdle } "The PDF is stored with the invoice." (fun books ->
                    Artifacts.recordPdf (context ctx) config invoiceId (sha, size) books |> Result.mapError Artifacts.describe)

            recorded, effects
        | PdfStoring _, Error why, _ ->
            { model with Pdf = PdfIdle; Error = Some $"This browser could not store the PDF ({why})."; Notice = None }, []
        | _ -> model, []
    | PdfDownloadRequested ->
        match model.Place, model.Books, model.Configuration, model.Artifacts with
        | Routes.Invoice(invoiceId, _), Some books, Configured config, StoreOpen ->
            match Artifacts.storedPdf config books invoiceId, books.Books.Invoices.TryFind invoiceId with
            | Ok(Some sha), Some invoice -> { model with Pdf = PdfFetching $"{invoice.Number}.pdf" }, [ GetArtifact(Artifacts.database config, sha) ]
            | Error problem, _ -> { model with Error = Some(Artifacts.describe problem) }, []
            | _ -> model, []
        | _ -> model, []
    | PdfFetched result ->
        match model.Pdf, result with
        | PdfFetching fileName, Ok(Some data) -> { model with Pdf = PdfIdle }, [ OfferDownload(fileName, "application/pdf", data) ]
        | PdfFetching _, Ok None ->
            { model with
                Pdf = PdfIdle
                Error = Some "This browser no longer holds the PDF. Its fingerprint is still recorded; attach the same file again to restore it." },
            []
        | PdfFetching _, Error why -> { model with Pdf = PdfIdle; Error = Some $"The PDF could not be read back ({why})." }, []
        | _ -> model, []
    | FollowUpShowChosen v ->
        match Routes.followUpOf v with
        | Some view -> refineTo (Routes.FollowUp view) model
        | None -> model, []
    | ReminderRecorded key ->
        // From a follow-up row the key names the invoice; on an invoice's own page, it is the one shown.
        let invoiceId =
            match key, model.Place with
            | "", Routes.Invoice(id, _) -> id
            | id, _ -> id

        command model "Reminder recorded." (fun books ->
            let times =
                match (Lifecycle.followUp books invoiceId).Collection with
                | Reminded n -> n + 1
                | _ -> 1

            Lifecycle.setCollection (context ctx) invoiceId (Reminded times) books |> Result.mapError describe)
    | FollowUpNoteChanged v -> { model with FollowUpNote = v }, []
    | DisputeMarked
    | DisputeSettled ->
        match model.Place with
        | Routes.Invoice(invoiceId, _) when model.FollowUpNote.Trim() = "" ->
            { model with Error = Some(if msg = DisputeMarked then "Say what the customer disputes." else "Say how the dispute was resolved."); Notice = None }, []
        | Routes.Invoice(invoiceId, _) ->
            let note = model.FollowUpNote.Trim()

            let changed, effects =
                command model (if msg = DisputeMarked then "Dispute recorded. The invoice and the books are unchanged." else "Dispute resolved.") (fun books ->
                    (if msg = DisputeMarked then Lifecycle.dispute else Lifecycle.resolveDispute) (context ctx) invoiceId note (today ctx) books
                    |> Result.mapError describe)

            (if changed.Error.IsNone then { changed with FollowUpNote = "" } else changed), effects
        | _ -> model, []
    | InboxCustomerChosen v -> refineTo (Routes.Inbox(nonEmpty v)) model
    | InboxApplied paymentId ->
        match model.Manifest with
        | Some manifest ->
            command model $"Payment {paymentId} applied." (fun books ->
                match books.Payments.TryFind paymentId with
                | None -> Error $"There is no payment {paymentId}."
                | Some payment ->
                    let invoices = openInvoices (today ctx) books |> List.filter (fun i -> i.CustomerId = payment.CustomerId)

                    // Oldest due first, each up to what it still owes, until the payment is used.
                    let requests, _ =
                        invoices
                        |> List.fold
                            (fun (requests, left: Money) (i: IssuedInvoice) ->
                                let owed = outstanding books i
                                let amount = if left.Minor < owed.Minor then left else owed

                                if amount.Minor <= 0L then
                                    requests, left
                                else
                                    let request: AllocationRequest =
                                        { AllocationId = nextId "AL" (fun id -> books.Allocations |> List.exists (fun a -> a.Id = id) || requests |> List.exists (fun (r: AllocationRequest) -> r.AllocationId = id))
                                          PaymentId = payment.Id
                                          InvoiceId = i.InvoiceId
                                          Amount = amount
                                          JournalEntryId = nextId "JE-PAY" (fun id -> books.Books.Ledger.Entries.ContainsKey id || requests |> List.exists (fun (r: AllocationRequest) -> r.JournalEntryId = id))
                                          CashAccountId = Organization.accountByCode books.Books.Ledger manifest.Accounting.CashAccount
                                          ReceivableAccountId = Organization.accountByCode books.Books.Ledger manifest.Accounting.ReceivablesAccount }

                                    requests @ [ request ], subtract left amount)
                            ([], unallocated books payment)

                    if requests.IsEmpty then Error $"{(books.Books.Customers.TryFind payment.CustomerId |> Option.map _.Name |> Option.defaultValue payment.CustomerId)} has no open invoice to apply it to. Keep it as credit instead."
                    else Credits.allocateAcross (context ctx) requests books |> Result.mapError describe)
        | None -> model, []
    | InboxCredited paymentId ->
        match model.Manifest with
        | Some manifest ->
            command model $"Payment {paymentId} kept as the customer's credit." (fun books ->
                let request: Credits.CreditRequest =
                    { CreditId = nextId "CR" books.Credits.ContainsKey
                      PaymentId = paymentId
                      Date = today ctx
                      JournalEntryId = nextId "JE-CR" books.Books.Ledger.Entries.ContainsKey }

                Credits.creditUnapplied (context ctx) (Organization.receivableAccounts manifest books.Books.Ledger) request books |> Result.mapError describe)
        | None -> model, []
    | ReceiptCustomerChanged v -> { model with Receipt = { model.Receipt with CustomerId = v } }, []
    | ReceiptAmountChanged v -> { model with Receipt = { model.Receipt with Amount = v } }, []
    | ReceiptDateChanged v -> { model with Receipt = { model.Receipt with Date = v } }, []
    | ReceiptMethodChanged v -> { model with Receipt = { model.Receipt with Method = v } }, []
    | ReceiptReferenceChanged v -> { model with Receipt = { model.Receipt with Reference = v } }, []
    | ReceiptRecorded ->
        let form = model.Receipt
        let received = if form.Date.Trim() = "" then Some(today ctx) else parseDate form.Date

        match parseAmount form.Amount, received with
        | _, _ when form.CustomerId = "" -> { model with Error = Some "Choose who paid."; Notice = None }, []
        | None, _ -> { model with Error = Some "Enter the amount received, such as 1250.00."; Notice = None }, []
        | _, None -> { model with Error = Some "Enter the date received as YYYY-MM-DD."; Notice = None }, []
        | Some amount, Some date ->
            let recorded, effects =
                command model "Payment recorded. Apply it, or keep it as credit." (fun books ->
                    let payment =
                        { Id = nextId "PAY" books.Payments.ContainsKey
                          CustomerId = form.CustomerId
                          DateReceived = date
                          Amount = amount
                          Method = methodOf form.Method
                          Reference = form.Reference.Trim()
                          Memo = None }

                    recordPayment (context ctx) payment books |> Result.mapError describe)

            (if recorded.Error.IsNone then { recorded with Receipt = { recorded.Receipt with Amount = ""; Reference = ""; Date = "" } } else recorded), effects
    | CpaExportRequested kind ->
        match model.Books, model.Packs.Files with
        | Some books, true ->
            let year =
                match model.Place with
                | Routes.Cpa(Some year) -> year
                | _ -> (today ctx).Year

            let yearEnd = DateOnly(year, 12, 31)
            let ledger = books.Books.Ledger
            let base64 (text: string) = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes text)

            match kind with
            | "trial-balance" -> model, [ OfferDownload($"trial-balance-{year}.csv", "text/csv", base64 (Reports.trialBalanceCsv (Reports.trialBalance "USD" yearEnd ledger))) ]
            | "journal" ->
                let ofYear = { ledger with Journal = ledger.Journal |> List.filter (fun id -> ledger.Entries[id].Date.Year = year) }
                model, [ OfferDownload($"journal-{year}.csv", "text/csv", base64 (Reports.journalCsv ofYear)) ]
            | _ -> model, []
        | _ -> { model with Error = Some "This browser cannot offer downloads here." }, []
    | ResetConfirmed ->
        // Only offered when the stored books fail their checks.
        match model.Storage with
        | Untrustworthy _ when not (onGitHub model) -> apply ctx (Loaded None) { model with Storage = Loading }
        | _ -> model, []

/// The capability a message that changes books on GitHub needs, and the
/// one-line summary its commit carries. Messages not here change no books.
let bookCommand (msg: Msg) : (Summa.Access.Access.Capability * string) option =
    let open' capability summary = Some(capability, summary)

    match msg with
    | CustomerAdded -> open' Summa.Access.Access.ManageBilling "add a customer"
    | EngagementAdded -> open' Summa.Access.Access.ManageBilling "add an engagement"
    | DraftSubmitted -> open' Summa.Access.Access.CreateDraftInvoice "save a draft invoice"
    | DraftIssued -> open' Summa.Access.Access.IssueInvoice "issue an invoice"
    | PdfStored _ -> open' Summa.Access.Access.IssueInvoice "record an invoice's PDF"
    | PaymentRecorded -> open' Summa.Access.Access.RecordPayment "record a payment"
    | ReceiptRecorded -> open' Summa.Access.Access.RecordPayment "record a payment on account"
    | InboxApplied _ -> open' Summa.Access.Access.AllocatePayment "apply a payment"
    | InboxCredited _ -> open' Summa.Access.Access.AllocatePayment "keep a payment as credit"
    | CreditMemoIssued -> open' Summa.Access.Access.CreateCreditMemo "issue a credit memo"
    | ReminderRecorded _ -> open' Summa.Access.Access.RecordPayment "record a reminder"
    | DisputeMarked
    | DisputeSettled -> open' Summa.Access.Access.RecordPayment "record a dispute"
    | PeriodClosed
    | PeriodLocked -> open' Summa.Access.Access.ClosePeriod "close a period"
    | PeriodReopened -> open' Summa.Access.Access.ReopenPeriod "reopen a period"
    | CompanySaved -> open' Summa.Access.Access.ManageSettings "change the company details"
    | ProposalApproved _ -> open' Summa.Access.Access.IssueInvoice "issue an invoice from a proposal"
    | ProposalAbandoned _ -> open' Summa.Access.Access.ProposeInvoice "abandon an invoice proposal"
    | ProposalRateOverridden _ -> open' Summa.Access.Access.OverrideRate "change a proposed rate"
    | _ -> None

/// Runs a message that changes books on GitHub again, on `basis` with the
/// books as they stand on the store: the transition the store commits, and
/// decides again when someone else changed the books first.
let replay (ctx: Ctx) (msg: Msg) (basis: Model) (stored: Receivables) : Result<Receivables, string> =
    let replayed, _ = apply ctx msg { basis with Books = Some stored; Error = None }

    match replayed.Error, replayed.Books with
    | Some why, _ -> Error why
    | None, Some books -> Ok books
    | None, None -> Error "The books are not available."

/// The engine: one message, the next model and what to ask of the browser.
/// Books on GitHub change through the store: a message that changes them
/// needs its capability, is shown at once, and is committed as a command
/// (`CommitBooks`), never saved as a snapshot.
let private updateBooks (ctx: Ctx) (msg: Msg) (model: Model) : Model * AppEffect list =
    if not (onGitHub model) then
        apply ctx msg model
    else
        let local = function
            | SaveBooks _ -> false
            | _ -> true

        match bookCommand msg with
        | Some(capability, _) when model.Storage = Ready && not (model.Capabilities.Contains capability) ->
            { model with Error = Some "Your role in these books does not include this. Ask an administrator if you need it."; Notice = None }, []
        | Some _ when model.Storage = Ready && not model.ReadOnly.IsEmpty ->
            { model with Error = Some("These books can only be read: " + String.concat "; " model.ReadOnly); Notice = None }, []
        | Some(capability, summary) ->
            let next, effects = apply ctx msg model

            let changedBooks = not (obj.ReferenceEquals(model.Books, next.Books))
            let changedManifest = not (obj.ReferenceEquals(model.Manifest, next.Manifest))

            let commits =
                [ if changedBooks then CommitBooks { Capability = capability; Summary = summary; Msg = msg }
                  match next.Manifest with
                  | Some manifest when changedManifest && msg = CompanySaved -> CommitManifest manifest
                  | _ -> () ]

            { next with Unsaved = model.Unsaved + commits.Length }, (effects |> List.filter local) @ commits
        | None ->
            let next, effects = apply ctx msg model
            next, effects |> List.filter local

let private editsEditor =
    function
    | DraftCustomerChanged _
    | LineDescriptionChanged _
    | LineHoursChanged _
    | LineRateChanged _
    | LineAdded
    | LineRemoved _
    | LineTaxableChanged _
    | DraftPurchaseOrderChanged _
    | DraftNotesChanged _
    | DraftTaxCodeChanged _
    | DraftTaxAmountChanged _
    | DraftTaxAccountChosen _
    | DraftTaxJurisdictionChanged _
    | DraftTaxRateChanged _
    | DraftTaxRateSourceChanged _
    | DraftTaxEvidenceChanged _
    | DraftTaxInclusiveChanged _ -> true
    | _ -> false

/// The engine: one message, the next model and what to ask of the browser.
/// The editor's form is kept in the tab as the person types, so a refresh
/// does not lose it, and forgotten once the draft is saved or issued (WI-0044).
let update (ctx: Ctx) (msg: Msg) (model: Model) : Model * AppEffect list =
    let next, effects = updateBooks ctx msg model

    match msg with
    | _ when editsEditor msg && next.Draft <> model.Draft -> next, effects @ [ KeepEditor(Some(encodeEditor next.Counter next.Draft)) ]
    | DraftSubmitted
    | DraftIssued when not (obj.ReferenceEquals(model.Books, next.Books)) -> { next with KeptEditor = None }, effects @ [ KeepEditor None ]
    | _ -> next, effects

// ---- View -----------------------------------------------------------------------------------

let private text (s: string) = Value(Text s)
let private flag (b: bool) = Value(Flag b)
let private money (m: Money) = Text(Documents.moneyText m)

let private statusText =
    function
    | Issued -> "Unpaid"
    | PartiallyPaid -> "Partly paid"
    | Paid -> "Paid"
    | WrittenOff -> "Written off"
    | Voided -> "Voided"

let private statusTone =
    function
    | Paid -> "ok"
    | Voided
    | WrittenOff -> ""
    | _ -> "attention"

let private timingText =
    function
    | Current -> "Current"
    | DueSoon -> "Due soon"
    | Overdue -> "Overdue"

let private customerName (books: Receivables) (id: string) =
    books.Books.Customers.TryFind id |> Option.map _.Name |> Option.defaultValue id

let private termsLabel =
    function
    | Some terms -> Documents.termsText terms
    | None -> "Organization default"

let private bucketText =
    function
    | NotYetDue -> "Not yet due"
    | Days1To30 -> "1-30 days"
    | Days31To60 -> "31-60 days"
    | Days61To90 -> "61-90 days"
    | Over90 -> "Over 90 days"

/// What the main area shows for the place the URL names (SUM-LINK-007).
type Screen =
    | Showing of Routes.Place
    /// No such place, or no such record in these books.
    | Missing
    | Forbidden
    /// A parameter Summa does not understand, or a damaged address.
    | InvalidLink of explanation: string
    /// A draft that has since been issued: say so and link the invoice.
    | IssuedDraft of draftId: string * invoiceId: string

let screen (model: Model) =
    match model.Unrouted, model.Books with
    | Some(Limen.Routing.RouteError.NotPermitted _), _ -> Forbidden
    | Some(Limen.Routing.RouteError.Invalid(_, parameter, value, expected)), _ ->
        InvalidLink $"The link's '{parameter}' is '{value}', but Summa expects {expected}."
    | Some(Limen.Routing.RouteError.Malformed part), _ -> InvalidLink $"The link is damaged ({part}). Check that it was copied whole."
    | Some _, _ -> Missing
    | None, books ->
        let holds (pick: Receivables -> Map<string, 'a>) id = books |> Option.forall (fun b -> (pick b).ContainsKey id)

        match model.Place with
        | Routes.Invoice(id, _) when not (holds (fun b -> b.Books.Invoices) id) -> Missing
        | Routes.Draft id when not (holds (fun b -> b.Books.Drafts) id) ->
            match books |> Option.bind (fun b -> b.Books.IssuedFrom.TryFind id) with
            | Some invoiceId -> IssuedDraft(id, invoiceId)
            | None -> Missing
        | Routes.Home
        | Routes.Customers _
        | Routes.Invoices _
        | Routes.NewInvoice _
        | Routes.Draft _
        | Routes.Invoice _
        | Routes.Receivables _
        | Routes.Settings as place -> Showing place
        | Routes.SignIn _
        | Routes.NotFound -> Missing
        | Routes.Customer(id, _) when not (holds (fun b -> b.Books.Customers) id) -> Missing
        | Routes.Payment id when not (holds (fun b -> b.Payments) id) -> Missing
        | Routes.CreditMemo id when not (holds (fun b -> b.CreditMemos) id) -> Missing
        | Routes.Engagement id when not (holds (fun b -> b.Books.Engagements) id) -> Missing
        | Routes.Customer _
        | Routes.Payments _
        | Routes.Payment _
        | Routes.CreditMemos _
        | Routes.CreditMemo _
        | Routes.Engagements _
        | Routes.Engagement _ as place -> Showing place
        | Routes.Proposal id when not (holds (fun b -> b.Books.Proposals) id) -> Missing
        | Routes.Proposals
        | Routes.Proposal _ as place -> Showing place
        | Routes.JournalEntry id when not (holds (fun b -> b.Books.Ledger.Entries) id) -> Missing
        | Routes.Ledger(Some account, _, _) when not (holds (fun b -> b.Books.Ledger.Accounts) account) -> Missing
        | Routes.Periods _
        | Routes.Period _
        | Routes.Ledger _
        | Routes.JournalEntry _
        | Routes.Reports
        | Routes.TrialBalance _
        | Routes.IncomeStatement _
        | Routes.BalanceSheet _
        | Routes.Work
        | Routes.FollowUp _
        | Routes.Inbox _
        | Routes.Cpa _ as place -> Showing place

let private statusKey =
    function
    | Issued -> "unpaid"
    | PartiallyPaid -> "partly-paid"
    | Paid -> "paid"
    | WrittenOff -> "written-off"
    | Voided -> "voided"

let private containsText (search: string option) (texts: string list) =
    match search with
    | None -> true
    | Some term -> texts |> List.exists (fun t -> t.Contains(term, StringComparison.OrdinalIgnoreCase))

let private dateInput = Option.map (fun (d: DateOnly) -> d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) >> Option.defaultValue ""

/// The environment banner (SUM0-040): every non-production environment
/// shows it, and a deployment that keeps books in the browser says so.
let banner (model: Model) =
    match model.Configuration with
    | Configured config ->
        Environments.banner config
        |> Option.map (fun b -> if config.Location.IsNone then b + " · books are kept only in this browser" else b)
    | _ -> None

/// The named values app/index.html binds to.
/// A select's customer options, each saying whether it is the one chosen.
/// The kernel cannot set a select's value before its options exist, so on
/// a link that names a customer the chosen option carries `selected` itself.
/// Who acted, as the history shows it: an agent's action says so, with its
/// identity and run (INV-PROV-001, SUM4-042), and is never shown as a person's.
let private actorText (who: string) (provenance: Provenance option) =
    match provenance with
    | Some { ActorKind = "agent"; Agent = agent; ExecutionId = execution } ->
        let identity = agent |> Option.map (fun g -> $" ({g.Provider} {g.Model})") |> Option.defaultValue ""
        let run = execution |> Option.map (fun e -> $", run {e}") |> Option.defaultValue ""
        $"Agent {who}{identity}{run}"
    | Some { ActorKind = "automation" } -> $"Automation {who}"
    | _ -> who

let private customerChoices (books: Receivables option) (activeOnly: bool) (chosen: string option) =
    books
    |> Option.map (fun b ->
        b.Books.Customers
        |> Map.toList
        |> List.map snd
        |> List.filter (fun c -> not activeOnly || c.Active)
        |> List.sortBy _.Name
        |> List.map (fun c -> [ "value", Text c.Id; "label", Text c.Name; "selected", Flag(Some c.Id = chosen) ]))
    |> Option.defaultValue []

let private methodText =
    function
    | Ach -> "ACH"
    | Check -> "Check"
    | Wire -> "Wire"
    | CreditCard -> "Card"
    | PaymentMethod.Cash -> "Cash"
    | PaymentMethod.Other -> "Other"

let private customerHref id = Text(Routes.href (Routes.Customer(id, Routes.CustomerInvoices)))
let private invoiceHref id = Text(Routes.href (Routes.Invoice(id, Routes.Document)))
let private dateCell (d: DateOnly) = Text(Documents.dateText d)

/// A tab bar's items: each tab's value, label and whether it is selected.
let private tabs (current: 'a) (text: 'a -> string) (labels: ('a * string) list) =
    Items(labels |> List.map (fun (t, label) -> [ "value", Text(text t); "label", Text label; "selected", Text(if t = current then "true" else "false") ]))

/// The payments, customer, credit memo and engagement screens (WI-0030
/// slice 2), each opened from its link (WI-0043).
let private placeValues (model: Model) (shown: Screen) : View =
    let books = model.Books
    let value' name (v: Scalar) = name, Value v
    let itemsOf (rows: (string * Scalar) list list) = Items rows
    let on (pick: Routes.Place -> bool) = match shown with Showing place -> pick place | _ -> false
    let nameOf (b: Receivables) id = customerName b id

    // ---- Payments
    let paymentFilters =
        match model.Place with
        | Routes.Payments list -> list
        | _ -> Routes.allPayments

    let allPaymentsList = books |> Option.map (fun b -> b.Payments |> Map.toList |> List.map snd) |> Option.defaultValue []

    let listedPayments =
        books
        |> Option.map (fun b ->
            allPaymentsList
            |> List.filter (fun p ->
                containsText paymentFilters.Search [ p.Reference; p.Id; nameOf b p.CustomerId ]
                && paymentFilters.Customer |> Option.forall ((=) p.CustomerId)
                && paymentFilters.From |> Option.forall (fun d -> p.DateReceived >= d)
                && paymentFilters.To |> Option.forall (fun d -> p.DateReceived <= d)
                && (not paymentFilters.Unapplied || (unallocated b p).Minor > 0L))
            |> List.sortBy (fun p -> p.DateReceived, p.Id)
            |> fun rows -> if paymentFilters.Sort = Routes.NewestPayment then List.rev rows else rows
            |> List.map (fun p ->
                [ "id", Text p.Id
                  "href", Text(Routes.href (Routes.Payment p.Id))
                  "date", dateCell p.DateReceived
                  "customer", Text(nameOf b p.CustomerId)
                  "customerHref", customerHref p.CustomerId
                  "method", Text(methodText p.Method)
                  "reference", Text(if p.Reference = "" then "-" else p.Reference)
                  "amount", money p.Amount
                  "unapplied", money (unallocated b p) ]))
        |> Option.defaultValue []

    let payment =
        match shown, books with
        | Showing(Routes.Payment id), Some b -> b.Payments.TryFind id |> Option.map (fun p -> b, p)
        | _ -> None

    let payText (f: Receivables -> Payment -> string) = Value(Text(payment |> Option.map (fun (b, p) -> f b p) |> Option.defaultValue ""))

    // ---- One customer
    let customer =
        match shown, books with
        | Showing(Routes.Customer(id, tab)), Some b -> b.Books.Customers.TryFind id |> Option.map (fun c -> b, c, tab)
        | _ -> None

    let custText (f: Receivables -> Customer -> string) = Value(Text(customer |> Option.map (fun (b, c, _) -> f b c) |> Option.defaultValue ""))
    let customerTab = customer |> Option.map (fun (_, _, t) -> t) |> Option.defaultValue Routes.CustomerInvoices

    let custInvoices =
        customer
        |> Option.map (fun (b, c, _) ->
            b.Books.Invoices
            |> Map.toList
            |> List.map snd
            |> List.filter (fun i -> i.CustomerId = c.Id)
            |> List.sortByDescending (fun i -> i.IssueDate, i.Number)
            |> List.map (fun i ->
                [ "id", Text i.InvoiceId
                  "href", invoiceHref i.InvoiceId
                  "number", Text i.Number
                  "issueDate", dateCell i.IssueDate
                  "status", Text(statusText (status b i))
                  "outstanding", money (outstanding b i) ]))
        |> Option.defaultValue []

    let custPayments =
        customer
        |> Option.map (fun (b, c, _) ->
            b.Payments
            |> Map.toList
            |> List.map snd
            |> List.filter (fun p -> p.CustomerId = c.Id)
            |> List.sortByDescending (fun p -> p.DateReceived, p.Id)
            |> List.map (fun p ->
                [ "id", Text p.Id
                  "href", Text(Routes.href (Routes.Payment p.Id))
                  "date", dateCell p.DateReceived
                  "reference", Text(if p.Reference = "" then "-" else p.Reference)
                  "amount", money p.Amount ]))
        |> Option.defaultValue []

    // ---- Credit memos
    let memoCustomer =
        match model.Place with
        | Routes.CreditMemos customerId -> customerId
        | _ -> None

    let memoRows (b: Receivables) (memos: CreditMemo list) =
        memos
        |> List.sortByDescending (fun m -> m.IssueDate, m.Id)
        |> List.map (fun m ->
            let invoice = m.InvoiceId |> Option.bind b.Books.Invoices.TryFind

            [ "id", Text m.Id
              "href", Text(Routes.href (Routes.CreditMemo m.Id))
              "date", dateCell m.IssueDate
              "customer", Text(nameOf b m.CustomerId)
              "customerHref", customerHref m.CustomerId
              "invoice", Text(invoice |> Option.map _.Number |> Option.defaultValue "-")
              "invoiceHref", Text(invoice |> Option.map (fun i -> Routes.href (Routes.Invoice(i.InvoiceId, Routes.Document))) |> Option.defaultValue "#/")
              "reason", Text m.Reason
              "amount", money m.Amount
              "remaining", money (Credits.remaining b (FromCreditMemo m.Id) |> Option.defaultValue (zero m.Amount.Currency)) ])

    let listedMemos =
        books
        |> Option.map (fun b -> b.CreditMemos |> Map.toList |> List.map snd |> List.filter (fun m -> memoCustomer |> Option.forall ((=) m.CustomerId)) |> memoRows b)
        |> Option.defaultValue []

    let custCredits =
        customer
        |> Option.map (fun (b, c, _) -> b.CreditMemos |> Map.toList |> List.map snd |> List.filter (fun m -> m.CustomerId = c.Id) |> memoRows b)
        |> Option.defaultValue []

    let memo =
        match shown, books with
        | Showing(Routes.CreditMemo id), Some b -> b.CreditMemos.TryFind id |> Option.map (fun m -> b, m, CreditMemoDocuments.ofCreditMemo b id |> Result.toOption)
        | _ -> None

    let memoDoc = memo |> Option.bind (fun (_, _, doc) -> doc)
    let docText (f: CreditMemoDocuments.CreditMemoDocument -> string) = Value(Text(memoDoc |> Option.map f |> Option.defaultValue ""))
    let memoText (f: Receivables -> CreditMemo -> string) = Value(Text(memo |> Option.map (fun (b, m, _) -> f b m) |> Option.defaultValue ""))

    // ---- Engagements
    let engagementCustomer =
        match model.Place with
        | Routes.Engagements customerId -> customerId
        | _ -> None

    let feeText (e: Engagement) = e.FixedFee |> Option.map Documents.moneyText |> Option.defaultValue "Hourly"

    let listedEngagements =
        books
        |> Option.map (fun b ->
            b.Books.Engagements
            |> Map.toList
            |> List.map snd
            |> List.filter (fun e -> engagementCustomer |> Option.forall ((=) e.CustomerId))
            |> List.sortBy (fun e -> nameOf b e.CustomerId, e.Name)
            |> List.map (fun e ->
                [ "id", Text e.Id
                  "href", Text(Routes.href (Routes.Engagement e.Id))
                  "name", Text e.Name
                  "customer", Text(nameOf b e.CustomerId)
                  "customerHref", customerHref e.CustomerId
                  "fee", Text(feeText e) ]))
        |> Option.defaultValue []

    let engagement =
        match shown, books with
        | Showing(Routes.Engagement id), Some b -> b.Books.Engagements.TryFind id |> Option.map (fun e -> b, e)
        | _ -> None

    let engText (f: Receivables -> Engagement -> string) = Value(Text(engagement |> Option.map (fun (b, e) -> f b e) |> Option.defaultValue ""))

    [ "onPayments", Value(Flag(on (function Routes.Payments _ -> true | _ -> false)))
      "onPayment", Value(Flag(on (function Routes.Payment _ -> true | _ -> false)))
      "onCustomer", Value(Flag(on (function Routes.Customer _ -> true | _ -> false)))
      "onCreditMemos", Value(Flag(on (function Routes.CreditMemos _ -> true | _ -> false)))
      "onCreditMemo", Value(Flag(on (function Routes.CreditMemo _ -> true | _ -> false)))
      "onEngagements", Value(Flag(on (function Routes.Engagements _ -> true | _ -> false)))
      "onEngagement", Value(Flag(on (function Routes.Engagement _ -> true | _ -> false)))
      "paymentCustomerOptions", itemsOf (customerChoices books false paymentFilters.Customer)
      "memoCustomerOptions", itemsOf (customerChoices books false memoCustomer)
      "engagementsCustomerOptions", itemsOf (customerChoices books false engagementCustomer)
      "engagementFormCustomerOptions", itemsOf (customerChoices books true (nonEmpty model.Engagement.CustomerId))
      // Payments
      "hasPayments", Value(Flag(not allPaymentsList.IsEmpty))
      "hasListedPayments", Value(Flag(not listedPayments.IsEmpty))
      "noListedPayments", Value(Flag(not allPaymentsList.IsEmpty && listedPayments.IsEmpty))
      "paymentCountText", Value(Text $"{listedPayments.Length} of {allPaymentsList.Length} payments")
      "payments", itemsOf listedPayments
      "paymentSearch", Value(Text(paymentFilters.Search |> Option.defaultValue ""))
      "paymentCustomer", Value(Text(paymentFilters.Customer |> Option.defaultValue ""))
      "paymentFrom", Value(Text(dateInput paymentFilters.From))
      "paymentTo", Value(Text(dateInput paymentFilters.To))
      "paymentUnapplied", Value(Flag paymentFilters.Unapplied)
      "paymentSort", Value(Text(Routes.paymentSortText paymentFilters.Sort))
      "isPaymentFiltered", Value(Flag(paymentFilters <> Routes.allPayments))
      // One payment
      "payId", payText (fun _ p -> p.Id)
      "payDate", payText (fun _ p -> Documents.dateText p.DateReceived)
      "payCustomer", payText (fun b p -> nameOf b p.CustomerId)
      "payCustomerHref", payText (fun _ p -> Routes.href (Routes.Customer(p.CustomerId, Routes.CustomerInvoices)))
      "payMethod", payText (fun _ p -> methodText p.Method)
      "payReference", payText (fun _ p -> if p.Reference = "" then "-" else p.Reference)
      "payAmount", payText (fun _ p -> Documents.moneyText p.Amount)
      "payUnapplied", payText (fun b p -> Documents.moneyText (unallocated b p))
      "payReversed", Value(Flag(payment |> Option.exists (fun (b, p) -> b.Reversals.ContainsKey p.Id)))
      "payAllocations",
      itemsOf (
          payment
          |> Option.map (fun (b, p) ->
              b.Allocations
              |> List.filter (fun a -> a.PaymentId = p.Id)
              |> List.map (fun (a: Allocation) ->
                  [ "key", Text a.Id
                    "invoice", Text(b.Books.Invoices.TryFind a.InvoiceId |> Option.map _.Number |> Option.defaultValue a.InvoiceId)
                    "href", invoiceHref a.InvoiceId
                    "amount", money a.Amount ]))
          |> Option.defaultValue []
      )
      // One customer
      "custName", custText (fun _ c -> c.Name)
      "custBillingName", custText (fun _ c -> c.BillingName)
      "custEmail", custText (fun _ c -> if c.Email = "" then "-" else c.Email)
      "custAddress", custText (fun _ c -> c.BillingAddress)
      "custTerms", custText (fun _ c -> termsLabel c.DefaultTerms)
      "custBalance",
      custText (fun b c -> b.Books.Invoices |> Map.toList |> List.map snd |> List.filter (fun i -> i.CustomerId = c.Id) |> List.map (outstanding b) |> sum "USD" |> Documents.moneyText)
      "custCredit", custText (fun b c -> Credits.available b c.Id |> List.map snd |> sum "USD" |> Documents.moneyText)
      "custNewInvoiceHref", custText (fun _ c -> Routes.href (Routes.NewInvoice(Some c.Id)))
      "custStatus", custText (fun _ c -> if c.Active then "Active" else "Inactive")
      "customerTabs",
      tabs customerTab Routes.customerTabText [ Routes.CustomerInvoices, "Invoices"; Routes.CustomerPayments, "Payments"; Routes.CustomerCredits, "Credit memos" ]
      "onCustInvoicesTab", Value(Flag(customerTab = Routes.CustomerInvoices))
      "onCustPaymentsTab", Value(Flag(customerTab = Routes.CustomerPayments))
      "onCustCreditsTab", Value(Flag(customerTab = Routes.CustomerCredits))
      "custInvoices", itemsOf custInvoices
      "hasCustInvoices", Value(Flag(not custInvoices.IsEmpty))
      "custPayments", itemsOf custPayments
      "hasCustPayments", Value(Flag(not custPayments.IsEmpty))
      "custCredits", itemsOf custCredits
      "hasCustCredits", Value(Flag(not custCredits.IsEmpty))
      // Credit memos
      "creditMemos", itemsOf listedMemos
      "hasCreditMemos", Value(Flag(not listedMemos.IsEmpty))
      "memoCustomer", Value(Text(memoCustomer |> Option.defaultValue ""))
      // One credit memo, as its Folio document
      "cmHasDocument", Value(Flag memoDoc.IsSome)
      "cmNoDocument", Value(Flag(memo.IsSome && memoDoc.IsNone))
      "cmId", memoText (fun _ m -> m.Id)
      "cmIssuer", docText _.Issuer.LegalName
      "cmIssuerAddress", docText _.Issuer.Address
      "cmCustomer", docText _.Customer.BillingName
      "cmCustomerAddress", docText _.Customer.BillingAddress
      "cmIssueDate", memoText (fun _ m -> Documents.dateText m.IssueDate)
      "cmInvoice", docText (fun d -> fst d.Credits)
      "cmInvoiceDate", docText (fun d -> Documents.dateText (snd d.Credits))
      "cmInvoiceHref", memoText (fun _ m -> m.InvoiceId |> Option.map (fun id -> Routes.href (Routes.Invoice(id, Routes.Document))) |> Option.defaultValue "#/")
      "cmReason", memoText (fun _ m -> m.Reason)
      "cmAmount", memoText (fun _ m -> Documents.moneyText m.Amount)
      "cmRemaining", memoText (fun b m -> Credits.remaining b (FromCreditMemo m.Id) |> Option.map Documents.moneyText |> Option.defaultValue "")
      "cmHasLines", Value(Flag(memoDoc |> Option.exists (fun d -> not d.Lines.IsEmpty)))
      "cmLines",
      itemsOf (
          memoDoc
          |> Option.map (fun d ->
              d.Lines
              |> List.mapi (fun i l ->
                  [ "key", Text(string i)
                    "description", Text l.Description
                    "quantity", Text(Documents.quantityText l.QuantityThousandths)
                    "amount", Text(Documents.amountText l.Amount) ]))
          |> Option.defaultValue []
      )
      "cmApplications",
      itemsOf (
          memo
          |> Option.map (fun (b, m, _) ->
              b.Applications
              |> List.filter (fun a -> a.Source = FromCreditMemo m.Id)
              |> List.map (fun a ->
                  [ "key", Text a.Id
                    "invoice", Text(b.Books.Invoices.TryFind a.InvoiceId |> Option.map _.Number |> Option.defaultValue a.InvoiceId)
                    "href", invoiceHref a.InvoiceId
                    "date", dateCell a.Date
                    "amount", money a.Amount ]))
          |> Option.defaultValue []
      )
      // Credit on the invoice shown
      "creditAmount", Value(Text model.Credit.Amount)
      "creditReason", Value(Text model.Credit.Reason)
      // Engagements
      "engagements", itemsOf listedEngagements
      "hasEngagements", Value(Flag(not listedEngagements.IsEmpty))
      "engagementCustomer", Value(Text(engagementCustomer |> Option.defaultValue ""))
      "engagementFormCustomer", Value(Text model.Engagement.CustomerId)
      "engagementFormName", Value(Text model.Engagement.Name)
      "engagementFormFee", Value(Text model.Engagement.FixedFee)
      "engName", engText (fun _ e -> e.Name)
      "engCustomer", engText (fun b e -> nameOf b e.CustomerId)
      "engCustomerHref", engText (fun _ e -> Routes.href (Routes.Customer(e.CustomerId, Routes.CustomerInvoices)))
      "engFee", engText (fun _ e -> feeText e)
      "engTerms", engText (fun _ e -> termsLabel e.Terms)
      "engNewInvoiceHref", engText (fun _ e -> Routes.href (Routes.NewInvoice(Some e.CustomerId)))
      "engMilestones",
      itemsOf (
          engagement
          |> Option.map (fun (_, e) ->
              e.Milestones
              |> List.map (fun m ->
                  [ "key", Text m.Id
                    "label", Text m.Label
                    "amount", Text(Billing.milestoneAmount e m |> Option.map Documents.moneyText |> Option.defaultValue "Share of an unset fee")
                    "state", Text(m.CompletedOn |> Option.map (fun d -> "Completed " + Documents.dateText d) |> Option.defaultValue "Open") ]))
          |> Option.defaultValue []
      )
      "engHasMilestones", Value(Flag(engagement |> Option.exists (fun (_, e) -> not e.Milestones.IsEmpty)))
      "engInvoices",
      itemsOf (
          engagement
          |> Option.map (fun (b, e) ->
              b.Books.Invoices
              |> Map.toList
              |> List.map snd
              |> List.filter (fun i -> i.EngagementId = Some e.Id)
              |> List.map (fun i ->
                  [ "id", Text i.InvoiceId
                    "href", invoiceHref i.InvoiceId
                    "number", Text i.Number
                    "issueDate", dateCell i.IssueDate
                    "total", money i.Total ]))
          |> Option.defaultValue []
      ) ]

let private monthLabel (year: int, month: int) =
    DateOnly(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture)

let private periodStateText =
    function
    | PeriodState.Open -> "Open"
    | PeriodState.Closed -> "Closed"
    | PeriodState.Locked -> "Locked"

/// Where a journal entry came from (v0.4 §22): the invoice, payment, credit
/// memo, credit application or write-off that posted it.
let private originOf (r: Receivables) (entryId: string) : (string * Routes.Place) option =
    [ fun () ->
          r.Books.Invoices
          |> Map.tryPick (fun _ i -> if i.JournalEntryId = entryId then Some($"Invoice {i.Number}", Routes.Invoice(i.InvoiceId, Routes.Document)) else None)
      fun () -> r.Allocations |> List.tryFind (fun a -> a.JournalEntryId = entryId) |> Option.map (fun a -> $"Payment {a.PaymentId}", Routes.Payment a.PaymentId)
      fun () -> r.CreditMemos |> Map.tryPick (fun _ m -> if m.JournalEntryId = entryId then Some($"Credit memo {m.Id}", Routes.CreditMemo m.Id) else None)
      fun () ->
          r.Applications
          |> List.tryFind (fun a -> a.JournalEntryId = entryId)
          |> Option.map (fun a -> "Credit applied to an invoice", Routes.Invoice(a.InvoiceId, Routes.InvoicePayments))
      fun () -> r.WriteOffs |> Map.tryPick (fun _ w -> if w.JournalEntryId = entryId then Some("Write-off", Routes.Invoice(w.InvoiceId, Routes.History)) else None) ]
    |> List.tryPick (fun find -> find ())

/// The general ledger, journal entries, reports and accounting periods
/// (WI-0030 slice 3), each with its account, dates and basis in the link.
let private bookValues (model: Model) (shown: Screen) : View =
    let books = model.Books
    let ledger = books |> Option.map (fun b -> b.Books.Ledger)
    let usd = zero "USD"
    let amount (m: Money) = Text(if m.Minor = 0L then "" else Documents.amountText m)
    let entryHref id = Text(Routes.href (Routes.JournalEntry id))
    let accountName id = ledger |> Option.bind (fun l -> l.Accounts.TryFind id) |> Option.map (fun a -> $"{a.Code} {a.Name}") |> Option.defaultValue id
    let flagOf b = Value(Flag b)
    let textOf (t: string) = Value(Text t)
    let within (from: DateOnly option) (until: DateOnly option) (d: DateOnly) = from |> Option.forall (fun f -> d >= f) && until |> Option.forall (fun u -> d <= u)

    // ---- The general ledger
    let ledgerAccount, ledgerFrom, ledgerTo =
        match model.Place with
        | Routes.Ledger(account, from, until) -> account, from, until
        | _ -> None, None, None

    let accountOptions =
        ledger
        |> Option.map (fun l ->
            l.Accounts
            |> Map.toList
            |> List.map snd
            |> List.sortBy _.Code
            |> List.map (fun a -> [ "value", Text a.Id; "label", Text $"{a.Code} {a.Name}"; "selected", Flag(Some a.Id = ledgerAccount) ]))
        |> Option.defaultValue []

    let accountRows =
        match ledger, ledgerAccount with
        | Some l, Some account when l.Accounts.ContainsKey account -> Reports.generalLedger "USD" account l
        | _ -> []

    let opening =
        accountRows
        |> List.takeWhile (fun r -> ledgerFrom |> Option.exists (fun f -> r.Date < f))
        |> List.tryLast
        |> Option.map _.RunningBalance
        |> Option.defaultValue usd

    let ledgerRows =
        accountRows
        |> List.filter (fun r -> within ledgerFrom ledgerTo r.Date)
        |> List.mapi (fun i r ->
            [ "key", Text $"{r.EntryId}-{i}"
              "date", Text(Documents.dateText r.Date)
              "entry", Text r.EntryId
              "href", entryHref r.EntryId
              "description", Text r.Description
              "debit", amount r.Debit
              "credit", amount r.Credit
              "balance", Text(Documents.amountText r.RunningBalance) ])

    let journalRows =
        match ledger, ledgerAccount with
        | Some l, None ->
            l.Journal
            |> List.map (fun id -> l.Entries[id])
            |> List.filter (fun e -> within ledgerFrom ledgerTo e.Date)
            |> List.map (fun e ->
                [ "key", Text e.Id
                  "date", Text(Documents.dateText e.Date)
                  "entry", Text e.Id
                  "href", entryHref e.Id
                  "description", Text e.Description
                  "amount", Text(Documents.amountText (fst (totals "USD" e.Lines))) ])
        | _ -> []

    // ---- One journal entry
    let entry =
        match shown, ledger with
        | Showing(Routes.JournalEntry id), Some l -> l.Entries.TryFind id |> Option.map (fun e -> l, e)
        | _ -> None

    let entryText (f: PostedEntry -> string) = textOf (entry |> Option.map (snd >> f) |> Option.defaultValue "")

    // ---- Reports
    let today = model.Today

    let reportAsOf =
        match model.Place with
        | Routes.TrialBalance asOf
        | Routes.BalanceSheet asOf -> asOf
        | _ -> None

    let asOfDate = reportAsOf |> Option.defaultValue today

    let trial =
        match shown, ledger with
        | Showing(Routes.TrialBalance _), Some l -> Some(Reports.trialBalance "USD" asOfDate l)
        | _ -> None

    let sheet =
        match shown, ledger with
        | Showing(Routes.BalanceSheet _), Some l -> Some(Reports.balanceSheet "USD" asOfDate l)
        | _ -> None

    let incomeFrom, incomeTo, basis =
        match model.Place with
        | Routes.IncomeStatement(from, until, basis) -> from, until, basis
        | _ -> None, None, Routes.Accrual

    let fromDate = incomeFrom |> Option.defaultValue (DateOnly(today.Year, 1, 1))
    let toDate = incomeTo |> Option.defaultValue today

    let income =
        match shown, books, model.Manifest with
        | Showing(Routes.IncomeStatement _), Some b, Some manifest ->
            match basis with
            | Routes.Accrual ->
                let s = Reports.incomeStatement "USD" fromDate toDate b.Books.Ledger
                Some(s.Revenue, s.Expenses, s.NetIncome)
            | Routes.Cash ->
                let cash = Organization.accountByCode b.Books.Ledger manifest.Accounting.CashAccount
                let s = Periods.cashBasis "USD" fromDate toDate [ cash ] b
                Some(s.Revenue, s.Expenses, s.NetIncome)
        | _ -> None

    let money' (pick: 'r -> Money) (report: 'r option) = textOf (report |> Option.map (pick >> Documents.moneyText) |> Option.defaultValue "")

    // ---- Periods
    let year =
        match model.Place with
        | Routes.Periods year -> year |> Option.defaultValue today.Year
        | Routes.Period(year, _) -> year
        | _ -> today.Year

    let stateOf (l: Ledger) (y, m) = l.Periods.TryFind((y, m)) |> Option.defaultValue PeriodState.Open
    let inMonth (y, m) (d: DateOnly) = d.Year = y && d.Month = m

    let monthRows =
        ledger
        |> Option.map (fun l ->
            [ 1..12 ]
            |> List.map (fun m ->
                [ "key", Text $"{year:D4}-{m:D2}"
                  "label", Text(monthLabel (year, m))
                  "href", Text(Routes.href (Routes.Period(year, m)))
                  "state", Text(periodStateText (stateOf l (year, m)))
                  "entries", Text(string (l.Journal |> List.filter (fun id -> inMonth (year, m) l.Entries[id].Date) |> List.length)) ]))
        |> Option.defaultValue []

    let period =
        match shown, books with
        | Showing(Routes.Period(y, m)), Some b -> Some(b, (y, m))
        | _ -> None

    let periodState = period |> Option.map (fun (b, p) -> stateOf b.Books.Ledger p)

    let periodEntries =
        period
        |> Option.map (fun (b, p) ->
            let l = b.Books.Ledger

            l.Journal
            |> List.map (fun id -> l.Entries[id])
            |> List.filter (fun e -> inMonth p e.Date)
            |> List.map (fun e ->
                [ "key", Text e.Id
                  "date", Text(Documents.dateText e.Date)
                  "entry", Text e.Id
                  "href", entryHref e.Id
                  "description", Text e.Description
                  "amount", Text(Documents.amountText (fst (totals "USD" e.Lines))) ]))
        |> Option.defaultValue []

    // What to look at before closing a period (v0.4 period close).
    let checks =
        period
        |> Option.map (fun (b, (y, m)) ->
            let monthEnd = DateOnly(y, m, DateTime.DaysInMonth(y, m))
            let tb = Reports.trialBalance "USD" monthEnd b.Books.Ledger
            let unapplied = b.Payments |> Map.toList |> List.map snd |> List.filter (fun p -> inMonth (y, m) p.DateReceived && (unallocated b p).Minor > 0L)

            [ "trial", "Trial balance at month end", (if tb.TotalDebits = tb.TotalCredits then "Balances" else "Does not balance"), tb.TotalDebits = tb.TotalCredits
              "drafts", "Drafts not yet issued", string b.Books.Drafts.Count, b.Books.Drafts.IsEmpty
              "unapplied", "Payments received this month not fully applied", string unapplied.Length, unapplied.IsEmpty ]
            |> List.map (fun (key, label, result, ok) -> [ "key", Text key; "label", Text label; "result", Text result; "tone", Text(if ok then "ok" else "attention") ]))
        |> Option.defaultValue []

    let placeIs (pick: Routes.Place -> bool) =
        flagOf (
            match shown with
            | Showing place -> pick place
            | _ -> false
        )

    [ "onLedger", placeIs (function Routes.Ledger _ -> true | _ -> false)
      "onJournalEntry", placeIs (function Routes.JournalEntry _ -> true | _ -> false)
      "onReports", placeIs ((=) Routes.Reports)
      "onTrialBalance", placeIs (function Routes.TrialBalance _ -> true | _ -> false)
      "onIncomeStatement", placeIs (function Routes.IncomeStatement _ -> true | _ -> false)
      "onBalanceSheet", placeIs (function Routes.BalanceSheet _ -> true | _ -> false)
      "onPeriods", placeIs (function Routes.Periods _ -> true | _ -> false)
      "onPeriod", placeIs (function Routes.Period _ -> true | _ -> false)
      // Ledger
      "ledgerAccountOptions", Items accountOptions
      "ledgerAccount", textOf (ledgerAccount |> Option.defaultValue "")
      "ledgerAccountName", textOf (ledgerAccount |> Option.map accountName |> Option.defaultValue "")
      "ledgerFrom", textOf (dateInput ledgerFrom)
      "ledgerTo", textOf (dateInput ledgerTo)
      "isAccountLedger", flagOf ledgerAccount.IsSome
      "isJournal", flagOf ledgerAccount.IsNone
      "ledgerOpening", textOf (Documents.amountText opening)
      "hasLedgerOpening", flagOf ledgerFrom.IsSome
      "ledgerRows", Items ledgerRows
      "journalRows", Items journalRows
      "hasLedgerRows", flagOf (not ledgerRows.IsEmpty || not journalRows.IsEmpty)
      // One entry
      "entryId", entryText _.Id
      "entryDate", entryText (fun e -> Documents.dateText e.Date)
      "entryDescription", entryText _.Description
      "entrySource", entryText _.Source
      "entryPostedAt", entryText (fun e -> e.PostedAt.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
      "entryReverses", flagOf (entry |> Option.exists (fun (_, e) -> e.Reverses.IsSome))
      "entryReversesHref", textOf (entry |> Option.bind (fun (_, e) -> e.Reverses) |> Option.map (fun id -> Routes.href (Routes.JournalEntry id)) |> Option.defaultValue "")
      "entryReversesId", entryText (fun e -> e.Reverses |> Option.defaultValue "")
      "entryLines",
      Items(
          entry
          |> Option.map (fun (l, e) ->
              e.Lines
              |> List.mapi (fun i line ->
                  let debit, credit =
                      match line.Side with
                      | Debit m -> m, usd
                      | Credit m -> usd, m

                  [ "key", Text(string i)
                    "account", Text(accountName line.AccountId)
                    "href", Text(Routes.href (Routes.Ledger(Some line.AccountId, None, None)))
                    "memo", Text(line.Memo |> Option.defaultValue "")
                    "debit", amount debit
                    "credit", amount credit ]))
          |> Option.defaultValue []
      )
      "hasEntryOrigin", flagOf (entry |> Option.exists (fun (_, e) -> books |> Option.bind (fun b -> originOf b e.Id) |> Option.isSome))
      "entryOrigin", textOf (entry |> Option.bind (fun (_, e) -> books |> Option.bind (fun b -> originOf b e.Id)) |> Option.map fst |> Option.defaultValue "")
      "entryOriginHref",
      textOf (entry |> Option.bind (fun (_, e) -> books |> Option.bind (fun b -> originOf b e.Id)) |> Option.map (snd >> Routes.href) |> Option.defaultValue "")
      "entryDebits", textOf (entry |> Option.map (fun (_, e) -> Documents.amountText (fst (totals "USD" e.Lines))) |> Option.defaultValue "")
      "entryCredits", textOf (entry |> Option.map (fun (_, e) -> Documents.amountText (snd (totals "USD" e.Lines))) |> Option.defaultValue "")
      // Reports
      "trialBalanceHref", textOf (Routes.href (Routes.TrialBalance None))
      "incomeStatementHref", textOf (Routes.href (Routes.IncomeStatement(None, None, Routes.Accrual)))
      "balanceSheetHref", textOf (Routes.href (Routes.BalanceSheet None))
      "reportAsOf", textOf (dateInput reportAsOf)
      "reportDate", textOf (Documents.dateText asOfDate)
      "trialRows",
      Items(
          trial
          |> Option.map (fun tb ->
              tb.Rows
              |> List.map (fun r ->
                  [ "key", Text r.AccountId
                    "code", Text r.Code
                    "name", Text r.Name
                    "href", Text(Routes.href (Routes.Ledger(Some r.AccountId, None, Some asOfDate)))
                    "debit", amount r.Debit
                    "credit", amount r.Credit ]))
          |> Option.defaultValue []
      )
      "trialDebits", money' (fun (t: Reports.TrialBalance) -> t.TotalDebits) trial
      "trialCredits", money' (fun (t: Reports.TrialBalance) -> t.TotalCredits) trial
      "trialBalances", flagOf (trial |> Option.exists (fun tb -> tb.TotalDebits = tb.TotalCredits))
      "sheetAssets", money' (fun (b: Reports.BalanceSheet) -> b.Assets) sheet
      "sheetLiabilities", money' (fun (b: Reports.BalanceSheet) -> b.Liabilities) sheet
      "sheetEquity", money' (fun (b: Reports.BalanceSheet) -> b.Equity) sheet
      "sheetEarnings", money' (fun (b: Reports.BalanceSheet) -> b.CurrentEarnings) sheet
      "sheetBalances", flagOf (sheet |> Option.exists Reports.balances)
      "incomeFrom", textOf (dateInput incomeFrom)
      "incomeTo", textOf (dateInput incomeTo)
      "incomePeriod", textOf $"{Documents.dateText fromDate} to {Documents.dateText toDate}"
      "incomeBasis", textOf (Routes.basisText basis)
      "incomeBasisLabel", textOf (if basis = Routes.Cash then "Cash basis" else "Accrual basis")
      "incomeAccounts",
      Items(
          match shown, books, model.Manifest with
          | Showing(Routes.IncomeStatement _), Some b, Some manifest ->
              let ledgerHref account = Text(Routes.href (Routes.Ledger(Some account, Some fromDate, Some toDate)))

              match basis with
              | Routes.Accrual ->
                  let operating = Reports.withoutClosing b.Books.Ledger

                  operating.Accounts
                  |> Map.toList
                  |> List.map snd
                  |> List.filter (fun a -> a.Type = Revenue || a.Type = Expense)
                  |> List.sortBy _.Code
                  |> List.choose (fun a ->
                      let rows = Reports.generalLedger "USD" a.Id operating |> List.filter (fun r -> r.Date >= fromDate && r.Date <= toDate)
                      let debits = rows |> List.map _.Debit |> sum "USD"
                      let credits = rows |> List.map _.Credit |> sum "USD"
                      let net = if a.Type = Revenue then subtract credits debits else subtract debits credits

                      if net.Minor = 0L then None
                      else Some [ "key", Text a.Id; "account", Text $"{a.Code} {a.Name}"; "kind", Text(if a.Type = Revenue then "Revenue" else "Expense"); "amount", Text(Documents.amountText net); "href", ledgerHref a.Id ])
              | Routes.Cash ->
                  let cash = Organization.accountByCode b.Books.Ledger manifest.Accounting.CashAccount

                  (Periods.cashBasis "USD" fromDate toDate [ cash ] b).RevenueByAccount
                  |> List.filter (fun (_, m) -> m.Minor <> 0L)
                  |> List.map (fun (account, m) -> [ "key", Text account; "account", Text(accountName account); "kind", Text "Revenue received"; "amount", Text(Documents.amountText m); "href", ledgerHref account ])
          | _ -> []
      )
      "incomeRanges",
      Items(
          [ "this-month", "This month"; "last-month", "Last month"; "this-quarter", "This quarter"; "last-quarter", "Last quarter"; "year-to-date", "Year to date"; "last-year", "Last year" ]
          |> List.map (fun (key, label) ->
              let chosen = dateRange today key = Some(fromDate, toDate) && incomeFrom.IsSome
              [ "value", Text key; "label", Text label; "selected", Text(if chosen then "true" else "false") ])
      )
      "incomeRevenue", money' (fun (r, _, _) -> r) income
      "incomeExpenses", money' (fun (_, e, _) -> e) income
      "incomeNet", money' (fun (_, _, n) -> n) income
      // Periods
      "periodsYear", textOf (string year)
      "previousYearHref", textOf (Routes.href (Routes.Periods(Some(year - 1))))
      "nextYearHref", textOf (Routes.href (Routes.Periods(Some(year + 1))))
      "months", Items monthRows
      "periodLabel", textOf (period |> Option.map (snd >> monthLabel) |> Option.defaultValue "")
      "periodState", textOf (periodState |> Option.map periodStateText |> Option.defaultValue "")
      "periodYearHref", textOf (period |> Option.map (fun (_, (y, _)) -> Routes.href (Routes.Periods(Some y))) |> Option.defaultValue "")
      "periodEntries", Items periodEntries
      "periodChecks", Items checks
      "periodBlockers", Items(period |> Option.map (fun (b, p) -> periodBlockers b p |> List.mapi (fun i why -> [ "key", Text(string i); "blocker", Text why ])) |> Option.defaultValue [])
      "isPeriodBlocked", flagOf (periodState = Some PeriodState.Open && period |> Option.exists (fun (b, p) -> not (periodBlockers b p).IsEmpty))
      "canClosePeriod", flagOf (periodState = Some PeriodState.Open && period |> Option.exists (fun (b, p) -> (periodBlockers b p).IsEmpty))
      "canLockPeriod", flagOf (periodState = Some PeriodState.Closed)
      "canReopenPeriod", flagOf (periodState = Some PeriodState.Closed || periodState = Some PeriodState.Locked) ]

let private collectionText =
    function
    | NoFollowUp -> "No follow-up yet"
    | Reminded 1 -> "Reminded once"
    | Reminded n -> $"Reminded {n} times"
    | Escalated note -> $"Escalated: {note}"
    | OnHold reason -> $"On hold: {reason}"

let private disputeText =
    function
    | NotDisputed -> ""
    | Disputed(reason, on) -> $"Disputed on {Documents.dateText on}: {reason}"
    | DisputeResolved(resolution, on) -> $"Dispute resolved on {Documents.dateText on}: {resolution}"

/// The work queue, follow-up, payments inbox and CPA workspace (WI-0030
/// slice 5), each at its own address.
let private attentionValues (model: Model) (shown: Screen) : View =
    let books = model.Books
    let today = model.Today
    let flagOf b = Value(Flag b)
    let textOf (t: string) = Value(Text t)

    let placeIs (pick: Routes.Place -> bool) =
        flagOf (
            match shown with
            | Showing place -> pick place
            | _ -> false
        )

    let work = books |> Option.map (workItems today) |> Option.defaultValue []

    // Follow-up
    let view =
        match model.Place with
        | Routes.FollowUp view -> view
        | _ -> Routes.OverdueInvoices

    let followRows =
        books
        |> Option.map (fun b ->
            openInvoices today b
            |> List.filter (fun i ->
                match view, (Lifecycle.followUp b i.InvoiceId).Dispute with
                | Routes.OverdueInvoices, _ -> isOverdue today b i
                | Routes.DisputedInvoices, Disputed _ -> true
                | Routes.DisputedInvoices, _ -> false
                | Routes.AllOpenInvoices, _ -> true)
            |> List.map (fun i ->
                let follow = Lifecycle.followUp b i.InvoiceId

                [ "id", Text i.InvoiceId
                  "href", Text(Routes.href (Routes.Invoice(i.InvoiceId, Routes.InvoicePayments)))
                  "number", Text i.Number
                  "customer", Text i.Customer.Name
                  "dueDate", Text(Documents.dateText i.DueDate)
                  "late", Text(if i.DueDate < today then $"{today.DayNumber - i.DueDate.DayNumber} days" else "Not due")
                  "outstanding", Text(Documents.moneyText (outstanding b i))
                  "collection", Text(collectionText follow.Collection)
                  "dispute", Text(disputeText follow.Dispute) ]))
        |> Option.defaultValue []

    // Inbox
    let inboxCustomer =
        match model.Place with
        | Routes.Inbox customerId -> customerId
        | _ -> None

    let inboxRows =
        books
        |> Option.map (fun b ->
            b.Payments
            |> Map.toList
            |> List.map snd
            |> List.filter (fun p -> (unallocated b p).Minor > 0L && inboxCustomer |> Option.forall ((=) p.CustomerId))
            |> List.sortBy (fun p -> p.DateReceived, p.Id)
            |> List.map (fun p ->
                let openFor = openInvoices today b |> List.filter (fun i -> i.CustomerId = p.CustomerId)

                [ "id", Text p.Id
                  "href", Text(Routes.href (Routes.Payment p.Id))
                  "date", Text(Documents.dateText p.DateReceived)
                  "customer", Text(b.Books.Customers.TryFind p.CustomerId |> Option.map _.Name |> Option.defaultValue p.CustomerId)
                  "reference", Text(if p.Reference = "" then "-" else p.Reference)
                  "unapplied", Text(Documents.moneyText (unallocated b p))
                  "openInvoices", Text(match openFor.Length with 0 -> "No open invoices" | 1 -> "1 open invoice" | n -> $"{n} open invoices")
                  "noApply", Flag openFor.IsEmpty ]))
        |> Option.defaultValue []

    // CPA workspace
    let year =
        match model.Place with
        | Routes.Cpa(Some year) -> year
        | _ -> today.Year

    let yearStart, yearEnd = DateOnly(year, 1, 1), DateOnly(year, 12, 31)

    let adjustments =
        books
        |> Option.map (fun b ->
            let l = b.Books.Ledger

            l.Journal
            |> List.map (fun id -> l.Entries[id])
            |> List.filter (fun e -> e.Date.Year = year)
            |> List.choose (fun e ->
                match Periods.kindOf e with
                | Periods.Adjusting(kind, _) -> Some(e, Periods.adjustingName kind)
                | Periods.OpeningBalance _ -> Some(e, "Opening balances")
                | Periods.YearEndClose _ -> Some(e, "Year-end close")
                | _ -> None)
            |> List.map (fun (e, kind) ->
                [ "key", Text e.Id
                  "href", Text(Routes.href (Routes.JournalEntry e.Id))
                  "date", Text(Documents.dateText e.Date)
                  "kind", Text kind
                  "description", Text e.Description ]))
        |> Option.defaultValue []

    let closedMonths =
        books |> Option.map (fun b -> [ 1..12 ] |> List.filter (fun m -> b.Books.Ledger.Periods.TryFind((year, m)) |> Option.exists ((<>) PeriodState.Open)) |> List.length) |> Option.defaultValue 0

    [ "onWork", placeIs ((=) Routes.Work)
      "onFollowUp", placeIs (function Routes.FollowUp _ -> true | _ -> false)
      "onInbox", placeIs (function Routes.Inbox _ -> true | _ -> false)
      "onCpa", placeIs (function Routes.Cpa _ -> true | _ -> false)
      // Work queue
      "workItems",
      Items(work |> List.mapi (fun i (kind, what, href) -> [ "key", Text(string i); "kind", Text kind; "text", Text what; "href", Text href ]))
      "hasWork", flagOf (not work.IsEmpty)
      "noWork", flagOf work.IsEmpty
      "workCount", textOf (string work.Length)
      "followUpHref", textOf (Routes.href (Routes.FollowUp Routes.OverdueInvoices))
      "inboxHref", textOf (Routes.href (Routes.Inbox None))
      "cpaHref", textOf (Routes.href (Routes.Cpa None))
      // Follow-up
      "followUpTabs", tabs view Routes.followUpText [ Routes.OverdueInvoices, "Overdue"; Routes.DisputedInvoices, "Disputed"; Routes.AllOpenInvoices, "All open" ]
      "followRows", Items followRows
      "hasFollowRows", flagOf (not followRows.IsEmpty)
      "noFollowRows", flagOf followRows.IsEmpty
      // Inbox
      "inboxRows", Items inboxRows
      "hasInboxRows", flagOf (not inboxRows.IsEmpty)
      "noInboxRows", flagOf inboxRows.IsEmpty
      "inboxCustomer", textOf (inboxCustomer |> Option.defaultValue "")
      "inboxCustomerOptions", Items(customerChoices books false inboxCustomer)
      "receiptCustomerOptions", Items(customerChoices books true (nonEmpty model.Receipt.CustomerId))
      "receiptCustomer", textOf model.Receipt.CustomerId
      "receiptAmount", textOf model.Receipt.Amount
      "receiptDate", textOf model.Receipt.Date
      "receiptMethod", textOf model.Receipt.Method
      "receiptReference", textOf model.Receipt.Reference
      // CPA workspace
      "cpaYear", textOf (string year)
      "cpaPreviousHref", textOf (Routes.href (Routes.Cpa(Some(year - 1))))
      "cpaNextHref", textOf (Routes.href (Routes.Cpa(Some(year + 1))))
      "cpaTrialHref", textOf (Routes.href (Routes.TrialBalance(Some yearEnd)))
      "cpaIncomeHref", textOf (Routes.href (Routes.IncomeStatement(Some yearStart, Some yearEnd, Routes.Accrual)))
      "cpaCashIncomeHref", textOf (Routes.href (Routes.IncomeStatement(Some yearStart, Some yearEnd, Routes.Cash)))
      "cpaSheetHref", textOf (Routes.href (Routes.BalanceSheet(Some yearEnd)))
      "cpaLedgerHref", textOf (Routes.href (Routes.Ledger(None, Some yearStart, Some yearEnd)))
      "cpaPeriodsHref", textOf (Routes.href (Routes.Periods(Some year)))
      "cpaAgingHref", textOf (Routes.href (Routes.Receivables(Some yearEnd, None)))
      "cpaClosedMonths", textOf $"{closedMonths} of 12 months closed"
      "cpaAdjustments", Items adjustments
      "hasCpaAdjustments", flagOf (not adjustments.IsEmpty)
      "canExport", flagOf model.Packs.Files ]

let private proposalStateText =
    function
    | Proposed -> "Proposed"
    | ReadyForReview -> "Ready for review"
    | Abandoned -> "Abandoned"
    | Accepted invoiceId -> $"Issued as {invoiceId}"

/// Who prepared a proposal: its first contribution.
let private preparedBy (p: Proposal) =
    p.Contributions |> List.tryHead |> Option.map (fun c -> actorText c.Who c.Provenance) |> Option.defaultValue "-"

/// Invoice proposals, an agent's among them, to inspect and approve or
/// abandon (SUM4-042, SUM4-043, INV-AGENT-004).
let private proposalValues (model: Model) (shown: Screen) : View =
    let books = model.Books
    let rows (items: (string * Scalar) list list) = Items items
    let on pick = Value(Flag(match shown with Showing place -> pick place | _ -> false))
    let proposalTotal (p: Proposal) = p.Lines |> List.map (fun l -> lineAmount l.Line) |> sum p.Currency

    let listed =
        books
        |> Option.map (fun b ->
            b.Books.Proposals
            |> Map.toList
            |> List.map snd
            |> List.sortByDescending _.CreatedAt
            |> List.map (fun p ->
                [ "id", Text p.Id
                  "href", Text(Routes.href (Routes.Proposal p.Id))
                  "customer", Text(customerName b p.CustomerId)
                  "state", Text(proposalStateText p.State)
                  "preparedBy", Text(preparedBy p)
                  "total", money (proposalTotal p) ]))
        |> Option.defaultValue []

    let shownProposal =
        match shown, books with
        | Showing(Routes.Proposal id), Some b -> b.Books.Proposals.TryFind id |> Option.map (fun p -> b, p)
        | _ -> None

    let explanation =
        match shownProposal, model.Manifest with
        | Some(b, p), Some manifest -> Some(Billing.explain b p (Organization.systemTerms manifest))
        | _ -> None

    let live = shownProposal |> Option.exists (fun (_, p) -> p.State = Proposed || p.State = ReadyForReview)
    let propText (f: Receivables -> Proposal -> string) = Value(Text(shownProposal |> Option.map (fun (b, p) -> f b p) |> Option.defaultValue ""))

    let textRows (pick: Billing.Explanation -> string list) =
        explanation |> Option.map (pick >> List.mapi (fun i t -> [ "key", Text(string i); "text", Text t ])) |> Option.defaultValue []

    [ "onProposals", on (function Routes.Proposals -> true | _ -> false)
      "onProposal", on (function Routes.Proposal _ -> true | _ -> false)
      "proposals", rows listed
      "hasProposals", Value(Flag(not listed.IsEmpty))
      "noProposals", Value(Flag listed.IsEmpty)
      "propId", propText (fun _ p -> p.Id)
      "propCustomer", propText (fun b p -> customerName b p.CustomerId)
      "propCustomerHref", propText (fun _ p -> Routes.href (Routes.Customer(p.CustomerId, Routes.CustomerInvoices)))
      "propState", propText (fun _ p -> proposalStateText p.State)
      "propPreparedBy", propText (fun _ p -> preparedBy p)
      "propTotal", propText (fun _ p -> Documents.moneyText (proposalTotal p))
      "propByAgent",
      Value(Flag(shownProposal |> Option.exists (fun (_, p) -> p.Contributions |> List.exists (fun c -> c.Provenance |> Option.exists (fun v -> v.ActorKind = "agent")))))
      "propIssuedHref",
      propText (fun _ p ->
          match p.State with
          | Accepted invoiceId -> Routes.href (Routes.Invoice(invoiceId, Routes.Document))
          | _ -> "")
      "propIsIssued", Value(Flag(shownProposal |> Option.exists (fun (_, p) -> match p.State with Accepted _ -> true | _ -> false)))
      "propLines",
      rows (
          shownProposal
          |> Option.map (fun (_, p) ->
              p.Lines
              |> List.mapi (fun i l ->
                  [ "key", Text(string i)
                    "description", Text l.Line.Description
                    "quantity", Text(Documents.quantityText l.Line.QuantityThousandths)
                    "rate", Text(if l.Priced then Documents.amountText l.Line.UnitPrice else "No rate yet")
                    "amount", Text(Documents.amountText (lineAmount l.Line)) ]))
          |> Option.defaultValue []
      )
      "propContributions",
      rows (
          shownProposal
          |> Option.map (fun (_, p) ->
              p.Contributions
              |> List.mapi (fun i c ->
                  [ "key", Text(string i)
                    "who", Text(actorText c.Who c.Provenance)
                    "what", Text(c.What.Replace('-', ' '))
                    "when", Text(c.At.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)) ]))
          |> Option.defaultValue []
      )
      "propSelected", rows (textRows _.Selected)
      "propExcluded", rows (textRows _.Excluded)
      "propHasExcluded", Value(Flag(explanation |> Option.exists (fun e -> not e.Excluded.IsEmpty)))
      "propRates", rows (textRows _.Rates)
      "propAssumptions", rows (textRows _.Assumptions)
      "propHasAssumptions", Value(Flag(explanation |> Option.exists (fun e -> not e.Assumptions.IsEmpty)))
      "propGrouping", Value(Text(explanation |> Option.map _.Grouping |> Option.defaultValue ""))
      "propTerms", Value(Text(explanation |> Option.map _.Terms |> Option.defaultValue ""))
      // The actions, keyed by the proposal, while it can still be decided.
      "propActions", rows (if live then shownProposal |> Option.map (fun (_, p) -> [ [ "id", Text p.Id ] ]) |> Option.toList |> List.concat else [])
      "propIsLive", Value(Flag live)
      "propLineOptions",
      rows (
          shownProposal
          |> Option.map (fun (_, p) ->
              p.Lines
              |> List.mapi (fun i l -> [ "value", Text(string i); "label", Text $"{i + 1}. {l.Line.Description}"; "selected", Flag(string i = model.ProposalRate.Line) ]))
          |> Option.defaultValue []
      )
      "propRateLine", Value(Text model.ProposalRate.Line)
      "propRate", Value(Text model.ProposalRate.Rate)
      "propRateReason", Value(Text model.ProposalRate.Reason) ]

let view (model: Model) : View =
    let books = model.Books
    let zeroUsd = zero "USD"

    let invoices =
        books
        |> Option.map (fun b -> b.Books.Invoices |> Map.toList |> List.map snd |> List.sortByDescending (fun i -> i.IssueDate, i.Number))
        |> Option.defaultValue []

    let outstandingOf (i: IssuedInvoice) = books |> Option.map (fun b -> outstanding b i) |> Option.defaultValue zeroUsd
    let owed = invoices |> List.map outstandingOf |> sum "USD"

    let overdue =
        invoices |> List.filter (fun i -> books |> Option.exists (fun b -> isOverdue model.Today b i))

    let drafts =
        books
        |> Option.map (fun b -> b.Books.Drafts |> Map.toList |> List.map snd)
        |> Option.defaultValue []

    let shown = screen model

    let showing (pick: Routes.Place -> bool) =
        flag (
            match shown with
            | Showing place -> pick place
            | _ -> false
        )

    let filters =
        match model.Place with
        | Routes.Invoices list -> list
        | _ -> Routes.allInvoices

    let statusOf (i: IssuedInvoice) = books |> Option.map (fun b -> status b i) |> Option.defaultValue Issued

    let listed =
        invoices
        |> List.filter (fun i ->
            containsText filters.Search [ i.Number; i.Customer.Name ]
            && (filters.Status.IsEmpty || filters.Status.Contains(statusKey (statusOf i)))
            && filters.Customer |> Option.forall ((=) i.CustomerId)
            && filters.From |> Option.forall (fun d -> i.IssueDate >= d)
            && filters.To |> Option.forall (fun d -> i.IssueDate <= d)
            && (not filters.Overdue || books |> Option.exists (fun b -> isOverdue model.Today b i)))
        |> fun rows ->
            match filters.Sort with
            | Routes.Newest -> rows
            | Routes.Oldest -> List.rev rows
            | Routes.DueFirst -> rows |> List.sortBy (fun i -> i.DueDate, i.Number)
            | Routes.ByNumber -> rows |> List.sortBy _.Number
            | Routes.ByCustomer -> rows |> List.sortBy (fun i -> i.Customer.Name, i.Number)

    let invoiceRows =
        listed
        |> List.map (fun i ->
            let st = books |> Option.map (fun b -> status b i) |> Option.defaultValue Issued

            [ "id", Text i.InvoiceId
              "href", Text(Routes.href (Routes.Invoice(i.InvoiceId, Routes.Document)))
              "number", Text i.Number
              "customer", Text i.Customer.Name
              "issueDate", Text(Documents.dateText i.IssueDate)
              "dueDate", Text(Documents.dateText i.DueDate)
              "total", money i.Total
              "outstanding", money (outstandingOf i)
              "status", Text(statusText st)
              "statusTone", Text(statusTone st)
              "timing", Text(timingText (timing model.Today i.DueDate (outstandingOf i))) ])

    let detail =
        match shown, books with
        | Showing(Routes.Invoice(id, _)), Some b -> b.Books.Invoices.TryFind id |> Option.map (fun i -> b, i)
        | _ -> None

    let doc = detail |> Option.map (fun (_, i) -> Documents.ofInvoice i)

    let docText (f: Documents.InvoiceDocument -> string) = text (doc |> Option.map f |> Option.defaultValue "")

    let tab =
        match model.Place with
        | Routes.Invoice(_, tab) -> tab
        | _ -> Routes.Document

    let history =
        detail
        |> Option.map (fun (b, i) ->
            let draft = b.Books.IssuedFrom |> Map.tryFindKey (fun _ invoiceId -> invoiceId = i.InvoiceId)

            b.Books.Ledger.Audit
            |> List.filter (fun a -> a.Subject = i.InvoiceId || Some a.Subject = draft)
            |> List.sortBy _.When
            |> List.mapi (fun index a ->
                [ "key", Text(string index)
                  "when", Text(a.When.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
                  "who", Text(actorText a.Who a.Provenance)
                  "what", Text(a.What.Replace('-', ' ') + (a.Provenance |> Option.bind _.Reason |> Option.map (fun r -> $": {r}") |> Option.defaultValue ""))
                  // The entity's version and what changed (INV-AUD-002).
                  "changed",
                  Text(
                      match a.Change with
                      | Some change ->
                          (change.Version |> Option.map (fun v -> $"version {v}") |> Option.toList) @ change.Changed |> String.concat "; "
                      | None -> ""
                  ) ]))
        |> Option.defaultValue []

    let detailValues =
        [ "hasInvoice", flag doc.IsSome
          "onDocumentTab", flag (tab = Routes.Document)
          "onPaymentsTab", flag (tab = Routes.InvoicePayments)
          "onHistoryTab", flag (tab = Routes.History)
          "invoiceTabs",
          Items(
              [ Routes.Document, "Invoice"; Routes.InvoicePayments, "Payments"; Routes.History, "History" ]
              |> List.map (fun (t, label) ->
                  [ "value", Text(Routes.invoiceTabText t)
                    "label", Text label
                    "selected", Text(if t = tab then "true" else "false") ])
          )
          "history", Items history
          "hasHistory", flag (not history.IsEmpty)
          "docNumber", docText _.Number
          "docIssuer", docText _.Issuer.LegalName
          "docIssuerAddress", docText _.Issuer.Address
          "docIssuerEmail", docText _.Issuer.Email
          "docCustomer", docText _.Customer.BillingName
          "docCustomerAddress", docText _.Customer.BillingAddress
          "docIssueDate", docText (fun d -> Documents.dateText d.IssueDate)
          "docDueDate", docText (fun d -> Documents.dateText d.DueDate)
          "docTerms", docText (fun d -> Documents.termsText d.Terms)
          "docPurchaseOrder", docText (fun d -> d.Details.PurchaseOrder |> Option.defaultValue "")
          "docHasPurchaseOrder", flag (doc |> Option.exists (fun d -> d.Details.PurchaseOrder.IsSome))
          "docSubtotal", docText (fun d -> Documents.amountText d.Subtotal)
          "docTotal", docText (fun d -> Documents.moneyText d.Total)
          // Taxes and other adjustments, as entered (INV-ADJ-005).
          "docAdjustments",
          Items(
              doc
              |> Option.map (fun d -> d.Adjustments |> List.mapi (fun i (label, amount) -> [ "key", Text(string i); "label", Text label; "amount", Text(Documents.amountText amount) ]))
              |> Option.defaultValue []
          )
          "docPayment", docText _.Issuer.PaymentInstructions
          "docNotes", docText (fun d -> d.Details.CustomerNotes |> Option.defaultValue "")
          "docHasNotes", flag (doc |> Option.exists (fun d -> d.Details.CustomerNotes.IsSome))
          "docLines",
          Items(
              doc
              |> Option.map (fun d ->
                  d.Lines
                  |> List.mapi (fun index l ->
                      [ "key", Text(string index)
                        "description", Text l.Description
                        "quantity", Text(Documents.quantityText l.QuantityThousandths)
                        "rate", Text(Documents.amountText l.UnitPrice)
                        "amount", Text(Documents.amountText l.Amount) ]))
              |> Option.defaultValue []
          )
          // The PDF printed through Folio and kept in this environment's store (INV-DOC-011).
          "pdfStatus",
          text (
              match detail, model.Configuration with
              | Some(b, i), Configured config ->
                  match b.Books.Artifacts.TryFind(Issuance.artifactId i.InvoiceId Sources.InvoicePdf), Artifacts.storedPdf config b i.InvoiceId with
                  | _, Ok(Some sha) -> $"Stored. SHA-256 {sha.Substring(0, 12)}…"
                  | _, Error problem -> Artifacts.describe problem
                  | Some a, _ ->
                      match a.Status with
                      | Sources.Failed why -> $"Not stored yet: the last attempt failed ({why})."
                      | _ -> "Not stored yet. Print the invoice and save it as PDF, then attach that file here."
                  | None, _ -> "This invoice has no PDF record."
              | _ -> ""
          )
          "canAttachPdf",
          flag (
              model.Packs.Files
              && model.Artifacts = StoreOpen
              && model.Pdf = PdfIdle
              && match detail, model.Configuration with
                 | Some(b, i), Configured config -> Artifacts.storedPdf config b i.InvoiceId = Ok None
                 | _ -> false
          )
          "isPdfWorking", flag (model.Pdf <> PdfIdle)
          "canDownloadPdf",
          flag (
              model.Artifacts = StoreOpen
              && model.Pdf = PdfIdle
              && match detail, model.Configuration with
                 | Some(b, i), Configured config ->
                     match Artifacts.storedPdf config b i.InvoiceId with
                     | Ok(Some _) -> true
                     | _ -> false
                 | _ -> false
          )
          "fuCollection", text (detail |> Option.map (fun (b, i) -> collectionText (Lifecycle.followUp b i.InvoiceId).Collection) |> Option.defaultValue "")
          "fuDispute", text (detail |> Option.map (fun (b, i) -> disputeText (Lifecycle.followUp b i.InvoiceId).Dispute) |> Option.defaultValue "")
          "fuDisputed",
          flag (detail |> Option.exists (fun (b, i) -> match (Lifecycle.followUp b i.InvoiceId).Dispute with Disputed _ -> true | _ -> false))
          "fuNotDisputed",
          flag (detail |> Option.exists (fun (b, i) -> match (Lifecycle.followUp b i.InvoiceId).Dispute with Disputed _ -> false | _ -> true))
          "fuNote", text model.FollowUpNote
          "detailEntryId", text (detail |> Option.map (fun (_, i) -> i.JournalEntryId) |> Option.defaultValue "")
          "detailEntryHref", text (detail |> Option.map (fun (_, i) -> Routes.href (Routes.JournalEntry i.JournalEntryId)) |> Option.defaultValue "")
          "detailCustomer", text (detail |> Option.map (fun (_, i) -> i.Customer.Name) |> Option.defaultValue "")
          "detailCustomerHref",
          text (detail |> Option.map (fun (_, i) -> Routes.href (Routes.Customer(i.CustomerId, Routes.CustomerInvoices))) |> Option.defaultValue "")
          "detailCredits",
          Items(
              detail
              |> Option.map (fun (b, i) ->
                  b.Applications
                  |> List.filter (fun a -> a.InvoiceId = i.InvoiceId)
                  |> List.map (fun a ->
                      let source, href =
                          match a.Source with
                          | FromCreditMemo id -> $"Credit memo {id}", Routes.href (Routes.CreditMemo id)
                          | FromCredit id -> $"Customer credit {id}", Routes.href (Routes.Customer(i.CustomerId, Routes.CustomerCredits))
                          | FromDeposit id -> $"Deposit {id}", Routes.href (Routes.Customer(i.CustomerId, Routes.CustomerCredits))

                      [ "key", Text a.Id; "source", Text source; "href", Text href; "date", Text(Documents.dateText a.Date); "amount", money a.Amount ]))
              |> Option.defaultValue []
          )
          "detailStatus",
          text (detail |> Option.map (fun (b, i) -> statusText (status b i)) |> Option.defaultValue "")
          "detailOutstanding", text (detail |> Option.map (fun (b, i) -> Documents.moneyText (outstanding b i)) |> Option.defaultValue "")
          "canRecordPayment", flag (detail |> Option.exists (fun (b, i) -> (outstanding b i).Minor > 0L))
          "detailPayments",
          Items(
              detail
              |> Option.map (fun (b, i) ->
                  liveAllocations b
                  |> List.filter (fun a -> a.InvoiceId = i.InvoiceId)
                  |> List.map (fun a ->
                      let p = b.Payments[a.PaymentId]

                      [ "key", Text a.Id
                        "date", Text(Documents.dateText p.DateReceived)
                        "reference", Text(if p.Reference = "" then "-" else p.Reference)
                        "amount", money a.Amount ]))
              |> Option.defaultValue []
          ) ]

    let draftCustomerTerms =
        books
        |> Option.bind (fun b -> b.Books.Customers.TryFind model.Draft.CustomerId)
        |> Option.map (fun c -> termsLabel c.DefaultTerms)
        |> Option.defaultValue ""

    // An exclusive tax adds to the total; one inside the prices does not.
    let draftTax =
        match parseAmount model.Draft.Tax.Amount with
        | Some tax when not model.Draft.Tax.Inclusive && model.Draft.Tax.Code.Trim() <> "" -> [ tax ]
        | _ -> []

    let draftTotal =
        model.Draft.Lines
        |> List.choose (fun l ->
            match parseHours l.Hours, parseAmount l.Rate with
            | Some h, Some r -> Some(extend h r)
            | _ -> None)
        |> fun lines -> lines @ draftTax
        |> sum "USD"

    let draftCustomerTax =
        books
        |> Option.bind (fun b -> b.Books.Customers.TryFind model.Draft.CustomerId)
        |> Option.map (fun c ->
            match c.Tax with
            | TaxNotAssessed -> "No tax status recorded"
            | SubjectToTax jurisdiction -> "Subject to tax" + (jurisdiction |> Option.map (fun j -> $" in {j}") |> Option.defaultValue "")
            | TaxExempt(evidence, _) -> $"Tax-exempt ({evidence})")
        |> Option.defaultValue ""

    // Whether the editor holds changes that are not saved: the form differs
    // from the draft as saved, or a new invoice has anything typed in it.
    let editorChanged =
        let content (form: DraftForm) =
            form.CustomerId, form.PurchaseOrder, form.Notes, form.Tax, form.Lines |> List.map (fun l -> l.Description, l.Hours, l.Rate, l.Taxable)

        match model.Place, books with
        | Routes.Draft draftId, Some b ->
            b.Books.Drafts.TryFind draftId
            |> Option.exists (fun d -> content (fst (formOf b.Books.Ledger 0 d)) <> content model.Draft)
        | Routes.NewInvoice customerId, _ ->
            let empty, _ = emptyDraft 0
            content { empty with CustomerId = defaultArg customerId "" } <> content model.Draft
        | _ -> false

    let reviewedDraft =
        match model.Draft.DraftId, books with
        | Some id, Some b ->
            b.Books.Drafts.TryFind id
            |> Option.exists (fun d -> d.Review = SubmittedForReview d.Version)
        | _ -> false

    let customerFilters =
        match model.Place with
        | Routes.Customers list -> list
        | _ -> Routes.allCustomers

    let customerRows =
        books
        |> Option.map (fun b ->
            b.Books.Customers
            |> Map.toList
            |> List.map snd
            |> List.filter (fun c -> (customerFilters.Inactive || c.Active) && containsText customerFilters.Search [ c.Name; c.BillingName; c.Email ])
            |> List.map (fun c ->
                c, b.Books.Invoices |> Map.toList |> List.map snd |> List.filter (fun i -> i.CustomerId = c.Id) |> List.map (outstanding b) |> sum "USD")
            |> fun rows ->
                match customerFilters.Sort with
                | Routes.ByName -> rows |> List.sortBy (fun (c, _) -> c.Name)
                | Routes.ByBalance -> rows |> List.sortByDescending (fun (c, balance) -> balance.Minor, c.Name))
        |> Option.defaultValue []

    // Receivables as they stood on a date (today unless the URL names one).
    let receivablesAsOf, receivablesCustomer =
        match model.Place with
        | Routes.Receivables(asOf, customerId) -> asOf, customerId
        | _ -> None, None

    let aging =
        let on = receivablesAsOf |> Option.defaultValue model.Today

        books
        |> Option.map (asOf on >> aging "USD" on)
        |> Option.defaultValue []
        |> List.filter (fun row -> receivablesCustomer |> Option.forall ((=) row.CustomerId))

    let configured =
        match model.Configuration with
        | Configured _ -> true
        | _ -> false

    let signingIn =
        match model.SignIn with
        | SignedOut _
        | LeavingForProvider
        | ProviderUnavailable -> true
        | _ -> false

    [ "isLoading", flag (configured && model.Storage = Loading && model.SignIn = NotRequired)
      "isRestoringSession", flag (configured && model.SignIn = Restoring)
      "needsSignIn", flag (configured && signingIn)
      "hasSignInNotice", flag (match model.SignIn with SignedOut(Some _) | ProviderUnavailable -> true | _ -> false)
      "signInNotice",
      text (
          match model.SignIn with
          | SignedOut(Some notice) -> notice
          | ProviderUnavailable -> "GitHub sign-in is not reachable right now. Try again in a moment."
          | _ -> ""
      )
      "isLeavingForProvider", flag (model.SignIn = LeavingForProvider)
      "keepInTab", flag model.KeepInTab
      "isSignedIn", flag (match model.SignIn with SignedInAs _ -> true | _ -> false)
      "signedInLogin", text (match model.SignIn with SignedInAs person -> person.Login | _ -> "")
      // Signed in, with books on GitHub: the store opens them (WI-0037).
      "awaitsGitHubStore", flag (configured && model.Storage = Loading && (match model.SignIn with SignedInAs _ -> true | _ -> false))
      "booksNotSetUp", flag (match model.Storage with NotSetUp _ -> true | _ -> false)
      "mayFound", flag (match model.Storage with NotSetUp(may, _) -> may | _ -> false)
      "notSetUpReason", text (match model.Storage with NotSetUp(_, Some why) -> why | _ -> "")
      "awaitingAdministrator", flag (match model.Storage with AwaitingAdministrator _ -> true | _ -> false)
      "canConfirm", flag (model.Storage = AwaitingAdministrator true)
      "booksOutdated", flag (match model.Storage with Outdated _ -> true | _ -> false)
      "mayMigrate", flag (model.Storage = Outdated true)
      "notAMember", flag (model.Storage = NotAMember)
      "storeUnreachable", flag (match model.Storage with Unreachable _ -> true | _ -> false)
      "storeProblem", text (match model.Storage with Unreachable why -> why | _ -> "")
      "isReadOnly", flag (not model.ReadOnly.IsEmpty)
      "readOnlyReasons", text (String.concat "; " model.ReadOnly)
      "canResetBooks", flag (not (onGitHub model))
      "canCheckBooks", flag (onGitHub model && model.Storage = Ready)
      "isCheckingBooks", flag (model.Check = Checking)
      "booksChecked", flag (match model.Check with Checked _ -> true | _ -> false)
      "booksCheckedClean", flag (model.Check = Checked [])
      "checkFindings",
      Items(
          match model.Check with
          | Checked findings -> findings |> List.mapi (fun i f -> [ "key", Text(string i); "finding", Text f ])
          | _ -> []
      )
      "hasUnsent", flag (model.Unsent.Waiting > 0)
      "unsentText",
      text (
          match model.Unsent.Waiting with
          | 1 -> "1 change has not reached GitHub yet."
          | n -> $"{n} changes have not reached GitHub yet."
      )
      "hasUnsentNote", flag model.Unsent.Note.IsSome
      "unsentNote", text (model.Unsent.Note |> Option.defaultValue "")
      "hasForeignUnsent", flag (model.Unsent.Foreign > 0)
      "foreignUnsentText",
      text (
          match model.Unsent.Foreign with
          | 1 -> "1 unsent change in this browser was made by another sign-in, or before Summa recorded who made it."
          | n -> $"{n} unsent changes in this browser were made by another sign-in, or before Summa recorded who made them."
      )
      "hasBlockedChange", flag model.Unsent.Blocked.IsSome
      "blockedSummary", text (model.Unsent.Blocked |> Option.map (fun (_, summary, _) -> summary) |> Option.defaultValue "")
      "blockedReason", text (model.Unsent.Blocked |> Option.map (fun (_, _, why) -> why) |> Option.defaultValue "")
      "isReady", flag (configured && model.Storage = Ready)
      "isUntrustworthy",
      flag (
          configured
          && match model.Storage with
             | Untrustworthy _ -> true
             | _ -> false
      )
      "storageProblems",
      Items(
          match model.Storage with
          | Untrustworthy problems -> problems |> List.mapi (fun i p -> [ "key", Text(string i); "problem", Text p ])
          | _ -> []
      )
      "organizationName", text (model.Manifest |> Option.map _.DisplayName |> Option.defaultValue "Summa")
      "isConfiguring", flag (match model.Configuration with Configuring -> true | _ -> false)
      "isMisconfigured", flag (match model.Configuration with Misconfigured _ -> true | _ -> false)
      "misconfiguration", text (match model.Configuration with Misconfigured why -> why | _ -> "")
      "hasBanner", flag (banner model).IsSome
      "environmentBanner", text (banner model |> Option.defaultValue "")
      "hasNotice", flag model.Notice.IsSome
      "notice", text (model.Notice |> Option.defaultValue "")
      "hasError", flag model.Error.IsSome
      "error", text (model.Error |> Option.defaultValue "")
      "hasUnsaved", flag (model.Unsaved > 0)
      "onDashboard", showing ((=) Routes.Home)
      "onCustomers", showing (function Routes.Customers _ -> true | _ -> false)
      "onInvoices", showing (function Routes.Invoices _ -> true | _ -> false)
      "onEditor", showing (function Routes.NewInvoice _ | Routes.Draft _ -> true | _ -> false)
      "onInvoice", showing (function Routes.Invoice _ -> true | _ -> false)
      "onReceivables", showing (function Routes.Receivables _ -> true | _ -> false)
      "onSettings", showing ((=) Routes.Settings)
      "isNotFound", flag (shown = Missing)
      "isNotPermitted", flag (shown = Forbidden)
      "isInvalidLink", flag (match shown with InvalidLink _ -> true | _ -> false)
      "invalidLink", text (match shown with InvalidLink why -> why | _ -> "")
      "isIssuedDraft", flag (match shown with IssuedDraft _ -> true | _ -> false)
      "issuedDraftId", text (match shown with IssuedDraft(draftId, _) -> draftId | _ -> "")
      "issuedDraftHref",
      text (
          match shown with
          | IssuedDraft(_, invoiceId) -> Routes.href (Routes.Invoice(invoiceId, Routes.Document))
          | _ -> ""
      )
      "hasCopyFallback", flag model.CopyFallback.IsSome
      "copyFallback", text (model.CopyFallback |> Option.defaultValue "")
      "navigation",
      Items(
          [ "home", "Home", Routes.Home
            "work", "Work", Routes.Work
            "invoices", "Invoices", Routes.Invoices Routes.allInvoices
            "proposals", "Proposals", Routes.Proposals
            "customers", "Customers", Routes.Customers Routes.allCustomers
            "engagements", "Engagements", Routes.Engagements None
            "payments", "Payments", Routes.Payments Routes.allPayments
            "credit-memos", "Credit memos", Routes.CreditMemos None
            "receivables", "Receivables", Routes.Receivables(None, None)
            "ledger", "Ledger", Routes.Ledger(None, None, None)
            "reports", "Reports", Routes.Reports
            "periods", "Periods", Routes.Periods None
            "settings", "Settings", Routes.Settings ]
          |> List.map (fun (id, label, place) ->
              let section =
                  match model.Place with
                  | Routes.Home -> "home"
                  | Routes.Work -> "work"
                  | Routes.FollowUp _ -> "receivables"
                  | Routes.Inbox _ -> "payments"
                  | Routes.Cpa _ -> "reports"
                  | Routes.Invoices _
                  | Routes.Invoice _
                  | Routes.NewInvoice _
                  | Routes.Draft _ -> "invoices"
                  | Routes.Proposals
                  | Routes.Proposal _ -> "proposals"
                  | Routes.Customers _
                  | Routes.Customer _ -> "customers"
                  | Routes.Engagements _
                  | Routes.Engagement _ -> "engagements"
                  | Routes.Payments _
                  | Routes.Payment _ -> "payments"
                  | Routes.CreditMemos _
                  | Routes.CreditMemo _ -> "credit-memos"
                  | Routes.Receivables _ -> "receivables"
                  | Routes.Ledger _
                  | Routes.JournalEntry _ -> "ledger"
                  | Routes.Reports
                  | Routes.TrialBalance _
                  | Routes.IncomeStatement _
                  | Routes.BalanceSheet _ -> "reports"
                  | Routes.Periods _
                  | Routes.Period _ -> "periods"
                  | Routes.Settings -> "settings"
                  | _ -> ""

              [ "id", Text id
                "label", Text label
                "href", Text(Routes.href place)
                "current", Text(if section = id && (match shown with Showing _ -> true | _ -> false) then "page" else "false") ])
      )
      // Dashboard
      "totalOutstanding", Value(money owed)
      "overdueCount", Value(Number(decimal overdue.Length))
      "overdueAmount", Value(money (overdue |> List.map outstandingOf |> sum "USD"))
      "draftCount", Value(Number(decimal drafts.Length))
      "invoiceCount", Value(Number(decimal invoices.Length))
      "hasOverdue", flag (not overdue.IsEmpty)
      "overdueInvoices",
      Items(
          overdue
          |> List.map (fun i ->
              [ "id", Text i.InvoiceId
                "href", Text(Routes.href (Routes.Invoice(i.InvoiceId, Routes.Document)))
                "number", Text i.Number
                "customer", Text i.Customer.Name
                "dueDate", Text(Documents.dateText i.DueDate)
                "outstanding", money (outstandingOf i) ])
      )
      "hasDrafts", flag (not drafts.IsEmpty)
      "drafts",
      Items(
          drafts
          |> List.map (fun d ->
              [ "id", Text d.DraftId
                "href", Text(Routes.href (Routes.Draft d.DraftId))
                "customer", Text(books |> Option.map (fun b -> customerName b d.CustomerId) |> Option.defaultValue d.CustomerId)
                "total", money (total d)
                "state",
                Text(
                    match d.Review with
                    | Editing -> "Draft"
                    | SubmittedForReview _ -> "Ready to issue"
                ) ])
      )
      // Customers
      "customerName", text model.Customer.Name
      "customerBillingName", text model.Customer.BillingName
      "customerAddress", text model.Customer.Address
      "customerEmail", text model.Customer.Email
      "customerTerms", text model.Customer.TermsDays
      "customerTaxStatus", text model.Customer.TaxStatus
      "customerTaxJurisdiction", text model.Customer.TaxJurisdiction
      "customerTaxEvidence", text model.Customer.TaxEvidence
      "customerTaxExempt", flag (model.Customer.TaxStatus = "exempt")
      "customerTaxOptions",
      Items(
          [ "not-assessed", "Not recorded"; "taxable", "Subject to tax"; "exempt", "Tax-exempt" ]
          |> List.map (fun (value, label) -> [ "value", Text value; "label", Text label; "selected", Flag(model.Customer.TaxStatus = value) ])
      )
      "hasCustomers", flag (books |> Option.exists (fun b -> not b.Books.Customers.IsEmpty))
      "hasListedCustomers", flag (not customerRows.IsEmpty)
      "noListedCustomers", flag (books |> Option.exists (fun b -> not b.Books.Customers.IsEmpty) && customerRows.IsEmpty)
      "customerSearch", text (customerFilters.Search |> Option.defaultValue "")
      "customerSort", text (Routes.customerSortText customerFilters.Sort)
      "customerInactive", flag customerFilters.Inactive
      "customers",
      Items(
          customerRows
          |> List.map (fun (c, balance) ->
              [ "id", Text c.Id
                "name", Text c.Name
                "email", Text c.Email
                "terms", Text(termsLabel c.DefaultTerms)
                "href", Text(Routes.href (Routes.Customer(c.Id, Routes.CustomerInvoices)))
                "invoicesHref", Text(Routes.href (Routes.Invoices { Routes.allInvoices with Customer = Some c.Id }))
                "balance", money balance ])
      )
      // Editor
      "draftCustomer", text model.Draft.CustomerId
      "draftCustomerTerms", text draftCustomerTerms
      "draftPurchaseOrder", text model.Draft.PurchaseOrder
      "draftNotes", text model.Draft.Notes
      "draftTotal", Value(money draftTotal)
      "draftLabel", text (model.Draft.DraftId |> Option.map (fun id -> $"Draft {id}") |> Option.defaultValue "New invoice")
      "draftLines",
      Items(
          model.Draft.Lines
          |> List.mapi (fun i l ->
              [ "key", Text l.Key
                "position", Text(string (i + 1))
                "description", Text l.Description
                "hours", Text l.Hours
                "rate", Text l.Rate
                "taxable", Flag l.Taxable ])
      )
      // The tax the person enters (INV-ADJ-005): never worked out by Summa.
      "draftTaxCode", text model.Draft.Tax.Code
      "draftTaxAmount", text model.Draft.Tax.Amount
      "draftTaxJurisdiction", text model.Draft.Tax.Jurisdiction
      "draftTaxRate", text model.Draft.Tax.Rate
      "draftTaxRateSource", text model.Draft.Tax.RateSource
      "draftTaxEvidence", text model.Draft.Tax.Evidence
      "draftTaxInclusive", flag model.Draft.Tax.Inclusive
      "draftTaxAccount", text model.Draft.Tax.AccountCode
      "taxAccountOptions",
      Items(
          books
          |> Option.map (fun b ->
              b.Books.Ledger.Accounts
              |> Map.toList
              |> List.map snd
              |> List.filter (fun a -> a.Type = Liability && a.Active)
              |> List.sortBy _.Code
              |> List.map (fun a -> [ "value", Text a.Code; "label", Text $"{a.Code} {a.Name}"; "selected", Flag(a.Code = model.Draft.Tax.AccountCode) ]))
          |> Option.defaultValue []
      )
      "draftCustomerTax", text draftCustomerTax
      "editorChanged", flag editorChanged
      "customerOptions", Items(customerChoices books true (nonEmpty model.Draft.CustomerId))
      "hasBlockers", flag (not model.Blockers.IsEmpty)
      "blockers",
      Items(
          model.Blockers
          |> List.mapi (fun i b ->
              [ "key", Text $"{i}"
                "explanation", Text b.Explanation
                "resolution", Text b.Resolution ])
      )
      // The document the reviewed draft would be issued as (INV-REV-001).
      yield!
          (let preview =
              match model.Manifest, books, model.Draft.DraftId with
              | Some manifest, Some b, Some draftId when reviewedDraft ->
                  Issuance.preview b (issueRequest manifest b draftId model.Today) |> Result.toOption |> Option.map fst
              | _ -> None

           let pv (f: Documents.InvoiceDocument -> string) = text (preview |> Option.map f |> Option.defaultValue "")

           [ "hasPreview", flag preview.IsSome
             "previewNumber", pv _.Number
             "previewIssuer", pv _.Issuer.LegalName
             "previewCustomer", pv _.Customer.BillingName
             "previewIssueDate", pv (fun d -> Documents.dateText d.IssueDate)
             "previewDueDate", pv (fun d -> Documents.dateText d.DueDate)
             "previewTerms", pv (fun d -> Documents.termsText d.Terms)
             "previewTotal", pv (fun d -> Documents.moneyText d.Total)
             "previewAdjustments",
             Items(
                 preview
                 |> Option.map (fun d -> d.Adjustments |> List.mapi (fun i (label, amount) -> [ "key", Text(string i); "label", Text label; "amount", Text(Documents.amountText amount) ]))
                 |> Option.defaultValue []
             )
             "previewPayment", pv _.Issuer.PaymentInstructions
             "previewLines",
             Items(
                 preview
                 |> Option.map (fun d ->
                     d.Lines
                     |> List.mapi (fun index l ->
                         [ "key", Text(string index)
                           "description", Text l.Description
                           "quantity", Text(Documents.quantityText l.QuantityThousandths)
                           "rate", Text(Documents.amountText l.UnitPrice)
                           "amount", Text(Documents.amountText l.Amount) ]))
                 |> Option.defaultValue []
             ) ])
      "canIssue", flag reviewedDraft
      "cannotIssue", flag (not reviewedDraft)
      // Invoices
      "hasInvoices", flag (not invoices.IsEmpty)
      "noListedInvoices", flag (not invoices.IsEmpty && listed.IsEmpty)
      "invoiceCountText", text $"{listed.Length} of {invoices.Length} invoices"
      "invoices", Items invoiceRows
      "invoiceSearch", text (filters.Search |> Option.defaultValue "")
      "invoiceCustomer", text (filters.Customer |> Option.defaultValue "")
      "invoiceFrom", text (dateInput filters.From)
      "invoiceTo", text (dateInput filters.To)
      "invoiceOverdue", flag filters.Overdue
      "invoiceSort", text (Routes.invoiceSortText filters.Sort)
      "isFiltered", flag (filters <> Routes.allInvoices)
      "statusOptions",
      Items(
          [ "unpaid", "Unpaid"; "partly-paid", "Partly paid"; "paid", "Paid"; "written-off", "Written off"; "voided", "Voided" ]
          |> List.map (fun (value, label) -> [ "value", Text value; "label", Text label; "checked", Flag(filters.Status.Contains value) ])
      )
      "invoiceCustomerOptions", Items(customerChoices books false filters.Customer)
      "receivablesCustomerOptions", Items(customerChoices books false receivablesCustomer)
      // Payment
      "paymentAmount", text model.Payment.Amount
      "paymentDate", text model.Payment.Date
      "paymentMethod", text model.Payment.Method
      "paymentReference", text model.Payment.Reference
      // Receivables
      "hasAging", flag (not aging.IsEmpty)
      "receivablesAsOf", text (dateInput receivablesAsOf)
      "receivablesCustomer", text (receivablesCustomer |> Option.defaultValue "")
      "receivablesDate", text (Documents.dateText (receivablesAsOf |> Option.defaultValue model.Today))
      "aging",
      Items(
          aging
          |> List.map (fun row ->
              [ "key", Text row.CustomerId
                "customer", Text(books |> Option.map (fun b -> customerName b row.CustomerId) |> Option.defaultValue row.CustomerId)
                "current", money row.Current
                "days1to30", money row.Days1To30
                "days31to60", money row.Days31To60
                "days61to90", money row.Days61To90
                "over90", money row.Over90
                "total", money row.Total ])
      )
      // Settings
      "companyLegalName", text model.Company.LegalName
      "companyAddress", text model.Company.Address
      "companyEmail", text model.Company.Email
      "companyPayment", text model.Company.PaymentInstructions ]
    @ detailValues
    @ placeValues model shown
    @ bookValues model shown
    @ attentionValues model shown
    @ proposalValues model shown
