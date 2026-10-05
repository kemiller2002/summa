/// The Limen protocol and the Aegis boundary, driven exactly as the browser
/// kernel drives them: JSON messages in, JSON replies out. Faults go to a
/// deterministic Aegis collector sink, so each test can prove both that an
/// operational failure is captured and that an expected refusal is not.
module Summa.Web.Tests.BoundaryTests

open System
open System.Text.Json.Nodes
open Xunit
open Aegis
open Summa.Web.Application

let private collector () =
    let sink = Sinks.Collector()
    let aegis = { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }
    sink, aegis

let private recordedCodes (sink: Sinks.Collector) =
    sink.Events
    |> List.map (fun event -> (JsonNode.Parse event).["code"] |> string)

let private offer (transferFingerprint: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Http","Storage","Clipboard","Navigation"],
        "location":{{"origin":"http://localhost","path":"/","query":"","hash":""}},
        "handshake":{{"protocol":{{"major":1,"minor":4}},
          "contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},
          "capabilities":[
            {{"id":"limen.files","version":1,"fingerprint":"{Limen.files.Fingerprint}"}},
            {{"id":"limen.transfer","version":1,"fingerprint":"{transferFingerprint}"}}]}}}}"""

let private initialize = offer Limen.transfer.Fingerprint

let private event name (key: string option) (value: string option) =
    let key = key |> Option.map (fun k -> $",\"key\":\"{k}\"") |> Option.defaultValue ""
    let value = value |> Option.map (fun v -> $",\"value\":\"{v}\"") |> Option.defaultValue ""
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}"{key}{value}}}}}"""

