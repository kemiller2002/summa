/// The accounting application's side of the Limen boundary (WI-0030).
///
/// Kernel messages in, engine messages out. The engine's effects become
/// Limen requests: Storage for the browser's copy of the books, and the
/// optional `summa.print` pack for the browser's print dialog. Every step
/// runs under the Aegis boundary. Mechanics only; the engine decides.
module Summa.Web.Application.AccountingWire

open System
open System.Text.Json
open System.Text.Json.Nodes
open Aegis
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
open Summa.Web.Application.Json
open Summa.Web.Application.Limen
open Summa.Web.Application.Boundary

/// `summa.print` v1: Summa's own pack (web-kernel/print.js), which opens the
/// browser's print dialog for the page's Folio document.
let print =
    { Id = "summa.print"
      Version = 1
      Fingerprint = "summa.print/1: print" }

/// `summa.host` v1: what Fides' sign-in client needs from the browser beyond
/// Limen's core effects (web-kernel/host.js): this tab's session storage,
/// leaving for the identity provider, tidying the address bar and telling
/// the origin's other tabs.
let host =
    { Id = "summa.host"
      Version = 1
      Fingerprint = "summa.host/1: tab storage, leave, replace address, broadcast" }

/// `limen.schedule` v1: timeouts, for the GitHub adapter's back-off waits.
let schedule =
    { Id = "limen.schedule"
      Version = 1
      Fingerprint = "sha256:627657798f0fa735ac6f955eb4f413e7c397f0aef6942b340a87683a30db54d0" }

/// `limen.coordination` v1: the Web Lock that makes one tab the holder of
/// this browser's unsent changes.
let coordination =
    { Id = "limen.coordination"
      Version = 1
      Fingerprint = "sha256:510dfbcd2f3f7966b842d518d209a511ada3ffb30132853f291e9d5fa8342684" }

