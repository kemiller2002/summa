/// Limen's browser/engine protocol (version 1), as the application tier reads
/// and writes it: the kernel's messages in, the engine's view, effects and
/// handshake answer out. Mechanics only; no Summa decision is made here.
///
/// See the `protocol` export of `@echelon-foundry/limen` (0.7.1) and its
/// `capabilities/files` and `capabilities/transfer` packs.
module Summa.Web.Application.Limen

open System.Text.Json
open System.Text.Json.Nodes
open Summa.Web.Engine.Common
open Summa.Web.Application.Json

/// A generated contract unit the engine was written against. The kernel
/// offers its own identities in the handshake; the engine accepts only these.
type Contract =
    { Id: string
      Version: int
      Fingerprint: string }

/// Limen Core's contract identity (`limen.core` v1).
let core =
    { Id = "limen.core"
      Version = 1
      Fingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c" }

/// User-mediated files (`limen.files` v1): the picked file's opaque id.
let files =
    { Id = "limen.files"
      Version = 1
      Fingerprint = "sha256:2cf28b1016994b11955c412282ec6038a6c8c205a056b14c6039ef6fd5bfabdf" }

/// The HTTP transfer profile (`limen.transfer` v1): multipart uploads by file id.
let transfer =
    { Id = "limen.transfer"
      Version = 1
      Fingerprint = "sha256:86dc0ced666c6dc6190a2afba74539d45672beef7f50f0060aee579bc31ee609" }

/// The newest protocol revision this engine speaks (1.4).
[<Literal>]
let ProtocolMinor = 4

/// How long the kernel waits for the local server before reporting an
/// unknown outcome. Uploads can be large (the server accepts 25 MB).
[<Literal>]
let RequestTimeoutMs = 30000

[<Literal>]
let UploadTimeoutMs = 300000

[<NoComparison; NoEquality>]
type HttpOutcome =
    /// A response arrived and its body was read: any status, including 4xx/5xx.
    | Responded of status: int * body: JsonNode option
    /// Nothing usable came back. `status` is present when a response arrived.
    | NotDelivered of reason: string * status: int option
    | Cancelled
    /// The request was sent and nobody knows whether the server applied it.
    | OutcomeUnknown of reason: string

[<NoComparison; NoEquality>]
type CapabilityOutcome =
    | Completed of JsonNode
    /// Unsupported or Rejected: the request was not executed at all.
    | NotExecuted of string

[<NoComparison; NoEquality>]
type Inbound =
    | Initialize of handshake: JsonNode option
    | Event of name: string * key: string option * value: string option
    | HttpResult of Correlation * HttpOutcome
    | CapabilityResult of Correlation * capability: string * CapabilityOutcome
    | CapabilityFact of capability: string * fact: JsonNode
    | LocationChanged

let private httpOutcome (path: string) (node: JsonNode) =
    let kind = required "kind" path asString node

    match kind with
    | "Success" -> Responded(required "status" path asInt node, tryField "body" node)
    | "Failure" -> NotDelivered(required "reason" path asString node, optional "status" path asInt node)
    | "Cancelled" -> Cancelled
    | "OutcomeUnknown" -> OutcomeUnknown(optional "reason" path asString node |> Option.defaultValue "unknown")
    | other -> raise (MalformedInput($"{path}.kind", $"a known Http outcome, not '{other}'"))

let private effectResult (node: JsonNode) =
    let path = "$.result"
    let kind = required "kind" path asString node
    let correlation = required "correlationId" path asString node
    let outcome = required "outcome" path (fun _ value -> value) node

    match kind with
    | "HttpResult" -> HttpResult(correlation, httpOutcome $"{path}.outcome" outcome)
    | "CapabilityResult" ->
        let capability = required "capability" path asString node

        let result =
            match required "kind" $"{path}.outcome" asString outcome with
            | "Completed" -> Completed(required "result" $"{path}.outcome" (fun _ value -> value) outcome)
            | other ->
                NotExecuted(
                    other
                    + (optional "reason" $"{path}.outcome" asString outcome
                       |> Option.map (fun reason -> $" ({reason})")
                       |> Option.defaultValue "")
                )

        CapabilityResult(correlation, capability, result)
    // The engine never requests Storage, Clipboard or Navigation effects, so a
    // result for one cannot answer anything it asked.
    | other -> raise (MalformedInput($"{path}.kind", $"a result for an effect the engine requested, not '{other}'"))

/// Reads one message from the kernel.
let decode (messageJson: string) =
    let message = parse messageJson |> asObject "$"

    match required "kind" "$" asString message with
    | "Initialize" -> Initialize(tryField "handshake" message)
    | "Event" ->
        let event = required "event" "$" asObject message

        Event(
            required "name" "$.event" asString event,
            optional "key" "$.event" asString event,
            optional "value" "$.event" asString event
        )
    | "EffectResult" -> effectResult (required "result" "$" asObject message)
    | "CapabilityFact" ->
        CapabilityFact(required "capability" "$" asString message, required "fact" "$" asObject message)
    | "LocationChanged" -> LocationChanged
    | other -> raise (MalformedInput("$.kind", $"a known message kind, not '{other}'"))

// ---------------------------------------------------------------------------
// The handshake answer.
// ---------------------------------------------------------------------------

[<NoComparison; NoEquality>]
type Handshake =
    | Accepted of minor: int * contract: JsonNode
    | Rejected of reason: (Utf8JsonWriter -> unit)

let private sameUnit (contract: Contract) (path: string) (node: JsonNode) idField =
    required idField path asString node = contract.Id
    && required "version" path asInt node = contract.Version
    && required "fingerprint" path asString node = contract.Fingerprint

let private writeOffer (writer: Utf8JsonWriter) (contract: Contract) =
    writer.WriteStartObject()
    writer.WriteString("id", contract.Id)
    writer.WriteNumber("version", contract.Version)
    writer.WriteString("fingerprint", contract.Fingerprint)
    writer.WriteEndObject()

/// The engine's answer to the kernel's handshake offer: accept the Core
/// contract and select the files and transfer packs, exactly as offered, or
/// say precisely why not.
let answer (offer: JsonNode) =
    let path = "$.handshake"
    let protocol = required "protocol" path asObject offer
    let major = required "major" $"{path}.protocol" asInt protocol
    let minor = required "minor" $"{path}.protocol" asInt protocol
    let contract = required "contract" path asObject offer
    let offered = required "capabilities" path asArray offer

    let missing =
        [ files; transfer ]
        |> List.tryFind (fun wanted ->
            not (offered |> List.exists (fun capability -> sameUnit wanted $"{path}.capabilities[]" capability "id")))

    if major <> 1 then
        Rejected(fun writer ->
            writer.WriteString("kind", "ProtocolUnsupported")
            writer.WritePropertyName "offered"
            writeNode writer protocol)
    elif not (sameUnit core $"{path}.contract" contract "unit") then
        Rejected(fun writer ->
            writer.WriteString("kind", "ContractMismatch")
            writer.WritePropertyName "expected"
            writer.WriteStartObject()
            writer.WriteString("unit", core.Id)
            writer.WriteNumber("version", core.Version)
            writer.WriteString("fingerprint", core.Fingerprint)
            writer.WriteEndObject()
            writer.WritePropertyName "offered"
            writeNode writer contract)
    else
        match missing with
        | Some capability ->
            Rejected(fun writer ->
                writer.WriteString("kind", "CapabilityUnavailable")
                writer.WriteString("id", capability.Id)
                writer.WriteNumber("version", capability.Version))
        | None -> Accepted(min minor ProtocolMinor, contract)

// ---------------------------------------------------------------------------
// The engine's reply: view, effects, and (to Initialize only) the handshake.
// ---------------------------------------------------------------------------

let private methodName =
    function
    | Get -> "GET"
    | Post -> "POST"
    | Delete -> "DELETE"

let private writeScalar (writer: Utf8JsonWriter) =
    function
    | Text text -> writer.WriteStringValue text
    | Flag flag -> writer.WriteBooleanValue flag
    | Number number -> writer.WriteNumberValue number

let private writeView (writer: Utf8JsonWriter) (view: View) =
    writer.WriteStartObject()

    for name, value in view do
        writer.WritePropertyName name

        match value with
        | Value scalar -> writeScalar writer scalar
        | Items items ->
            writer.WriteStartArray()

            for item in items do
                writer.WriteStartObject()

                for field, scalar in item do
                    writer.WritePropertyName field
                    writeScalar writer scalar

                writer.WriteEndObject()

            writer.WriteEndArray()

    writer.WriteEndObject()

let private writeCapabilityEffect (writer: Utf8JsonWriter) (correlation: string) (contract: Contract) (request: Utf8JsonWriter -> unit) =
    writer.WriteStartObject()
    writer.WriteString("kind", "Capability")
    writer.WriteString("correlationId", correlation)
    writer.WriteString("capability", contract.Id)
    writer.WriteNumber("version", contract.Version)
    writer.WritePropertyName "request"
    writer.WriteStartObject()
    request writer
    writer.WriteEndObject()
    writer.WriteEndObject()

let private writeEffect (writer: Utf8JsonWriter) (effect: Effect) =
    match effect with
    | Send(correlation, request) ->
        writer.WriteStartObject()
        writer.WriteString("kind", "Http")
        writer.WriteString("correlationId", correlation)
        writer.WriteString("method", methodName request.Method)
        writer.WriteString("url", request.Path)

        match request.Json with
        | Some body ->
            writer.WritePropertyName "headers"
            writer.WriteStartObject()
            writer.WriteString("content-type", "application/json")
            writer.WriteEndObject()
            writer.WriteString("body", render body)
        | None -> ()

        writer.WriteNumber("timeoutMs", RequestTimeoutMs)
        writer.WriteEndObject()
    | Upload(correlation, path, parts) ->
        writeCapabilityEffect writer correlation transfer (fun writer ->
            writer.WriteString("operation", "send")
            writer.WriteString("method", "POST")
            writer.WriteString("url", path)
            writer.WritePropertyName "body"
            writer.WriteStartObject()
            writer.WriteString("kind", "multipart")
            writer.WritePropertyName "parts"
            writer.WriteStartArray()

            for part in parts do
                writer.WriteStartObject()

                match part with
                | Field(name, value) ->
                    writer.WriteString("kind", "field")
                    writer.WriteString("name", name)
                    writer.WriteString("value", value)
                | FilePart(name, file, fileName) ->
                    writer.WriteString("kind", "file")
                    writer.WriteString("name", name)
                    writer.WriteString("file", file)
                    writer.WriteString("fileName", fileName)

                writer.WriteEndObject()

            writer.WriteEndArray()
            writer.WriteEndObject()
            writer.WriteString("response", "json")
            writer.WriteNumber("timeoutMs", UploadTimeoutMs)
            writer.WriteBoolean("progress", false)
            writer.WriteNumber("progressIntervalMs", 0))
    | Release(correlation, file) ->
        writeCapabilityEffect writer correlation files (fun writer ->
            writer.WriteString("operation", "release")
            writer.WriteString("file", file))

/// The engine's complete reply to one kernel message.
let encode (view: View) (effects: Effect list) (handshake: Handshake option) =
    write (fun writer ->
        writer.WriteStartObject()
        writer.WritePropertyName "view"
        writeView writer view
        writer.WritePropertyName "effects"
        writer.WriteStartArray()
        effects |> List.iter (writeEffect writer)
        writer.WriteEndArray()
        writer.WritePropertyName "cancellations"
        writer.WriteStartArray()
        writer.WriteEndArray()

        match handshake with
        | None -> ()
        | Some(Accepted(minor, contract)) ->
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
            writeOffer writer files
            writeOffer writer transfer
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

// ---------------------------------------------------------------------------
// Files-pack facts.
// ---------------------------------------------------------------------------

/// A files-pack fact: which `data-files-input` (and row key) it is about, and
/// the first file the user picked there, if any.
type Picked =
    { Input: string
      Key: string
      File: SelectedFile option }

let picked (fact: JsonNode) : Picked option =
    let path = "$.fact"

    match required "kind" path asString fact with
    | "Selected" ->
        let input = required "input" path asObject fact

        let file =
            required "files" path asArray fact
            |> List.tryHead
            |> Option.map (fun first ->
                { Id = required "file" $"{path}.files[0]" asString first
                  Name = required "name" $"{path}.files[0]" asString first })

        Some
            { Input = required "name" $"{path}.input" asString input
              Key = optional "key" $"{path}.input" asString input |> Option.defaultValue ""
              File = file }
    // Cancelling the picker leaves the input's selection as it was.
    | _ -> None
