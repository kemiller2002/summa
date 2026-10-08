/// Summa's deep links (WI-0041, SUM-LINK-001..012): every place a person can
/// navigate to, every list's filter, sort and search, and every report's
/// dates, as a location in the URL, so a copied link opens the same view.
///
/// The table and codec are Limen.Routing's (the EchelonFoundry.Limen.Routing
/// 0.9.0 package, DF-SUMMA-2026-0010). This module only says what Summa's places are:
/// the routes, their typed parameters and how they map onto `Place`.
///
/// Locations hold opaque ids, dates, months and declared names only. No
/// amount, balance, token or account number is ever a parameter
/// (SUM-LINK-004); `RoutesTests` holds the table to that.
///
/// Pure: no browser, clock or storage.
module Summa.Web.Engine.Routes

open System
open Limen.Routing

// ---- Places -------------------------------------------------------------------------------

type InvoiceTab =
    | Document
    | InvoicePayments
    | History

type CustomerTab =
    | CustomerInvoices
    | CustomerPayments
    | CustomerCredits

type InvoiceSort =
    | Newest
    | Oldest
    | DueFirst
    | ByNumber
    | ByCustomer

type CustomerSort =
    | ByName
    | ByBalance

type PaymentSort =
    | NewestPayment
    | OldestPayment

/// Which invoices the follow-up list shows (v0.4 §14-17).
type FollowUpView =
    | OverdueInvoices
    | DisputedInvoices
    | AllOpenInvoices

/// Accrual or cash basis for the income statement (SUM2-024).
type Basis =
    | Accrual
    | Cash

/// Invoices: search (number or customer name), settlement status, the
/// customer, an issue-date range, overdue only, and the order.
type InvoiceList =
    { Search: string option
      Status: Set<string>
      Customer: string option
      From: DateOnly option
      To: DateOnly option
      Overdue: bool
      Sort: InvoiceSort }

type CustomerList =
    { Search: string option
      Inactive: bool
      Sort: CustomerSort }

type PaymentList =
    { Search: string option
      Customer: string option
      From: DateOnly option
      To: DateOnly option
      Unapplied: bool
      Sort: PaymentSort }

/// One place in the application. Ids are the records' own opaque ids.
type Place =
    | Home
    | SignIn of returnTo: string option
    | NotFound
    | Customers of CustomerList
    | Customer of customerId: string * CustomerTab
    | Invoices of InvoiceList
    | NewInvoice of customerId: string option
    | Draft of draftId: string
    | Invoice of invoiceId: string * InvoiceTab
    | Payments of PaymentList
    | Payment of paymentId: string
    | CreditMemos of customerId: string option
    | CreditMemo of creditMemoId: string
    | Engagements of customerId: string option
    | Engagement of engagementId: string
    | Receivables of asOf: DateOnly option * customerId: string option
    | Periods of year: int option
    | Period of year: int * month: int
    | Ledger of accountId: string option * from: DateOnly option * until: DateOnly option
    | JournalEntry of entryId: string
    | Reports
    | TrialBalance of asOf: DateOnly option
    | IncomeStatement of from: DateOnly option * until: DateOnly option * Basis
    | BalanceSheet of asOf: DateOnly option
    | Settings
    /// Everything that needs attention, in one list (v0.4 §31).
    | Work
    /// Overdue and disputed invoices and their follow-up (v0.4 §14-17).
    | FollowUp of FollowUpView
    /// Payments not fully applied (v0.4 §13).
    | Inbox of customerId: string option
    /// The CPA workspace for a year (v0.4 §27-28).
    | Cpa of year: int option

let allInvoices =
    { Search = None
      Status = Set.empty
      Customer = None
      From = None
      To = None
      Overdue = false
      Sort = Newest }

let allCustomers = { Search = None; Inactive = false; Sort = ByName }

let allPayments =
    { Search = None
      Customer = None
      From = None
      To = None
      Unapplied = false
      Sort = NewestPayment }

// ---- Names of declared values ---------------------------------------------------------------

