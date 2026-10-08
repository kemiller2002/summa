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

type Route =
    | Dashboard
    | Customers
    | Invoices
    | Editor
    | InvoiceDetail of invoiceId: string
    | Receivables
    | Settings

let routeName =
    function
    | Dashboard -> "dashboard"
    | Customers -> "customers"
    | Invoices -> "invoices"
    | Editor -> "editor"
    | InvoiceDetail _ -> "invoice"
    | Receivables -> "receivables"
    | Settings -> "settings"

type CustomerForm =
    { Name: string
      BillingName: string
      Address: string
      Email: string
      TermsDays: string }

let emptyCustomer =
    { Name = ""
      BillingName = ""
      Address = ""
      Email = ""
      TermsDays = "" }

type LineForm =
    { Key: string
      Description: string
      Hours: string
      Rate: string }

type DraftForm =
    { DraftId: string option
      CustomerId: string
      Lines: LineForm list
      PurchaseOrder: string
      Notes: string }

type PaymentForm =
    { InvoiceId: string
      Amount: string
      Date: string
      Method: string
      Reference: string }

type CompanyForm =
    { LegalName: string
      Address: string
      Email: string
      PaymentInstructions: string }

/// Where the books stand in this browser.
type Storage =
    /// Asked the browser for the stored snapshot.
    | Loading
    | Ready
    /// The stored snapshot failed its checks; nothing runs on it.
    | Untrustworthy of problems: string list

[<NoComparison; NoEquality>]
type Model =
    { Configuration: Configuration
      Storage: Storage
      Manifest: Organization.OrganizationManifest option
      Books: Receivables option
      Route: Route
      Today: DateOnly
      Customer: CustomerForm
      Draft: DraftForm
      Payment: PaymentForm
      Company: CompanyForm
      Blockers: Issuance.Blocker list
      Notice: string option
      Error: string option
      /// Draws keys for form rows, so a sequence of inputs always gives the same keys.
      Counter: int
      /// Saves the browser has not confirmed.
      Unsaved: int }

let private newLine (counter: int) =
    let key, next = nextKey "line" counter
    { Key = key; Description = ""; Hours = "1"; Rate = "" }, next

let private emptyDraft counter =
    let line, next = newLine counter

    { DraftId = None
      CustomerId = ""
      Lines = [ line ]
      PurchaseOrder = ""
      Notes = "" },
    next

let initial =
    let draft, counter = emptyDraft 0

    { Configuration = Configuring
      Storage = Loading
      Manifest = None
      Books = None
      Route = Dashboard
      Today = DateOnly(2000, 1, 1)
      Customer = emptyCustomer
      Draft = draft
      Payment = { InvoiceId = ""; Amount = ""; Date = ""; Method = "ach"; Reference = "" }
      Company = { LegalName = ""; Address = ""; Email = ""; PaymentInstructions = "" }
      Blockers = []
      Notice = None
      Error = None
      Counter = counter
      Unsaved = 0 }

/// Who acts and when, for every command.
type Ctx = { Now: DateTimeOffset; Actor: string }

type Msg =
    | Started
    /// The configuration document's text, or why it could not be read.
    | ConfigurationRead of Result<string, string>
    /// The stored snapshot, or None when this browser has none yet.
    | Loaded of string option
    | Saved of ok: bool
    | Navigate of string
    | CustomerNameChanged of string
    | CustomerBillingNameChanged of string
    | CustomerAddressChanged of string
    | CustomerEmailChanged of string
    | CustomerTermsChanged of string
    | CustomerAdded
    | NewInvoice
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
    | DraftOpened of draftId: string
    | InvoiceOpened of invoiceId: string
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

type AppEffect =
    | LoadConfiguration
    | LoadBooks
    | SaveBooks of snapshot: string
    | PrintPage

// ---- Helpers ---------------------------------------------------------------------------

let private context (ctx: Ctx) =
    { Who = ctx.Actor
      When = ctx.Now
      Source = "summa-app"
      CorrelationId = None }

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

