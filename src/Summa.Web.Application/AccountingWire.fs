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
          "resetConfirmed", (fun _ _ -> ResetConfirmed) ]

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
    | CapabilityResult of correlation: string * capability: string * completed: bool
    | HttpResult of correlation: string * Result<string, string>
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
                let status = required "status" at asInt outcome

                match tryField "body" outcome with
                | Some body when status >= 200 && status < 300 ->
                    match body with
                    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String -> Ok(v.GetValue<string>())
                    | other -> Ok(other.ToJsonString())
                | _ -> Error $"HTTP {status}"
            | "Failure" -> Error(required "reason" at asString outcome)
            | other -> Error other

        HttpResult(correlation, result)
    | "CapabilityResult" ->
        let outcome = required "outcome" path asObject node
        CapabilityResult(correlation, required "capability" path asString node, required "kind" $"{path}.outcome" asString outcome = "Completed")
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
    | "CapabilityFact" -> Ignored
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---- The handshake ---------------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Answer =
    | Accepted of minor: int * contract: JsonNode * printing: bool
    | Rejected of reason: (Utf8JsonWriter -> unit)

/// Accepts Limen Core and selects `summa.print` when the kernel offers it;
/// without it the page simply offers no print button.
let answer (offer: JsonNode) =
    let path = "$.handshake"
    let protocol = required "protocol" path asObject offer
    let major = required "major" $"{path}.protocol" asInt protocol
    let minor = required "minor" $"{path}.protocol" asInt protocol
    let contract = required "contract" path asObject offer
    let offered = required "capabilities" path asArray offer
    let printing = offered |> List.exists (fun c -> sameUnit print $"{path}.capabilities[]" c "id")

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
        Accepted(min minor ProtocolMinor, contract, printing)

// ---- Session ---------------------------------------------------------------------------------------

type Purpose =
    | Configuring
    | Loading
    | Saving
    | Printing
    | Copying

[<NoComparison; NoEquality>]
type Session =
    { Model: Model
      Pending: Map<string, Purpose>
      Sequence: int
      Printing: bool
      Fault: FaultView option }

let initial =
    { Model = Summa.Web.Engine.Accounting.initial
      Pending = Map.empty
      Sequence = 0
      Printing = false
      Fault = None }

[<NoComparison; NoEquality>]
type Request =
    | HttpGet of correlation: string * url: string
    | StorageGet of correlation: string * key: string
    | StorageSet of correlation: string * key: string * value: string
    | Print of correlation: string
    | Navigation of correlation: string * operation: string * url: string
    | Clipboard of correlation: string * text: string

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
            | CopyText text -> { s with Pending = s.Pending.Add(correlation, Copying) }, out @ [ Clipboard(correlation, text) ])
        (session, [])

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
        | Some(Accepted(minor, contract, printing)) ->
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

let private step (ctx: Ctx) (session: Session) (inbound: Inbound) =
    let run msg (s: Session) =
        let model, effects = update ctx msg s.Model
        requests { s with Model = model } effects

    let next, out, handshake =
        match inbound with
        | Initialize(offer, page) ->
            let answered = offer |> Option.map answer

            let printing =
                match answered with
                | Some(Accepted(_, _, p)) -> p
                | _ -> false

            let s, out = run (Started page) { session with Printing = printing }
            s, out, answered
        | LocationChanged page ->
            let s, out = run (Msg.LocationChanged page) session
            s, out, None
        | ClipboardResult(correlation, copied) ->
            match session.Pending.TryFind correlation with
            | Some Copying ->
                let s, out = run (LinkCopied copied) { session with Pending = session.Pending.Remove correlation }
                s, out, None
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
            | _ -> session, [], None
        | HttpResult(correlation, result) ->
            match session.Pending.TryFind correlation with
            | Some Configuring ->
                let s, out = run (ConfigurationRead result) { session with Pending = session.Pending.Remove correlation }
                s, out, None
            | _ -> session, [], None
        | CapabilityResult(correlation, _, _) -> { session with Pending = session.Pending.Remove correlation }, [], None
        | Ignored -> session, [], None

    next, render next out handshake

/// Handles one kernel message under the Aegis boundary. A fault leaves the
/// model as it was and is shown until the next message.
let handle (aegis: AegisConfig) (now: unit -> DateTimeOffset) (actor: string) (session: Session) (messageJson: string) =
    let cleared = { session with Fault = None }
    let ctx = { Now = now (); Actor = actor }

    match capture aegis "Summa.Web.Accounting.dispatch" (fun () -> step ctx cleared (decode messageJson)) with
    | Ok result -> result
    | Result.Error fault ->
        let faulted = { session with Fault = Some fault }
        faulted, render faulted [] None