let private http (correlation: string) (outcome: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"HttpResult","correlationId":"{correlation}","outcome":{outcome}}}}}"""

let private rowsBody = """[{"id":"WI-0001","title":"Ledger","status":"captured","tags":["a"],"priority":"high","backlogActions":["ready"],"attachments":[],"liveWorkItem":null}]"""

let private backlog aegis messages =
    messages
    |> List.fold
        (fun (session, _) message -> BacklogWire.handle aegis session message)
        (BacklogWire.initial, "")

let private viewOf (reply: string) = (JsonNode.Parse reply).["view"]

let private flag (name: string) (reply: string) = (viewOf reply).[name].GetValue<bool>()
let private text (name: string) (reply: string) = (viewOf reply).[name].GetValue<string>()

[<Fact>]
let ``the engine accepts the kernel's handshake and selects the files and transfer packs`` () =
    let _, aegis = collector ()
    let _, reply = BacklogWire.handle aegis BacklogWire.initial initialize
    let handshake = (JsonNode.Parse reply).["handshake"]
    Assert.Equal("Accepted", handshake.["kind"].GetValue<string>())
    Assert.Equal(4, handshake.["protocol"].["minor"].GetValue<int>())

    Assert.Equal<string list>(
        [ "limen.files"; "limen.transfer" ],
        handshake.["capabilities"].AsArray() |> Seq.map (fun c -> c.["id"].GetValue<string>()) |> Seq.toList
    )

    // Initialize also asks for the queue and the repository status.
    let effects = (JsonNode.Parse reply).["effects"].AsArray()

    Assert.Equal<string list>(
        [ "GET /api/work"; "GET /api/status" ],
        effects |> Seq.map (fun e -> $"""{e.["method"]} {e.["url"]}""") |> Seq.toList
    )

[<Fact>]
let ``a pack the engine was not written against is refused, precisely`` () =
    let _, aegis = collector ()
    let _, reply = BacklogWire.handle aegis BacklogWire.initial (offer "sha256:other")
    let handshake = (JsonNode.Parse reply).["handshake"]
    Assert.Equal("Rejected", handshake.["kind"].GetValue<string>())
    Assert.Equal("CapabilityUnavailable", handshake.["reason"].["kind"].GetValue<string>())
    Assert.Equal("limen.transfer", handshake.["reason"].["id"].GetValue<string>())

[<Fact>]
let ``a server refusal is a typed outcome: shown as an error, never an Aegis fault`` () =
    let sink, aegis = collector ()

    let _, reply =
        backlog aegis [ initialize; http "c1" """{"kind":"Success","status":400,"body":{"error":"illegal transition"}}""" ]

    Assert.True(flag "hasError" reply)
    Assert.Equal("illegal transition", text "error" reply)
    Assert.False(flag "hasOperationalFault" reply)
    Assert.Empty(sink.Events)

    // An HTML error page (a body that does not decode) reports the status.
    let _, html = backlog aegis [ initialize; http "c1" """{"kind":"Failure","reason":"invalid-response","status":404}""" ]
    Assert.Equal("request failed (404)", text "error" html)
    // An error body without an `error` field reports the status too.
    Assert.Equal(None, Replies.errorField (Some(JsonNode.Parse """{"message":"x"}""")))
    // A request whose outcome is unknown is said to be unknown, not failed.
    let _, unknown = backlog aegis [ initialize; http "c1" """{"kind":"OutcomeUnknown","reason":"connection-lost"}""" ]
    Assert.Contains("may or may not have been applied", text "error" unknown)
    Assert.Empty(sink.Events)

[<Fact>]
let ``a success whose body breaks the contract is an Aegis fault, presented safely`` () =
    let sink, aegis = collector ()

    let session, loaded =
        backlog aegis [ initialize; http "c1" $"""{{"kind":"Success","status":200,"body":{rowsBody}}}""" ]

    Assert.Equal(1, (viewOf loaded).["rows"].AsArray().Count)
    // A filter change asks for c3; its reply is not a list.
    let session, _ = BacklogWire.handle aegis session (event "tagFilterChanged" None (Some "x"))

    let session, faulted =
        BacklogWire.handle aegis session (http "c3" """{"kind":"Success","status":200,"body":{"rows":"nope"}}""")

    Assert.Equal<string list>([ Boundary.ResponseInvalid ], recordedCodes sink)
    Assert.True(flag "hasOperationalFault" faulted)
    Assert.False(flag "hasError" faulted)
    Assert.Contains("could not read", text "operationalFaultMessage" faulted)
    Assert.NotEqual<string>("", text "operationalFaultReference" faulted)
    // Safe presentation only: no exception names or paths reach the page.
    Assert.DoesNotContain("MalformedInput", faulted)
    Assert.DoesNotContain("$.body", faulted)
    // The page keeps the state it had.
    Assert.Equal(1, (viewOf faulted).["rows"].AsArray().Count)

    // The next message clears the fault.
    let _, next = BacklogWire.handle aegis session (event "tagFilterChanged" None (Some ""))
    Assert.False(flag "hasOperationalFault" next)

[<Fact>]
let ``a 2xx whose body would not decode is a broken contract, not a refusal`` () =
    let sink, aegis = collector ()
    let _, reply = backlog aegis [ initialize; http "c1" """{"kind":"Failure","reason":"invalid-response","status":200}""" ]
    Assert.True(flag "hasOperationalFault" reply)
    Assert.Equal<string list>([ Boundary.ResponseInvalid ], recordedCodes sink)

[<Fact>]
let ``a message that is not JSON is classified as an invalid message`` () =
    let sink, aegis = collector ()
    let _, reply = BacklogWire.handle aegis BacklogWire.initial "{not json"
    Assert.True(flag "hasOperationalFault" reply)
    Assert.Equal<string list>([ Boundary.MessageInvalid ], recordedCodes sink)

[<Fact>]
let ``an upload the kernel did not execute is a capability fault`` () =
    let sink, aegis = collector ()

    let fact =
        """{"kind":"CapabilityFact","capability":"limen.files","version":1,
            "fact":{"kind":"Selected","input":{"name":"add-file","key":"file-0"},
                    "files":[{"file":"f-1","name":"a.txt","size":1,"type":"text/plain","lastModified":0}]}}"""

    let session, _ =
        backlog
            aegis
            [ initialize
              fact
              event "addTitleChanged" None (Some "With a file")
              event "addSubmitted" None None ]

    let session, created =
        BacklogWire.handle aegis session (http "c3" """{"kind":"Success","status":200,"body":{"id":"WI-0001","title":"With a file","status":"captured"}}""")

    let upload = (JsonNode.Parse created).["effects"].[0]
    Assert.Equal("Capability", upload.["kind"].GetValue<string>())
    Assert.Equal("limen.transfer", upload.["capability"].GetValue<string>())
    Assert.Equal("f-1", upload.["request"].["body"].["parts"].[0].["file"].GetValue<string>())
    Assert.Equal("/api/work/WI-0001/attachments", upload.["request"].["url"].GetValue<string>())

    let _, refused =
        BacklogWire.handle
            aegis
            session
            """{"kind":"EffectResult","result":{"kind":"CapabilityResult","correlationId":"c4","capability":"limen.transfer","version":1,"outcome":{"kind":"Unsupported","reason":"not-negotiated"}}}"""

    Assert.True(flag "hasOperationalFault" refused)
    Assert.Equal<string list>([ Boundary.CapabilityUnavailable ], recordedCodes sink)

[<Fact>]
let ``an event the page does not know is a defect: it fails loudly and is not turned into a fault`` () =
    let sink, aegis = collector ()
    let session, _ = BacklogWire.handle aegis BacklogWire.initial initialize
    Assert.Throws<InvalidOperationException>(fun () -> BacklogWire.handle aegis session (event "noSuchEvent" None None) |> ignore)
    |> ignore
    Assert.Empty(sink.Events)

[<Fact>]
let ``the hub runs under the same boundary`` () =
    let sink, aegis = collector ()
    let session, _ = HubWire.handle aegis HubWire.initial initialize
    let _, reply = HubWire.handle aegis session (http "c1" """{"kind":"Success","status":200,"body":[{"id":"r1"}]}""")
    Assert.True(flag "hasOperationalFault" reply)
    Assert.Equal<string list>([ Boundary.ResponseInvalid ], recordedCodes sink)