/// Every event app/index.html may send, so a test can hold the page to it.
let events: Map<string, string -> string -> Msg> =
    Map.ofList
        [ "linkCopyRequested", (fun _ _ -> LinkCopyRequested)
          "invoiceSearchChanged", (fun _ value -> InvoiceSearchChanged value)
          "invoiceStatusToggled", (fun key _ -> InvoiceStatusToggled key)
          "invoiceCustomerChosen", (fun _ value -> InvoiceCustomerChosen value)
          "invoiceFromChanged", (fun _ value -> InvoiceFromChanged value)
          "invoiceToChanged", (fun _ value -> InvoiceToChanged value)
          "invoiceOverdueToggled", (fun _ _ -> InvoiceOverdueToggled)
          "invoiceSortChosen", (fun _ value -> InvoiceSortChosen value)
          "invoiceFiltersCleared", (fun _ _ -> InvoiceFiltersCleared)
          "customerSearchChanged", (fun _ value -> CustomerSearchChanged value)
          "customerSortChosen", (fun _ value -> CustomerSortChosen value)
          "customerInactiveToggled", (fun _ _ -> CustomerInactiveToggled)
          "receivablesAsOfChanged", (fun _ value -> ReceivablesAsOfChanged value)
          "receivablesCustomerChosen", (fun _ value -> ReceivablesCustomerChosen value)
          "invoiceTabChosen", (fun key _ -> InvoiceTabChosen key)
          "creditAmountChanged", (fun _ value -> CreditAmountChanged value)
          "creditReasonChanged", (fun _ value -> CreditReasonChanged value)
          "creditMemoIssued", (fun _ _ -> CreditMemoIssued)
          "paymentSearchChanged", (fun _ value -> PaymentSearchChanged value)
          "paymentCustomerChosen", (fun _ value -> PaymentCustomerChosen value)
          "paymentFromChanged", (fun _ value -> PaymentFromChanged value)
          "paymentToChanged", (fun _ value -> PaymentToChanged value)
          "paymentUnappliedToggled", (fun _ _ -> PaymentUnappliedToggled)
          "paymentSortChosen", (fun _ value -> PaymentSortChosen value)
          "paymentFiltersCleared", (fun _ _ -> PaymentFiltersCleared)
          "customerTabChosen", (fun key _ -> CustomerTabChosen key)
          "creditMemosCustomerChosen", (fun _ value -> CreditMemosCustomerChosen value)
          "engagementsCustomerChosen", (fun _ value -> EngagementsCustomerChosen value)
          "engagementCustomerChanged", (fun _ value -> EngagementCustomerChanged value)
          "engagementNameChanged", (fun _ value -> EngagementNameChanged value)
          "engagementFeeChanged", (fun _ value -> EngagementFeeChanged value)
          "engagementAdded", (fun _ _ -> EngagementAdded)
          "ledgerAccountChosen", (fun _ value -> LedgerAccountChosen value)
          "ledgerFromChanged", (fun _ value -> LedgerFromChanged value)
          "ledgerToChanged", (fun _ value -> LedgerToChanged value)
          "reportAsOfChanged", (fun _ value -> ReportAsOfChanged value)
          "incomeFromChanged", (fun _ value -> IncomeFromChanged value)
          "incomeToChanged", (fun _ value -> IncomeToChanged value)
          "incomeBasisChosen", (fun _ value -> IncomeBasisChosen value)
          "incomeRangeChosen", (fun key _ -> IncomeRangeChosen key)
          "periodClosed", (fun _ _ -> PeriodClosed)
          "periodLocked", (fun _ _ -> PeriodLocked)
          "periodReopened", (fun _ _ -> PeriodReopened)
          "pdfDownloadRequested", (fun _ _ -> PdfDownloadRequested)
          "followUpShowChosen", (fun key _ -> FollowUpShowChosen key)
          "reminderRecorded", (fun key _ -> ReminderRecorded key)
          "followUpNoteChanged", (fun _ value -> FollowUpNoteChanged value)
          "disputeMarked", (fun _ _ -> DisputeMarked)
          "disputeSettled", (fun _ _ -> DisputeSettled)
          "inboxCustomerChosen", (fun _ value -> InboxCustomerChosen value)
          "inboxApplied", (fun key _ -> InboxApplied key)
          "inboxCredited", (fun key _ -> InboxCredited key)
          "receiptCustomerChanged", (fun _ value -> ReceiptCustomerChanged value)
          "receiptAmountChanged", (fun _ value -> ReceiptAmountChanged value)
          "receiptDateChanged", (fun _ value -> ReceiptDateChanged value)
          "receiptMethodChanged", (fun _ value -> ReceiptMethodChanged value)
          "receiptReferenceChanged", (fun _ value -> ReceiptReferenceChanged value)
          "receiptRecorded", (fun _ _ -> ReceiptRecorded)
          "cpaTrialBalanceExported", (fun _ _ -> CpaExportRequested "trial-balance")
          "cpaJournalExported", (fun _ _ -> CpaExportRequested "journal")
          "customerNameChanged", (fun _ value -> CustomerNameChanged value)
          "customerBillingNameChanged", (fun _ value -> CustomerBillingNameChanged value)
          "customerAddressChanged", (fun _ value -> CustomerAddressChanged value)
          "customerEmailChanged", (fun _ value -> CustomerEmailChanged value)
          "customerTermsChanged", (fun _ value -> CustomerTermsChanged value)
          "customerAdded", (fun _ _ -> CustomerAdded)
          "draftCustomerChanged", (fun _ value -> DraftCustomerChanged value)
          "lineDescriptionChanged", (fun key value -> LineDescriptionChanged(key, value))
          "lineHoursChanged", (fun key value -> LineHoursChanged(key, value))
          "lineRateChanged", (fun key value -> LineRateChanged(key, value))
          "lineAdded", (fun _ _ -> LineAdded)
          "lineRemoved", (fun key _ -> LineRemoved key)
          "draftPurchaseOrderChanged", (fun _ value -> DraftPurchaseOrderChanged value)
          "draftNotesChanged", (fun _ value -> DraftNotesChanged value)
          "draftSubmitted", (fun _ _ -> DraftSubmitted)
          "draftIssued", (fun _ _ -> DraftIssued)
          "printRequested", (fun _ _ -> PrintRequested)
          "paymentAmountChanged", (fun _ value -> PaymentAmountChanged value)
          "paymentDateChanged", (fun _ value -> PaymentDateChanged value)
          "paymentMethodChanged", (fun _ value -> PaymentMethodChanged value)
          "paymentReferenceChanged", (fun _ value -> PaymentReferenceChanged value)
          "paymentRecorded", (fun _ _ -> PaymentRecorded)
          "companyLegalNameChanged", (fun _ value -> CompanyLegalNameChanged value)
          "companyAddressChanged", (fun _ value -> CompanyAddressChanged value)
          "companyEmailChanged", (fun _ value -> CompanyEmailChanged value)
          "companyPaymentChanged", (fun _ value -> CompanyPaymentChanged value)
          "companySaved", (fun _ _ -> CompanySaved)
          "resetConfirmed", (fun _ _ -> ResetConfirmed)
          "signInRequested", (fun _ _ -> SignInRequested)
          "signOutRequested", (fun _ _ -> SignOutRequested)
          "keepSignInToggled", (fun _ _ -> KeepSignInToggled)
          "foundRequested", (fun _ _ -> FoundRequested)
          "confirmRequested", (fun _ _ -> ConfirmRequested)
          "migrateRequested", (fun _ _ -> MigrateRequested)
          "sendUnsentRequested", (fun _ _ -> SendUnsentRequested)
          "checkRequested", (fun _ _ -> CheckRequested)
          "abandonUnsentRequested", (fun _ _ -> AbandonUnsentRequested) ]

let private message (name: string) (key: string option) (value: string option) =
    match events |> Map.tryFind name with
    | Some make -> make (defaultArg key "") (defaultArg value "")
    // app/index.html and the engine disagree: a defect, not an operational failure.
    | None -> invalidOp $"The accounting page sent an event the engine does not know: '{name}'"

// ---- Kernel messages ------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type StorageOutcome =
    | StorageValue of string option
    | StorageFailed of reason: string

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of handshake: JsonNode option * location: Limen.Routing.PageLocation
    | LocationChanged of Limen.Routing.PageLocation
    | ClipboardResult of correlation: string * copied: bool
    | Event of name: string * key: string option * value: string option
    | StorageResult of correlation: string * StorageOutcome
    /// A pack's outcome: Completed with its result, or why it was not executed.
    | CapabilityResult of correlation: string * capability: string * outcome: JsonNode
    | CapabilityFact of capability: string * fact: JsonNode
    | HttpResult of correlation: string * Bridge.HttpAnswer
    | Ignored