/// The draft the editor describes, or what is wrong with it.
let private draftOf (manifest: Organization.OrganizationManifest) (books: Receivables) (form: DraftForm) =
    let revenue = Organization.accountByCode books.Books.Ledger manifest.Accounting.RevenueAccount

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
                      Rate = None }
            | _ -> Error $"'{l.Description}' needs a description, a quantity and a rate such as 150.00")

    match lines |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
    | Some why -> Error why
    | None when form.CustomerId = "" -> Error "Choose a customer."
    | None ->
        let draftId = form.DraftId |> Option.defaultWith (fun () -> nextId "D" (fun id -> books.Books.Drafts.ContainsKey id || books.Books.IssuedFrom.ContainsKey id))
        let stored = books.Books.Drafts.TryFind draftId

        Ok
            { DraftId = draftId
              CustomerId = form.CustomerId
              Currency = "USD"
              Lines = lines |> List.choose Result.toOption
              Adjustments = []
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

let private formOf (counter: int) (draft: DraftInvoice) =
    let lines, next =
        draft.Lines
        |> List.mapFold
            (fun c l ->
                let key, n = nextKey "line" c

                { Key = key
                  Description = l.Description
                  Hours = Documents.quantityText l.QuantityThousandths
                  Rate = Documents.amountText l.UnitPrice |> fun t -> t.Replace(",", "") },
                n)
            counter

    { DraftId = Some draft.DraftId
      CustomerId = draft.CustomerId
      Lines = lines
      PurchaseOrder = draft.Details.PurchaseOrder |> Option.defaultValue ""
      Notes = draft.Details.CustomerNotes |> Option.defaultValue "" },
    next

let private editLine (key: string) (change: LineForm -> LineForm) (model: Model) =
    { model with Draft = { model.Draft with Lines = model.Draft.Lines |> List.map (fun l -> if l.Key = key then change l else l) } }, []

let private routeOf (name: string) (model: Model) =
    match name with
    | "customers" -> Customers
    | "invoices" -> Invoices
    | "editor" -> Editor
    | "receivables" -> Receivables
    | "settings" -> Settings
    | "invoice" ->
        match model.Route with
        | InvoiceDetail id -> InvoiceDetail id
        | _ -> Invoices
    | _ -> Dashboard

// ---- Update ----------------------------------------------------------------------------

let rec update (ctx: Ctx) (msg: Msg) (model: Model) : Model * AppEffect list =
    let model = { model with Today = today ctx }

    match msg with
    | Started -> { model with Configuration = Configuring; Storage = Loading }, [ LoadConfiguration ]
    | ConfigurationRead(Error why) ->
        { model with Configuration = Misconfigured $"The deployment's configuration could not be read ({why})." }, []
    | ConfigurationRead(Ok document) ->
        match Deployment.parse document with
        | Error problem -> { model with Configuration = Misconfigured(Diagnostics.describe problem) }, []
        | Ok config when config.Location.IsSome ->
            // Books on GitHub need sign-in and the GitHub store (WI-0035, WI-0037).
            { model with
                Configuration =
                    Misconfigured "This deployment names a data location on GitHub, which this build cannot open yet: it keeps books only in the browser." },
            []
        | Ok config -> { model with Configuration = Configured config }, [ LoadBooks ]
    | Loaded None ->
        let company: Organization.CompanyInformation =
            { LegalName = "Demo Consulting LLC"
              Address = "1 Example Street\nSpringfield"
              TaxId = None
              Email = "billing@demo.example" }

        match LocalSnapshot.start (context ctx) "Demo Consulting" company "ACH to account ending 0000 (demo)" with
        | Ok(manifest, books) ->
            save
                { model with
                    Storage = Ready
                    Manifest = Some manifest
                    Books = Some books
                    Company = companyForm manifest
                    Notice = Some "A new set of books was started in this browser." }
        | Error problems -> { model with Storage = Untrustworthy [ describe problems ] }, []
    | Loaded(Some text) ->
        match LocalSnapshot.decode text with
        | Ok restored when restored.Problems.IsEmpty ->
            { model with
                Storage = Ready
                Manifest = Some restored.Manifest
                Books = Some restored.Books
                Company = companyForm restored.Manifest },
            []
        | Ok restored -> { model with Storage = Untrustworthy(restored.Problems |> List.map Diagnostics.describe) }, []
        | Error why -> { model with Storage = Untrustworthy [ why ] }, []
    | Saved true -> { model with Unsaved = max 0 (model.Unsaved - 1) }, []
    | Saved false ->
        { model with
            Unsaved = max 0 (model.Unsaved - 1)
            Error = Some "This browser could not save the books. The last change is shown but may be lost on reload." },
        []
    | Navigate name -> { model with Route = routeOf name model; Notice = None; Error = None; Blockers = [] }, []
    | CustomerNameChanged v -> { model with Customer = { model.Customer with Name = v } }, []
    | CustomerBillingNameChanged v -> { model with Customer = { model.Customer with BillingName = v } }, []
    | CustomerAddressChanged v -> { model with Customer = { model.Customer with Address = v } }, []
    | CustomerEmailChanged v -> { model with Customer = { model.Customer with Email = v } }, []
    | CustomerTermsChanged v -> { model with Customer = { model.Customer with TermsDays = v } }, []
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

        match terms with
        | Error why -> { model with Error = Some why }, []
        | Ok _ when form.Name.Trim() = "" -> { model with Error = Some "A customer needs a name." }, []
        | Ok terms ->
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
                          Active = true }

                    Ok { books with Books = saveCustomer (context ctx) customer books.Books })

            (if added.Error.IsNone then { added with Customer = emptyCustomer } else added), effects
    | NewInvoice ->
        let draft, counter = emptyDraft model.Counter
        { model with Route = Editor; Draft = draft; Counter = counter; Blockers = []; Notice = None; Error = None }, []
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
        | _ -> model, []
    | DraftIssued ->
        match model.Manifest, model.Books, model.Draft.DraftId with
        | Some manifest, Some books, Some draftId ->
            let request = issueRequest manifest books draftId (today ctx)

            match Issuance.issueInvoice (context ctx) request books with
            | Ok(issued, invoice) ->
                let draft, counter = emptyDraft model.Counter

                save
                    { model with
                        Books = Some issued
                        Route = InvoiceDetail invoice.InvoiceId
                        Draft = draft
                        Counter = counter
                        Blockers = []
                        Notice = Some $"Invoice {invoice.Number} issued."
                        Error = None }
            | Error blockers -> { model with Blockers = blockers; Error = Some "The invoice cannot be issued yet." }, []
        | _ -> { model with Error = Some "Save and review the draft first." }, []
    | DraftOpened draftId ->
        match model.Books |> Option.bind (fun b -> b.Books.Drafts.TryFind draftId) with
        | Some draft ->
            let form, counter = formOf model.Counter draft
            { model with Route = Editor; Draft = form; Counter = counter; Blockers = []; Notice = None; Error = None }, []
        | None -> { model with Error = Some $"Draft {draftId} is not here any more." }, []
    | InvoiceOpened invoiceId ->
        { model with
            Route = InvoiceDetail invoiceId
            Payment = { model.Payment with InvoiceId = invoiceId; Amount = ""; Reference = ""; Date = "" }
            Notice = None
            Error = None },
        []
    | PrintRequested -> model, [ PrintPage ]
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
                        match model.Route with
                        | InvoiceDetail id -> id
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
    | ResetConfirmed ->
        // Only offered when the stored books fail their checks.
        match model.Storage with
        | Untrustworthy _ -> update ctx (Loaded None) { model with Storage = Loading }
        | _ -> model, []

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