/// A union case and its text in the URL, both ways, and every text.
let private names (pairs: ('a * string) list) =
    (fun (value: 'a) -> pairs |> List.find (fst >> (=) value) |> snd),
    (fun (text: string) -> pairs |> List.tryFind (snd >> (=) text) |> Option.map fst),
    pairs |> List.map snd

let invoiceTabText, invoiceTabOf, invoiceTabs =
    names [ Document, "document"; InvoicePayments, "payments"; History, "history" ]

let customerTabText, customerTabOf, customerTabs =
    names [ CustomerInvoices, "invoices"; CustomerPayments, "payments"; CustomerCredits, "credits" ]

let invoiceSortText, invoiceSortOf, invoiceSorts =
    names [ Newest, "newest"; Oldest, "oldest"; DueFirst, "due"; ByNumber, "number"; ByCustomer, "customer" ]

let customerSortText, customerSortOf, customerSorts =
    names [ ByName, "name"; ByBalance, "balance" ]

let paymentSortText, paymentSortOf, paymentSorts =
    names [ NewestPayment, "newest"; OldestPayment, "oldest" ]

let basisText, basisOf, bases = names [ Accrual, "accrual"; Cash, "cash" ]

let followUpText, followUpOf, followUpViews =
    names [ OverdueInvoices, "overdue"; DisputedInvoices, "disputed"; AllOpenInvoices, "all" ]

/// The settlement states an invoice list can be filtered by (Payments.InvoiceStatus).
let invoiceStatuses = [ "unpaid"; "partly-paid"; "paid"; "written-off"; "voided" ]

// ---- The table ---------------------------------------------------------------------------------

/// The guard every place behind sign-in names (SUM-LINK-008).
[<Literal>]
let SignedIn = "signed-in"

let private viewFinancials = "ViewFinancials"

let private route name path (query: QueryParam list) (requires: string list) =
    { Route.create name path with
        Query = query
        Guard = Some SignedIn
        Requires = requires }

let private text name = QueryParam.optional name ParamType.String
let private date name = QueryParam.optional name ParamType.Date
let private flag name = QueryParam.optional name ParamType.Bool |> QueryParam.withDefault (Value.Boolean false)
let private choice name values fallback = QueryParam.optional name (ParamType.Enum values) |> QueryParam.withDefault (Value.Text fallback)

/// Every route, most specific first. Literal routes come before a parameter
/// in the same position ("/invoices/new" before "/invoices/{id}").
let routes: Route list =
    let financial name path query = route name path query [ viewFinancials ]

    [ financial "home" "" []
      { Route.create "sign-in" "sign-in" with
          Query = [ text ReturnTo.parameter ]
          ReturnTarget = false }
      { Route.create "not-found" "not-found" with ReturnTarget = false }
      financial "customers" "customers" [ text "q"; flag "inactive"; choice "sort" customerSorts "name" ]
      financial "customer" "customers/{id}" [ choice "tab" customerTabs "invoices" ]
      financial
          "invoices"
          "invoices"
          [ text "q"
            QueryParam.optional "status" (ParamType.Set invoiceStatuses)
            text "customer"
            date "from"
            date "to"
            flag "overdue"
            choice "sort" invoiceSorts "newest" ]
      route "invoice-new" "invoices/new" [ text "customer" ] [ viewFinancials; "CreateDraftInvoice" ]
      financial "invoice" "invoices/{id}" [ choice "tab" invoiceTabs "document" ]
      route "draft" "drafts/{id}" [] [ viewFinancials; "CreateDraftInvoice" ]
      financial
          "payments"
          "payments"
          [ text "q"; text "customer"; date "from"; date "to"; flag "unapplied"; choice "sort" paymentSorts "newest" ]
      financial "payment" "payments/{id}" []
      financial "credit-memos" "credit-memos" [ text "customer" ]
      financial "credit-memo" "credit-memos/{id}" []
      financial "engagements" "engagements" [ text "customer" ]
      financial "engagement" "engagements/{id}" []
      financial "receivables" "receivables" [ date "asOf"; text "customer" ]
      financial "periods" "periods" [ QueryParam.optional "year" ParamType.Int ]
      financial "period" "periods/{period:month}" []
      financial "ledger" "ledger" [ text "account"; date "from"; date "to" ]
      financial "journal-entry" "ledger/entries/{id}" []
      financial "reports" "reports" []
      financial "trial-balance" "reports/trial-balance" [ date "asOf" ]
      financial "income-statement" "reports/income-statement" [ date "from"; date "to"; choice "basis" bases "accrual" ]
      financial "balance-sheet" "reports/balance-sheet" [ date "asOf" ]
      route "settings" "settings" [] [ viewFinancials; "ManageSettings" ]
      financial "work" "work" []
      financial "follow-up" "follow-up" [ choice "show" followUpViews "overdue" ]
      route "inbox" "inbox" [ text "customer" ] [ viewFinancials; "AllocatePayment" ]
      route "cpa" "cpa" [ QueryParam.optional "year" ParamType.Int ] [ viewFinancials; "ExportData" ] ]

let roles =
    { Home = "home"
      SignIn = Some "sign-in"
      NotFound = Some "not-found" }

/// The table, or every problem in it. `RoutesTests` proves it is Ok.
let definition = RouteTable.define routes [] roles

let table =
    match definition with
    | Ok table -> table
    | Error problems -> invalidOp $"Summa's route table is invalid: %A{problems}"

/// Hash routes: a static host (GitHub Pages) serves one page, and the place
/// lives after "#" (SUM-LINK-005).
let mode = LocationMode.Hash

/// `.echelon/routes.json`: the table as `echelon.routes/v1` (SUM-LINK-011).
let inventory () = Inventory.render mode table

// ---- Place <-> target --------------------------------------------------------------------------

let private some name (value: Value option) = value |> Option.map (fun v -> name, v)
let private textValue = Option.map Value.Text
let private dateValue = Option.map Value.Date
let private query (pairs: (string * Value) option list) = pairs |> List.choose id |> Map.ofList
let private one name value = query [ Some(name, value) ]

let private target route parameters pairs =
    { Route = route
      Params = parameters
      Query = query pairs }

let toTarget (place: Place) : Target =
    let id value = Map [ "id", Value.Text value ]

    match place with
    | Home -> target "home" Map.empty []
    | SignIn returnTo -> target "sign-in" Map.empty [ some ReturnTo.parameter (textValue returnTo) ]
    | NotFound -> target "not-found" Map.empty []
    | Customers list ->
        target
            "customers"
            Map.empty
            [ some "q" (textValue list.Search)
              Some("inactive", Value.Boolean list.Inactive)
              Some("sort", Value.Text(customerSortText list.Sort)) ]
    | Customer(customerId, tab) -> { target "customer" (id customerId) [] with Query = one "tab" (Value.Text(customerTabText tab)) }
    | Invoices list ->
        target
            "invoices"
            Map.empty
            [ some "q" (textValue list.Search)
              Some("status", Value.Members(Set.toList list.Status))
              some "customer" (textValue list.Customer)
              some "from" (dateValue list.From)
              some "to" (dateValue list.To)
              Some("overdue", Value.Boolean list.Overdue)
              Some("sort", Value.Text(invoiceSortText list.Sort)) ]
    | NewInvoice customerId -> target "invoice-new" Map.empty [ some "customer" (textValue customerId) ]
    | Draft draftId -> target "draft" (id draftId) []
    | Invoice(invoiceId, tab) -> { target "invoice" (id invoiceId) [] with Query = one "tab" (Value.Text(invoiceTabText tab)) }
    | Payments list ->
        target
            "payments"
            Map.empty
            [ some "q" (textValue list.Search)
              some "customer" (textValue list.Customer)
              some "from" (dateValue list.From)
              some "to" (dateValue list.To)
              Some("unapplied", Value.Boolean list.Unapplied)
              Some("sort", Value.Text(paymentSortText list.Sort)) ]
    | Payment paymentId -> target "payment" (id paymentId) []
    | CreditMemos customerId -> target "credit-memos" Map.empty [ some "customer" (textValue customerId) ]
    | CreditMemo memoId -> target "credit-memo" (id memoId) []
    | Engagements customerId -> target "engagements" Map.empty [ some "customer" (textValue customerId) ]
    | Engagement engagementId -> target "engagement" (id engagementId) []
    | Receivables(asOf, customerId) -> target "receivables" Map.empty [ some "asOf" (dateValue asOf); some "customer" (textValue customerId) ]
    | Periods year -> target "periods" Map.empty [ some "year" (year |> Option.map (int64 >> Value.Integer)) ]
    | Period(year, month) -> target "period" (Map [ "period", Value.Month(year, month) ]) []
    | Ledger(accountId, from, until) ->
        target "ledger" Map.empty [ some "account" (textValue accountId); some "from" (dateValue from); some "to" (dateValue until) ]
    | JournalEntry entryId -> target "journal-entry" (id entryId) []
    | Reports -> target "reports" Map.empty []
    | TrialBalance asOf -> target "trial-balance" Map.empty [ some "asOf" (dateValue asOf) ]
    | IncomeStatement(from, until, basis) ->
        target "income-statement" Map.empty [ some "from" (dateValue from); some "to" (dateValue until); Some("basis", Value.Text(basisText basis)) ]
    | BalanceSheet asOf -> target "balance-sheet" Map.empty [ some "asOf" (dateValue asOf) ]
    | Settings -> target "settings" Map.empty []
    | Work -> target "work" Map.empty []
    | FollowUp view -> target "follow-up" Map.empty [ Some("show", Value.Text(followUpText view)) ]
    | Inbox customerId -> target "inbox" Map.empty [ some "customer" (textValue customerId) ]
    | Cpa year -> target "cpa" Map.empty [ some "year" (year |> Option.map (int64 >> Value.Integer)) ]

/// The typed values of a match. The table has already checked every type,
/// so a mismatch here is a defect in this module, reported as Unmapped.
let ofMatch (matched: Match) : Result<Place, string> =
    let parameters = matched.Chain |> List.collect (fun level -> Map.toList level.Params) |> Map.ofList
    let value name = matched.Query |> Map.tryFind name

    let textOf name =
        match value name with
        | Some(Value.Text text) -> Some text
        | _ -> None

    let dateOf name =
        match value name with
        | Some(Value.Date date) -> Some date
        | _ -> None

    let flagOf name =
        match value name with
        | Some(Value.Boolean flag) -> flag
        | _ -> false

    let named parse name =
        match textOf name |> Option.bind parse with
        | Some value -> Ok value
        | None -> Error $"'{name}' is not a declared value"

    let both make a b = a |> Result.bind (fun x -> b |> Result.map (make x))

    let id () =
        match parameters |> Map.tryFind "id" with
        | Some(Value.Text text) -> Ok text
        | _ -> Error "the id is missing"

    match matched.Route with
    | "home" -> Ok Home
    | "sign-in" -> Ok(SignIn(textOf ReturnTo.parameter))
    | "not-found" -> Ok NotFound
    | "customers" ->
        named customerSortOf "sort"
        |> Result.map (fun sort ->
            Customers
                { Search = textOf "q"
                  Inactive = flagOf "inactive"
                  Sort = sort })
    | "customer" -> both (fun customerId tab -> Customer(customerId, tab)) (id ()) (named customerTabOf "tab")
    | "invoices" ->
        named invoiceSortOf "sort"
        |> Result.map (fun sort ->
            Invoices
                { Search = textOf "q"
                  Status =
                    (match value "status" with
                     | Some(Value.Members members) -> Set.ofList members
                     | _ -> Set.empty)
                  Customer = textOf "customer"
                  From = dateOf "from"
                  To = dateOf "to"
                  Overdue = flagOf "overdue"
                  Sort = sort })
    | "invoice-new" -> Ok(NewInvoice(textOf "customer"))
    | "draft" -> id () |> Result.map Draft
    | "invoice" -> both (fun invoiceId tab -> Invoice(invoiceId, tab)) (id ()) (named invoiceTabOf "tab")
    | "payments" ->
        named paymentSortOf "sort"
        |> Result.map (fun sort ->
            Payments
                { Search = textOf "q"
                  Customer = textOf "customer"
                  From = dateOf "from"
                  To = dateOf "to"
                  Unapplied = flagOf "unapplied"
                  Sort = sort })
    | "payment" -> id () |> Result.map Payment
    | "credit-memos" -> Ok(CreditMemos(textOf "customer"))
    | "credit-memo" -> id () |> Result.map CreditMemo
    | "engagements" -> Ok(Engagements(textOf "customer"))
    | "engagement" -> id () |> Result.map Engagement
    | "receivables" -> Ok(Receivables(dateOf "asOf", textOf "customer"))
    | "periods" ->
        match value "year" with
        | Some(Value.Integer year) when year >= 1L && year <= 9999L -> Ok(Periods(Some(int year)))
        | Some _ -> Error "a year is between 1 and 9999"
        | None -> Ok(Periods None)
    | "period" ->
        match parameters |> Map.tryFind "period" with
        | Some(Value.Month(year, month)) -> Ok(Period(year, month))
        | _ -> Error "the period is missing"
    | "ledger" -> Ok(Ledger(textOf "account", dateOf "from", dateOf "to"))
    | "journal-entry" -> id () |> Result.map JournalEntry
    | "reports" -> Ok Reports
    | "trial-balance" -> Ok(TrialBalance(dateOf "asOf"))
    | "income-statement" -> named basisOf "basis" |> Result.map (fun basis -> IncomeStatement(dateOf "from", dateOf "to", basis))
    | "balance-sheet" -> Ok(BalanceSheet(dateOf "asOf"))
    | "settings" -> Ok Settings
    | "work" -> Ok Work
    | "follow-up" -> named followUpOf "show" |> Result.map FollowUp
    | "inbox" -> Ok(Inbox(textOf "customer"))
    | "cpa" ->
        match value "year" with
        | Some(Value.Integer year) when year >= 1L && year <= 9999L -> Ok(Cpa(Some(int year)))
        | Some _ -> Error "a year is between 1 and 9999"
        | None -> Ok(Cpa None)
    | other -> Error $"no place is named '{other}'"

let codec = RouteCodec.create table toTarget ofMatch

// ---- Who may open a place ----------------------------------------------------------------------

/// Who is looking. Local books (no data location) are one person's own
/// books in their own browser: everything is permitted. A signed-in
/// principal has the capabilities their membership grants (Summa.Access).
type Viewer =
    | Anonymous
    | Member of capabilities: Set<string>

/// Summa's guard (interface policy only; every command is authorized again
/// by Summa.Access). Anonymous goes to sign-in carrying the place it asked
/// for; a member without a place's capabilities is not permitted.
let guard (viewer: Viewer) : string -> Match -> GuardDecision =
    fun name matched ->
        match name, viewer with
        | SignedIn, Anonymous ->
            let location =
                Router.canonical (RouteTable.routes table) matched
                |> Result.toOption
                |> Option.bind (ReturnTo.capture table)

            GuardDecision.Redirect(
                "sign-in",
                Map.empty,
                location |> Option.map (fun l -> Map [ ReturnTo.parameter, Value.Text l ]) |> Option.defaultValue Map.empty
            )
        | SignedIn, Member capabilities when matched.Requires |> List.forall capabilities.Contains -> GuardDecision.Allow
        | SignedIn, Member _ -> GuardDecision.Deny
        | _ -> GuardDecision.Allow

// ---- Locations and links ------------------------------------------------------------------------

/// The canonical routed location of a place ("/invoices/INV-0001").
let format (place: Place) = RouteCodec.format codec place

/// The relative link to render as an href ("#/invoices/INV-0001"). A place
/// is built from the books' own ids, so it always formats; the fallback is
/// home, never a broken link.
let href (place: Place) =
    format place |> Result.defaultValue "/" |> Location.href mode

/// A reported location as a place, or why it is not one.
let parse (viewer: Viewer) (location: string) = RouteCodec.parse codec (guard viewer) location

/// The location in the browser's URL, as the kernel reports it.
let locationOf (page: PageLocation) = Location.ofBrowser mode page

/// The absolute URL for "Copy link" (SUM-LINK-009).
let share (page: PageLocation) (place: Place) =
    format place |> Result.map (Link.share mode page)

/// Where to go after sign-in: the captured place if it is still allowed,
/// otherwise home (SUM-LINK-008).
let resume (viewer: Viewer) (returnTo: string option) = ReturnTo.resume table (guard viewer) returnTo