let private effectResult (node: JsonNode) =
    let path = "$.result"
    let correlation = required "correlationId" path asString node

    match required "kind" path asString node with
    | "StorageResult" ->
        let outcome = required "outcome" path asObject node

        match required "kind" $"{path}.outcome" asString outcome with
        | "Success" -> StorageResult(correlation, StorageValue(optional "value" $"{path}.outcome" asString outcome))
        | "Failure" -> StorageResult(correlation, StorageFailed(required "reason" $"{path}.outcome" asString outcome))
        | other -> raise (MalformedInput($"{path}.outcome.kind", $"a known Storage outcome, not '{other}'"))
    | "HttpResult" ->
        let outcome = required "outcome" path asObject node
        let at = $"{path}.outcome"

        let result =
            match required "kind" at asString outcome with
            | "Success" ->
                let body =
                    match tryField "body" outcome with
                    | Some(:? JsonValue as v) when v.GetValueKind() = JsonValueKind.String -> v.GetValue<string>()
                    | Some other -> other.ToJsonString()
                    | None -> ""

                let headers =
                    match tryField "headers" outcome with
                    | Some(:? JsonObject as found) ->
                        found
                        |> Seq.choose (fun pair -> pair.Value |> Option.ofObj |> Option.map (fun value -> pair.Key.ToLowerInvariant(), asString $"{at}.headers.{pair.Key}" value))
                        |> List.ofSeq
                    | _ -> []

                Bridge.Responded(required "status" at asInt outcome, headers, body)
            | "Failure" -> Bridge.Unreachable(required "reason" at asString outcome)
            | "OutcomeUnknown" -> Bridge.Unknown(required "reason" at asString outcome)
            | other -> Bridge.Unreachable other

        HttpResult(correlation, result)
    | "CapabilityResult" ->
        CapabilityResult(correlation, required "capability" path asString node, required "outcome" path asObject node)
    | "ClipboardResult" ->
        let outcome = required "outcome" path asObject node
        ClipboardResult(correlation, required "kind" $"{path}.outcome" asString outcome = "Success")
    // A push or replace either applied or, refused, leaves the address as it
    // was: the engine's own state already says where the person is.
    | "NavigationResult" -> Ignored
    | other -> raise (MalformedInput($"{path}.kind", $"a result for an effect the engine requested, not '{other}'"))

/// The page's address as the kernel reports it ({origin, path, query, hash}).
let private location (message: JsonNode) =
    let node = required "location" "$" asObject message
    let field name = required name "$.location" asString node

    ({ Origin = field "origin"
       Path = field "path"
       Query = field "query"
       Hash = field "hash" }
    : Limen.Routing.PageLocation)

let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" -> Initialize(tryField "handshake" message, location message)
    | "LocationChanged" -> LocationChanged(location message)
    | "Event" ->
        let event = required "event" "$" asObject message
        Event(required "name" "$.event" asString event, optional "key" "$.event" asString event, optional "value" "$.event" asString event)
    | "EffectResult" -> effectResult (required "result" "$" asObject message)
    | "CapabilityFact" -> CapabilityFact(required "capability" "$" asString message, required "fact" "$" asObject message)
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---- The handshake ---------------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Answer =
    | Accepted of minor: int * contract: JsonNode * printing: bool * packs: Packs * hosting: bool * scheduling: bool * coordinating: bool
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// Accepts Limen Core and selects `summa.print`, `limen.files` and
/// `limen.store` when the kernel offers them. Without print there is no
/// print button; without files and the store, no PDF can be attached.
let answer (offer: JsonNode) =
    let path = "$.handshake"
    let protocol = required "protocol" path asObject offer
    let major = required "major" $"{path}.protocol" asInt protocol
    let minor = required "minor" $"{path}.protocol" asInt protocol
    let contract = required "contract" path asObject offer
    let offered = required "capabilities" path asArray offer
    let offers contract = offered |> List.exists (fun c -> sameUnit contract $"{path}.capabilities[]" c "id")
    let printing = offers print

    if major <> 1 then
        Rejected(fun writer ->
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WritePropertyName "offered"
            writeNode writer protocol)
    elif not (sameUnit core $"{path}.contract" contract "unit") then
        Rejected(fun writer ->
            writer.WriteString("kind", "ContractMismatch")
            writer.WritePropertyName "offered"
            writeNode writer contract)
    else
        Accepted(min minor ProtocolMinor, contract, printing, { Files = offers files; Store = offers store }, offers host, offers schedule, offers coordination)

// ---- Session ---------------------------------------------------------------------------------------

type Purpose =
    | Configuring
    | Loading
    | Saving
    | Printing
    | Copying
    | OpeningStore
    | ReadingFile
    | StoringArtifact
    | FetchingArtifact
    | Releasing
    | Downloading
    /// Keeping or forgetting the return target in this tab (WI-0043).
    | KeepingReturn
    /// Reading the return target this tab kept across a sign-in.
    | ReadingReturn

[<NoComparison; NoEquality>]
type Session =
    { Model: Model
      Pending: Map<string, Purpose>
      Sequence: int
      Printing: bool
      /// The kernel offers `summa.host`, which sign-in needs.
      Hosting: bool
      /// The kernel offers `limen.schedule`, for the GitHub adapter's waits.
      Scheduling: bool
      /// The kernel offers `limen.coordination`, for the unsent changes' lock.
      Coordinating: bool
      Fault: FaultView option }