/// The environment banner (SUM0-040): every non-production environment
/// shows it, and a deployment that keeps books in the browser says so.
let banner (model: Model) =
    match model.Configuration with
    | Configured config ->
        Environments.banner config
        |> Option.map (fun b -> if config.Location.IsNone then b + " · books are kept only in this browser" else b)
    | _ -> None

/// The named values app/index.html binds to.
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

    let current = model.Route
    let on route = flag (routeName current = route)

    let invoiceRows =
        invoices
        |> List.map (fun i ->
            let st = books |> Option.map (fun b -> status b i) |> Option.defaultValue Issued

            [ "id", Text i.InvoiceId
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
        match current, books with
        | InvoiceDetail id, Some b -> b.Books.Invoices.TryFind id |> Option.map (fun i -> b, i)
        | _ -> None

    let doc = detail |> Option.map (fun (_, i) -> Documents.ofInvoice i)

    let docText (f: Documents.InvoiceDocument -> string) = text (doc |> Option.map f |> Option.defaultValue "")

    let detailValues =
        [ "hasInvoice", flag doc.IsSome
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

    let draftTotal =
        model.Draft.Lines
        |> List.choose (fun l ->
            match parseHours l.Hours, parseAmount l.Rate with
            | Some h, Some r -> Some(extend h r)
            | _ -> None)
        |> sum "USD"

    let reviewedDraft =
        match model.Draft.DraftId, books with
        | Some id, Some b ->
            b.Books.Drafts.TryFind id
            |> Option.exists (fun d -> d.Review = SubmittedForReview d.Version)
        | _ -> false

    let aging =
        books |> Option.map (aging "USD" model.Today) |> Option.defaultValue []

    let configured =
        match model.Configuration with
        | Configured _ -> true
        | _ -> false

    [ "isLoading", flag (configured && model.Storage = Loading)
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
      "onDashboard", on "dashboard"
      "onCustomers", on "customers"
      "onInvoices", on "invoices"
      "onEditor", on "editor"
      "onInvoice", on "invoice"
      "onReceivables", on "receivables"
      "onSettings", on "settings"
      "navigation",
      Items(
          [ "dashboard", "Home"; "invoices", "Invoices"; "customers", "Customers"; "receivables", "Receivables"; "settings", "Settings" ]
          |> List.map (fun (id, label) ->
              let selected =
                  routeName current = id || (id = "invoices" && routeName current = "invoice")

              [ "id", Text id; "label", Text label; "current", Text(if selected then "page" else "false") ])
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
      "hasCustomers", flag (books |> Option.exists (fun b -> not b.Books.Customers.IsEmpty))
      "customers",
      Items(
          books
          |> Option.map (fun b ->
              b.Books.Customers
              |> Map.toList
              |> List.map snd
              |> List.sortBy _.Name
              |> List.map (fun c ->
                  let open' =
                      b.Books.Invoices |> Map.toList |> List.map snd |> List.filter (fun i -> i.CustomerId = c.Id) |> List.map (outstanding b) |> sum "USD"

                  [ "id", Text c.Id
                    "name", Text c.Name
                    "email", Text c.Email
                    "terms", Text(termsLabel c.DefaultTerms)
                    "balance", money open' ]))
          |> Option.defaultValue []
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
                "rate", Text l.Rate ])
      )
      "customerOptions",
      Items(
          books
          |> Option.map (fun b ->
              b.Books.Customers
              |> Map.toList
              |> List.map snd
              |> List.filter _.Active
              |> List.sortBy _.Name
              |> List.map (fun c -> [ "value", Text c.Id; "label", Text c.Name ]))
          |> Option.defaultValue []
      )
      "hasBlockers", flag (not model.Blockers.IsEmpty)
      "blockers",
      Items(
          model.Blockers
          |> List.mapi (fun i b ->
              [ "key", Text $"{i}"
                "explanation", Text b.Explanation
                "resolution", Text b.Resolution ])
      )
      "canIssue", flag reviewedDraft
      "cannotIssue", flag (not reviewedDraft)
      // Invoices
      "hasInvoices", flag (not invoices.IsEmpty)
      "invoices", Items invoiceRows
      // Payment
      "paymentAmount", text model.Payment.Amount
      "paymentDate", text model.Payment.Date
      "paymentMethod", text model.Payment.Method
      "paymentReference", text model.Payment.Reference
      // Receivables
      "hasAging", flag (not aging.IsEmpty)
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
