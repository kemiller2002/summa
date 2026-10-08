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

type AppEffect =
    | LoadConfiguration
    | LoadBooks
    | SaveBooks of snapshot: string
    | PrintPage
    /// Push or replace the browser's location (Limen Core Navigation).
    | Navigate of Limen.Routing.NavigationEffect
    /// Write text to the clipboard (Limen Core Clipboard).
    | CopyText of string

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

// ---- Places (WI-0041) -------------------------------------------------------------------

/// Local books are their owner's own: every capability. Sign-in (WI-0035)
/// replaces this with the signed-in member's capabilities.
let viewer =
    Routes.Member(Summa.Access.Access.allCapabilities |> List.map Summa.Access.Access.capabilityName |> Set.ofList)

/// A fragment that is not a route ("#main", the skip link's target) is an
/// in-page anchor: the place stays, and the address is put back.
let private isAnchor (page: Limen.Routing.PageLocation) = page.Hash.Length > 1 && not (page.Hash.StartsWith "#/")

let private navigation (effect: Limen.Routing.NavigationEffect option) = effect |> Option.map Navigate |> Option.toList

/// Prepares what a place shows when the person arrives at it: the draft it
/// opens, the invoice a payment is for, an empty new invoice.
let private enter (place: Routes.Place) (model: Model) =
    match place with
    | Routes.Invoice(invoiceId, _) when model.Payment.InvoiceId <> invoiceId ->
        { model with Payment = { model.Payment with InvoiceId = invoiceId; Amount = ""; Reference = ""; Date = "" } }
    | Routes.Draft draftId when model.Draft.DraftId <> Some draftId ->
        match model.Books |> Option.bind (fun b -> b.Books.Drafts.TryFind draftId) with
        | Some draft ->
            let form, counter = formOf model.Counter draft
            { model with Draft = form; Counter = counter; Blockers = [] }
        | None -> model
    | Routes.NewInvoice customerId ->
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
        // These books need no sign-in: go straight to where the link pointed.
        | Ok(Routes.SignIn returnTo) ->
            let target = Routes.resume viewer returnTo
            let resumed, replace = Limen.Routing.Navigation.replace router target

            match Routes.parse viewer target with
            | Ok place -> arrive place resumed (navigation replace)
            | Error _ -> arrive Routes.Home resumed (navigation replace)
        | Ok place -> arrive place router (navigation effect)
        | Error problem -> { model with Router = router; Unrouted = Some problem; Notice = None; Error = None }, navigation effect

let private invoiceList (model: Model) =
    match model.Place with
    | Routes.Invoices list -> list
    | _ -> Routes.allInvoices

let private customerList (model: Model) =
    match model.Place with
    | Routes.Customers list -> list
    | _ -> Routes.allCustomers

let private nonEmpty (text: string) = if text.Trim() = "" then None else Some(text.Trim())

// ---- Update ----------------------------------------------------------------------------

let rec update (ctx: Ctx) (msg: Msg) (model: Model) : Model * AppEffect list =
    let model = { model with Today = today ctx }

    match msg with
    | Started page ->
        let started, effects = adopt page { model with Configuration = Configuring; Storage = Loading }
        started, LoadConfiguration :: effects
    | LocationChanged page -> adopt page model
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
            enter
                model.Place
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

/// What the main area shows for the place the URL names (SUM-LINK-007).
type Screen =
    | Showing of Routes.Place
    /// No such place, or no such record in these books.
    | Missing
    | Forbidden
    /// A parameter Summa does not understand, or a damaged address.
    | InvalidLink of explanation: string
    /// A place whose screen a later build adds (WI-0030).
    | Unbuilt of title: string
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
        | Routes.Customer _ -> Unbuilt "Customer"
        | Routes.Payments _
        | Routes.Payment _ -> Unbuilt "Payments"
        | Routes.CreditMemos _
        | Routes.CreditMemo _ -> Unbuilt "Credit memos"
        | Routes.Engagements _
        | Routes.Engagement _ -> Unbuilt "Engagements"
        | Routes.Periods _
        | Routes.Period _ -> Unbuilt "Accounting periods"
        | Routes.Ledger _
        | Routes.JournalEntry _ -> Unbuilt "General ledger"
        | Routes.Reports
        | Routes.TrialBalance _
        | Routes.IncomeStatement _
        | Routes.BalanceSheet _ -> Unbuilt "Reports"

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
                  "who", Text a.Who
                  "what", Text(a.What.Replace('-', ' ')) ]))
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
      "isUnbuilt", flag (match shown with Unbuilt _ -> true | _ -> false)
      "unbuiltTitle", text (match shown with Unbuilt title -> title | _ -> "")
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
            "invoices", "Invoices", Routes.Invoices Routes.allInvoices
            "customers", "Customers", Routes.Customers Routes.allCustomers
            "receivables", "Receivables", Routes.Receivables(None, None)
            "settings", "Settings", Routes.Settings ]
          |> List.map (fun (id, label, place) ->
              let section =
                  match model.Place with
                  | Routes.Home -> "home"
                  | Routes.Invoices _
                  | Routes.Invoice _
                  | Routes.NewInvoice _
                  | Routes.Draft _ -> "invoices"
                  | Routes.Customers _
                  | Routes.Customer _ -> "customers"
                  | Routes.Receivables _ -> "receivables"
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
      "customerFilterOptions",
      Items(
          books
          |> Option.map (fun b ->
              b.Books.Customers
              |> Map.toList
              |> List.map snd
              |> List.sortBy _.Name
              |> List.map (fun c -> [ "value", Text c.Id; "label", Text c.Name ]))
          |> Option.defaultValue []
      )
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