let initial =
    { Model = Summa.Web.Engine.Accounting.initial
      Pending = Map.empty
      Sequence = 0
      Printing = false
      Hosting = false
      Scheduling = false
      Coordinating = false
      Fault = None }

/// What the wire is given: the clock, the actor of books without sign-in,
/// and Fides' client over the page's bridge.
[<NoComparison; NoEquality>]
type Env =
    { Now: unit -> DateTimeOffset
      LocalActor: string
      Bridge: Bridge.Bridge
      Identity: Identity.IdentityPort
      Store: Store.StorePort }

[<NoComparison; NoEquality>]
type Request =
    | HttpGet of correlation: string * url: string
    | HttpPost of correlation: string * url: string * body: string * timeoutMs: int
    /// Any Http request, with the response headers to return (the GitHub adapter).
    | HttpCall of correlation: string * method: string * url: string * headers: (string * string) list * body: string option * timeoutMs: int * responseHeaders: string list
    /// A `limen.schedule` timeout.
    | Wake of correlation: string * delayMs: int
    /// A `limen.coordination` exclusive lock, without waiting.
    | Acquire of correlation: string * name: string
    | StorageRemove of correlation: string * key: string
    /// A `summa.host` request: its operation and string arguments.
    | Host of correlation: string * operation: string * arguments: (string * string) list
    | StorageGet of correlation: string * key: string
    | StorageSet of correlation: string * key: string * value: string
    | Print of correlation: string
    | Navigation of correlation: string * operation: string * url: string
    | Clipboard of correlation: string * text: string
    /// A request to an optional pack, written by `request`.
    | PackRequest of correlation: string * pack: Contract * request: (Utf8JsonWriter -> unit)

let private artifactsStore = "artifacts"

/// Where this tab keeps the location to return to after sign-in: session
/// storage, which survives the round trip to GitHub in this tab and nothing
/// longer. It holds a relative location, never a token (SUM-LINK-008).
[<Literal>]
let ReturnKey = "summa.returnTo"

let private requests (session: Session) (effects: AppEffect list) =
    effects
    |> List.fold
        (fun (s: Session, out) effect ->
            let correlation = $"app-{s.Sequence + 1}"
            let s = { s with Sequence = s.Sequence + 1 }

            match effect with
            | LoadConfiguration -> { s with Pending = s.Pending.Add(correlation, Configuring) }, out @ [ HttpGet(correlation, ConfigurationUrl) ]
            | LoadBooks -> { s with Pending = s.Pending.Add(correlation, Loading) }, out @ [ StorageGet(correlation, StorageKey) ]
            | SaveBooks snapshot -> { s with Pending = s.Pending.Add(correlation, Saving) }, out @ [ StorageSet(correlation, StorageKey, snapshot) ]
            | PrintPage when s.Printing -> { s with Pending = s.Pending.Add(correlation, Printing) }, out @ [ Print correlation ]
            | PrintPage -> s, out
            | Navigate effect ->
                let operation, location =
                    match effect with
                    | Limen.Routing.NavigationEffect.Push location -> "push", location
                    | Limen.Routing.NavigationEffect.Replace location -> "replace", location

                s, out @ [ Navigation(correlation, operation, Limen.Routing.Location.href Summa.Web.Engine.Routes.mode location) ]
            | CopyText text -> { s with Pending = s.Pending.Add(correlation, Copying) }, out @ [ Clipboard(correlation, text) ]
            | OpenArtifactStore database ->
                let request (w: Utf8JsonWriter) =
                    w.WriteString("operation", "open")
                    w.WriteString("database", database)
                    w.WriteNumber("version", 1)
                    w.WritePropertyName "stores"
                    w.WriteStartArray()
                    w.WriteStartObject()
                    w.WriteString("name", artifactsStore)
                    w.WriteString("keyPath", "sha256")
                    w.WritePropertyName "indexes"
                    w.WriteStartArray()
                    w.WriteEndArray()
                    w.WriteEndObject()
                    w.WriteEndArray()
                    w.WritePropertyName "dropStores"
                    w.WriteStartArray()
                    w.WriteEndArray()

                { s with Pending = s.Pending.Add(correlation, OpeningStore) }, out @ [ PackRequest(correlation, store, request) ]
            | ReadFileSlice(file, offset, length) ->
                let request (w: Utf8JsonWriter) =
                    w.WriteString("operation", "read")
                    w.WriteString("file", file)
                    w.WriteString("format", "base64")
                    w.WriteNumber("offset", offset)
                    w.WriteNumber("length", length)

                { s with Pending = s.Pending.Add(correlation, ReadingFile) }, out @ [ PackRequest(correlation, files, request) ]
            | ReleaseFile file ->
                let request (w: Utf8JsonWriter) =
                    w.WriteString("operation", "release")
                    w.WriteString("file", file)

                { s with Pending = s.Pending.Add(correlation, Releasing) }, out @ [ PackRequest(correlation, files, request) ]
            | PutArtifact(database, sha, data, size) ->
                let request (w: Utf8JsonWriter) =
                    w.WriteString("operation", "transact")
                    w.WriteString("database", database)
                    w.WriteString("mode", "readwrite")
                    w.WritePropertyName "operations"
                    w.WriteStartArray()
                    w.WriteStartObject()
                    w.WriteString("op", "put")
                    w.WriteString("store", artifactsStore)
                    w.WritePropertyName "value"
                    w.WriteStartObject()
                    w.WriteString("sha256", sha)
                    w.WriteString("mediaType", "application/pdf")
                    w.WriteNumber("size", size)
                    w.WriteString("data", data)
                    w.WriteEndObject()
                    w.WriteEndObject()
                    w.WriteEndArray()

                { s with Pending = s.Pending.Add(correlation, StoringArtifact) }, out @ [ PackRequest(correlation, store, request) ]
            | GetArtifact(database, sha) ->
                let request (w: Utf8JsonWriter) =
                    w.WriteString("operation", "transact")
                    w.WriteString("database", database)
                    w.WriteString("mode", "readonly")
                    w.WritePropertyName "operations"
                    w.WriteStartArray()
                    w.WriteStartObject()
                    w.WriteString("op", "get")
                    w.WriteString("store", artifactsStore)
                    w.WriteString("key", sha)
                    w.WriteEndObject()
                    w.WriteEndArray()

                { s with Pending = s.Pending.Add(correlation, FetchingArtifact) }, out @ [ PackRequest(correlation, store, request) ]
            | OfferDownload(fileName, mediaType, data) ->
                let request (w: Utf8JsonWriter) =
                    w.WriteString("operation", "download")
                    w.WriteString("fileName", fileName)
                    w.WriteString("mimeType", mediaType)
                    w.WriteString("format", "base64")
                    w.WriteString("data", data)

                { s with Pending = s.Pending.Add(correlation, Downloading) }, out @ [ PackRequest(correlation, files, request) ]
            | KeepReturnTarget _
            | ReadReturnTarget when not s.Hosting -> s, out
            | KeepReturnTarget(Some location) ->
                { s with Pending = s.Pending.Add(correlation, KeepingReturn) }, out @ [ Host(correlation, "tabSet", [ "key", ReturnKey; "value", location ]) ]
            | KeepReturnTarget None ->
                { s with Pending = s.Pending.Add(correlation, KeepingReturn) }, out @ [ Host(correlation, "tabRemove", [ "key", ReturnKey ]) ]
            | ReadReturnTarget -> { s with Pending = s.Pending.Add(correlation, ReadingReturn) }, out @ [ Host(correlation, "tabGet", [ "key", ReturnKey ]) ]
            // Fides' client and the store carry these out (`step`).
            | BeginIdentity _
            | StartSignIn _
            | EndSignIn
            | OpenStoredBooks
            | FoundStoredBooks
            | ConfirmAdministrator
            | MigrateStoredBooks
            | CommitBooks _
            | CommitManifest _
            | SendUnsent
            | AbandonUnsent _
            | CheckBooks -> s, out)
        (session, [])

/// A browser service the bridge's client asked for, as a Limen request, or
/// the answer to give at once when the kernel cannot carry it.
let private bridgeRequest (session: Session) (id: string) (call: Bridge.KernelCall) : Result<Request, Bridge.KernelAnswer> =
    let hosting, scheduling = session.Hosting, session.Scheduling

    let hosted operation arguments =
        if hosting then Ok(Host(id, operation, arguments)) else Error Bridge.Missing

    match call with
    | Bridge.Post(url, body, timeoutMs) -> Ok(HttpPost(id, url, body, timeoutMs))
    | Bridge.Request(method, url, headers, body, timeoutMs, responseHeaders) -> Ok(HttpCall(id, method, url, headers, body, timeoutMs, responseHeaders))
    // Without the schedule pack a back-off is not timed: it ends at once.
    | Bridge.Sleep milliseconds when scheduling -> Ok(Wake(id, milliseconds))
    | Bridge.Sleep _ -> Error Bridge.Done
    | Bridge.Lock name when session.Coordinating -> Ok(Acquire(id, name))
    | Bridge.Lock _ -> Error(Bridge.Locked "Unsupported")
    | Bridge.DeviceGet key -> Ok(StorageGet(id, key))
    | Bridge.DeviceSet(key, value) -> Ok(StorageSet(id, key, value))
    | Bridge.DeviceRemove key -> Ok(StorageRemove(id, key))
    | Bridge.TabGet key when not hosting -> Error(Bridge.Read None)
    | Bridge.TabGet key -> hosted "tabGet" [ "key", key ]
    | Bridge.TabSet(key, value) -> hosted "tabSet" [ "key", key; "value", value ]
    | Bridge.TabRemove key -> hosted "tabRemove" [ "key", key ]
    | Bridge.Leave url -> hosted "leave" [ "url", url ]
    | Bridge.ReplaceAddress url -> hosted "replaceAddress" [ "url", url ]
    | Bridge.Announce message -> hosted "broadcast" [ "message", message ]

let private writeRequest (writer: Utf8JsonWriter) =
    function
    | HttpGet(correlation, url) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Http")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("method", "GET")
        writer.WriteString("url", url)
        writer.WriteNumber("timeoutMs", RequestTimeoutMs)
        writer.WriteEndObject()
    | HttpPost(correlation, url, body, timeoutMs) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Http")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("method", "POST")
        writer.WriteString("url", url)
        writer.WritePropertyName "headers"
        writer.WriteStartObject()
        writer.WriteString("Content-Type", "application/json")
        writer.WriteEndObject()
        writer.WriteString("body", body)
        writer.WriteNumber("timeoutMs", timeoutMs)
        writer.WriteString("response", "text")
        writer.WriteString("credentials", "omit")
        writer.WriteEndObject()
    | HttpCall(correlation, method, url, headers, body, timeoutMs, responseHeaders) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Http")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("method", method)
        writer.WriteString("url", url)

        if not headers.IsEmpty then
            writer.WritePropertyName "headers"
            writer.WriteStartObject()
            headers |> List.iter (fun (name, value) -> writer.WriteString(name, value))
            writer.WriteEndObject()

        body |> Option.iter (fun body -> writer.WriteString("body", body))
        writer.WriteNumber("timeoutMs", timeoutMs)
        writer.WriteString("response", "text")
        writer.WriteString("credentials", "omit")

        if not responseHeaders.IsEmpty then
            writer.WritePropertyName "responseHeaders"
            writer.WriteStartArray()
            responseHeaders |> List.iter writer.WriteStringValue
            writer.WriteEndArray()

        writer.WriteEndObject()
    | Wake(correlation, delayMs) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Capability")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("capability", schedule.Id)
        writer.WriteNumber("version", schedule.Version)
        writer.WritePropertyName "request"
        writer.WriteStartObject()
        writer.WriteString("operation", "timeout")
        writer.WriteNumber("delayMs", delayMs)
        writer.WriteEndObject()
        writer.WriteEndObject()
    | Acquire(correlation, name) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Capability")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("capability", coordination.Id)
        writer.WriteNumber("version", coordination.Version)
        writer.WritePropertyName "request"
        writer.WriteStartObject()
        writer.WriteString("operation", "acquire")
        writer.WriteString("name", name)
        writer.WriteString("mode", "exclusive")
        writer.WriteBoolean("wait", false)
        writer.WriteBoolean("steal", false)
        writer.WriteEndObject()
        writer.WriteEndObject()
    | StorageRemove(correlation, key) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Storage")
        writer.WriteString("operation", "remove")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("key", key)
        writer.WriteEndObject()
    | Host(correlation, operation, arguments) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Capability")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("capability", host.Id)
        writer.WriteNumber("version", host.Version)
        writer.WritePropertyName "request"
        writer.WriteStartObject()
        writer.WriteString("operation", operation)
        arguments |> List.iter (fun (name, value) -> writer.WriteString(name, value))
        writer.WriteEndObject()
        writer.WriteEndObject()
    | StorageGet(correlation, key) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Storage")
        writer.WriteString("operation", "get")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("key", key)
        writer.WriteEndObject()
    | StorageSet(correlation, key, value) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Storage")
        writer.WriteString("operation", "set")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("key", key)
        writer.WriteString("value", value)
        writer.WriteEndObject()
    | Print correlation ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Capability")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("capability", print.Id)
        writer.WriteNumber("version", print.Version)
        writer.WritePropertyName "request"
        writer.WriteStartObject()
        writer.WriteString("action", "print")
        writer.WriteEndObject()
        writer.WriteEndObject()
    | Navigation(correlation, operation, url) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Navigation")
        writer.WriteString("operation", operation)
        writer.WriteString("correlationId", correlation)
        writer.WriteString("url", url)
        writer.WriteEndObject()
    | Clipboard(correlation, text) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Clipboard")
        writer.WriteString("operation", "writeText")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("text", text)
        writer.WriteEndObject()
    | PackRequest(correlation, pack, request) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Capability")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("capability", pack.Id)
        writer.WriteNumber("version", pack.Version)
        writer.WritePropertyName "request"
        writer.WriteStartObject()
        request writer
        writer.WriteEndObject()
        writer.WriteEndObject()

let render (session: Session) (out: Request list) (handshake: Answer option) =
    let view =
        Summa.Web.Engine.Accounting.view session.Model
        @ [ "canPrint", Value(Flag session.Printing) ]
        @ faultView session.Fault

    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()
        out |> List.iter (writeRequest writer)
        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | None -> ()
        | Some(Accepted(minor, contract, printing, packs, hosting, scheduling, coordinating)) ->
            writer.WritePropertyName "handshake"
            writer.WriteStartObject()
            writer.WriteString("kind", "Accepted")
            writer.WritePropertyName "protocol"
            writer.WriteStartObject()
            writer.WriteNumber("major", 1)
            writer.WriteNumber("minor", minor)
            writer.WriteEndObject()
            writer.WritePropertyName "contract"
            writeNode writer contract
            writer.WritePropertyName "capabilities"
            writer.WriteStartArray()
            if printing then writeOffer writer print
            if packs.Files then writeOffer writer files
            if packs.Store then writeOffer writer store
            if hosting then writeOffer writer host
            if scheduling then writeOffer writer schedule
            if coordinating then writeOffer writer coordination
            writer.WriteEndArray()
            writer.WriteEndObject()
        | Some(Rejected reason) ->
            writer.WritePropertyName "handshake"
            writer.WriteStartObject()
            writer.WriteString("kind", "Rejected")
            writer.WritePropertyName "reason"
            writer.WriteStartObject()
            reason writer
            writer.WriteEndObject()
            writer.WriteEndObject()

        writer.WriteEndObject())

/// Who acts: the signed-in person, or the one person of local books.
let private actorOf (env: Env) (model: Model) =
    match model.SignIn with
    | SignedInAs person -> person.ActorId
    | _ -> env.LocalActor

let private step (env: Env) (now: DateTimeOffset) (session: Session) (inbound: Inbound) =
    let rec run msg (s: Session) =
        let ctx = { Now = now; Actor = actorOf env s.Model }
        let model, effects = update ctx msg s.Model

        // Sign-in effects go to Fides' client and book effects to the store;
        // their browser calls come back through the bridge (`settle`).
        effects
        |> List.iter (function
            | BeginIdentity(config, query) -> env.Identity.Begin config query
            | StartSignIn kept -> env.Identity.SignIn kept
            | EndSignIn -> env.Identity.SignOut()
            | OpenStoredBooks ->
                match model.Configuration, model.SignIn with
                | Configured config, SignedInAs person -> env.Store.Open config person
                | _ -> ()
            | FoundStoredBooks -> env.Store.Found()
            | ConfirmAdministrator -> env.Store.Confirm()
            | MigrateStoredBooks -> env.Store.Migrate()
            // The store runs the message again on the books as they stand,
            // from the model it was made on.
            | CommitBooks command -> env.Store.Commit { Command = command; Transition = replay ctx command.Msg s.Model }
            | CommitManifest manifest -> env.Store.CommitManifest manifest
            | SendUnsent -> env.Store.SendUnsent()
            | AbandonUnsent sequence -> env.Store.Abandon sequence
            | CheckBooks -> env.Store.Check()
            | _ -> ())

        let s, out = requests { s with Model = model } effects

        // Without the host pack nothing was kept in this tab.
        if not s.Hosting && List.contains ReadReturnTarget effects then
            let s, more = run (ReturnTargetRead None) s
            s, out @ more
        else
            s, out

    /// Hands the kernel's answer to the operation waiting on it.
    let answerBridge (s: Session) correlation answer =
        env.Bridge.Answer correlation answer |> ignore
        s, [], None

    /// The bridge's browser calls become requests and the operations that
    /// finished become engine messages, until nothing more happens at once.
    let rec settle (s: Session, out: Request list) =
        match env.Bridge.Drain() with
        | Error failure -> raise failure
        | Ok([], []) -> s, out
        | Ok(calls, messages) ->
            let made, immediate =
                calls
                |> List.fold
                    (fun (made, immediate) (id, call) ->
                        match bridgeRequest s id call with
                        | Ok request -> made @ [ request ], immediate
                        | Error answer -> made, immediate @ [ (id, answer) ])
                    ([], [])

            immediate |> List.iter (fun (id, answer) -> env.Bridge.Answer id answer |> ignore)
            let s, more = messages |> List.fold (fun (s, acc) msg -> let s, next = run msg s in s, acc @ next) (s, [])
            settle (s, out @ made @ more)

    let next, out, handshake =
        match inbound with
        | Initialize(offer, page) ->
            let answered = offer |> Option.map answer

            let printing, packs, hosting, scheduling, coordinating =
                match answered with
                | Some(Accepted(_, _, p, packs, hosting, scheduling, coordinating)) -> p, packs, hosting, scheduling, coordinating
                | _ -> false, { Files = false; Store = false }, false, false, false

            let started, first =
                run (Started page) { session with Printing = printing; Hosting = hosting; Scheduling = scheduling; Coordinating = coordinating }
            let s, more = run (PacksNegotiated packs) started
            s, first @ more, answered
        | LocationChanged page ->
            let s, out = run (Msg.LocationChanged page) session
            s, out, None
        | ClipboardResult(correlation, copied) ->
            match session.Pending.TryFind correlation with
            | Some Copying ->
                let s, out = run (LinkCopied copied) { session with Pending = session.Pending.Remove correlation }
                s, out, None
            | _ when env.Bridge.Waits correlation -> answerBridge session correlation Bridge.Done
            | _ -> session, [], None
        | Event(name, key, value) ->
            let s, out = run (message name key value) session
            s, out, None
        | StorageResult(correlation, outcome) ->
            match session.Pending.TryFind correlation with
            | Some Loading ->
                let value =
                    match outcome with
                    | StorageValue v -> v
                    // Nothing could be read: start fresh only if storage works at all.
                    | StorageFailed reason -> raise (CapabilityFailed("Storage", reason))

                let s, out = run (Loaded value) { session with Pending = session.Pending.Remove correlation }
                s, out, None
            | Some Saving ->
                let ok =
                    match outcome with
                    | StorageValue _ -> true
                    | StorageFailed _ -> false

                let s, out = run (Saved ok) { session with Pending = session.Pending.Remove correlation }
                s, out, None
            | _ when env.Bridge.Waits correlation ->
                answerBridge
                    session
                    correlation
                    (match outcome with
                     | StorageValue value -> Bridge.Read value
                     | StorageFailed reason -> Bridge.Refused reason)
            | _ -> session, [], None
        | HttpResult(correlation, result) ->
            match session.Pending.TryFind correlation with
            | Some Configuring ->
                let read =
                    match result with
                    | Bridge.Responded(status, _, body) when status >= 200 && status < 300 -> Ok body
                    | Bridge.Responded(status, _, _) -> Error $"HTTP {status}"
                    | Bridge.Unreachable reason
                    | Bridge.Unknown reason -> Error reason

                let s, out = run (ConfigurationRead read) { session with Pending = session.Pending.Remove correlation }
                s, out, None
            | _ when env.Bridge.Waits correlation -> answerBridge session correlation (Bridge.Http result)
            | _ -> session, [], None
        | CapabilityResult(correlation, capability, outcome) when capability = host.Id && session.Pending.ContainsKey correlation ->
            let rest = { session with Pending = session.Pending.Remove correlation }
            let path = "$.result.outcome"

            match session.Pending[correlation] with
            | ReadingReturn ->
                let read =
                    match required "kind" path asString outcome with
                    | "Completed" -> optional "value" $"{path}.result" asString (required "result" path asObject outcome)
                    | _ -> None

                let s, out = run (ReturnTargetRead read) rest
                s, out, None
            | _ -> rest, [], None
        | CapabilityResult(correlation, capability, _) when capability = schedule.Id && env.Bridge.Waits correlation ->
            answerBridge session correlation Bridge.Done
        | CapabilityResult(correlation, capability, outcome) when capability = coordination.Id && env.Bridge.Waits correlation ->
            let path = "$.result.outcome"

            let kind =
                match required "kind" path asString outcome with
                | "Completed" -> required "kind" $"{path}.result" asString (required "result" path asObject outcome)
                | _ -> "Unsupported"

            answerBridge session correlation (Bridge.Locked kind)
        | CapabilityResult(correlation, capability, outcome) when capability = host.Id && env.Bridge.Waits correlation ->
            let path = "$.result.outcome"

            let answer =
                match required "kind" path asString outcome with
                | "Completed" ->
                    let result = required "result" path asObject outcome

                    match required "kind" $"{path}.result" asString result with
                    | "Value" -> Bridge.Read(optional "value" $"{path}.result" asString result)
                    | _ -> Bridge.Done
                | _ -> Bridge.Missing

            answerBridge session correlation answer
        | CapabilityResult(correlation, _, outcome) ->
            let rest = { session with Pending = session.Pending.Remove correlation }
            let path = "$.result.outcome"
            let completed = required "kind" path asString outcome = "Completed"
            let result () = required "result" path asObject outcome
            let kindOf (node: JsonNode) = required "kind" $"{path}.result" asString node

            let failure () =
                if completed then kindOf (result ()) else required "kind" path asString outcome + (optional "reason" path asString outcome |> Option.map (fun r -> $": {r}") |> Option.defaultValue "")

            let answer =
                match session.Pending.TryFind correlation with
                | Some OpeningStore -> Some(ArtifactStoreOpened(if completed && kindOf (result ()) = "Opened" then Ok() else Error(failure ())))
                | Some ReadingFile ->
                    Some(
                        PdfChunkRead(
                            if completed && kindOf (result ()) = "Read" then
                                let read = result ()
                                Ok(required "data" $"{path}.result" asString read, required "eof" $"{path}.result" asBool read)
                            else
                                Error(failure ())
                        )
                    )
                | Some StoringArtifact -> Some(PdfStored(if completed && kindOf (result ()) = "Committed" then Ok() else Error(failure ())))
                | Some FetchingArtifact ->
                    Some(
                        PdfFetched(
                            if completed && kindOf (result ()) = "Committed" then
                                match required "results" $"{path}.result" asArray (result ()) with
                                | first :: _ when required "kind" $"{path}.result.results[0]" asString first = "Found" ->
                                    let value = required "value" $"{path}.result.results[0]" asObject first
                                    Ok(Some(required "data" $"{path}.result.results[0].value" asString value))
                                | _ -> Ok None
                            else
                                Error(failure ())
                        )
                    )
                | _ -> None

            match answer with
            | Some msg ->
                let s, out = run msg rest
                s, out, None
            | None -> rest, [], None
        | CapabilityFact(capability, fact) when capability = files.Id ->
            let path = "$.fact"

            match required "kind" path asString fact with
            | "Selected" when required "name" $"{path}.input" asString (required "input" path asObject fact) = PdfInput ->
                let picked =
                    required "files" path asArray fact
                    |> List.tryHead
                    |> Option.map (fun file ->
                        let at = $"{path}.files[0]"

                        { Id = required "file" at asString file
                          Name = required "name" at asString file
                          Size = required "size" at asInt64 file
                          Type = required "type" at asString file })

                let s, out = run (PdfPicked picked) session
                s, out, None
            | _ -> session, [], None
        | CapabilityFact(capability, fact) when capability = host.Id ->
            match required "kind" "$.fact" asString fact with
            | "Broadcast" -> env.Identity.Receive(required "message" "$.fact" asString fact)
            | _ -> ()

            session, [], None
        | CapabilityFact _ -> session, [], None
        | Ignored -> session, [], None

    let next, out = settle (next, out)
    next, render next out handshake

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// model as it was and is shown until the next message.
let handle (aegis: AegisConfig) (env: Env) (session: Session) (messageJson: string) =
    let cleared = { session with Fault = None }

    match capture aegis "Summa.Web.Accounting.dispatch" (fun () -> step env (env.Now()) cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { session with Fault = Some fault }
        faulted, render faulted [] None
