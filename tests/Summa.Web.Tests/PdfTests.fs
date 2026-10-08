/// The invoice's PDF in the accounting application (WI-0030 slice 4,
/// INV-DOC-011): printed through Folio, attached by the person, read in
/// slices through the files pack, checked and fingerprinted, kept in this
/// environment's artifact database through the store pack, recorded with
/// the invoice and offered back as a download. And the pre-issue preview
/// (INV-REV-001).
module Summa.Web.Tests.PdfTests

open System
open System.Text
open System.Text.Json.Nodes
open Xunit
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
module Wire = Summa.Web.Application.AccountingWire

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private go hash =
    LocationChanged
        { Origin = "https://summa.example"
          Path = "/app/"
          Query = ""
          Hash = hash }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | Some(_, Value(Number n)) -> string n
    | _ -> failwith $"no scalar view value {name}"

let private all = { Files = true; Store = true }
let private configuration = """{"environment":"local","environmentName":"test"}"""

/// Started with the packs, an invoice issued and opened.
let private opened (packs: Packs) =
    let started =
        run [ Started(match go "" with LocationChanged p -> p | _ -> failwith "page"); PacksNegotiated packs; ConfigurationRead(Ok configuration); Loaded None ] initial
        |> fst
        |> run [ go "#/customers"; CustomerNameChanged "Acme"; CustomerAddressChanged "1 Main"; CustomerAdded; go "#/invoices/new?customer=CUST-0001" ]
        |> fst

    let key = started.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Work"); LineRateChanged(key, "100"); DraftSubmitted; DraftIssued ] started |> fst

let private pdfBytes size =
    let header = Encoding.ASCII.GetBytes "%PDF-1.7\n"
    Array.append header (Array.create (size - header.Length) (byte 'x'))

let private picked (bytes: byte[]) = PdfPicked(Some { Id = "file-1"; Name = "INV.pdf"; Size = int64 bytes.Length; Type = "application/pdf" })

[<Fact>]
let ``with the store pack, the environment's artifact database is opened; without it, no PDF can be attached`` () =
    let _, effects =
        run [ Started(match go "" with LocationChanged p -> p | _ -> failwith "page"); PacksNegotiated all; ConfigurationRead(Ok configuration) ] initial

    Assert.Contains(OpenArtifactStore "summa-artifacts-local", effects)
    let without = opened { Files = false; Store = false }
    Assert.Equal("False", value "canAttachPdf" without)
    Assert.Equal("Not stored yet. Print the invoice and save it as PDF, then attach that file here.", value "pdfStatus" without)

[<Fact>]
let ``a PDF is read in slices, fingerprinted, stored and recorded, then offered back as a download`` () =
    let model = run [ ArtifactStoreOpened(Ok()) ] (opened all) |> fst
    Assert.Equal("True", value "canAttachPdf" model)
    let bytes = pdfBytes (ChunkBytes + 10)
    let first, reads = run [ picked bytes ] model
    Assert.Equal<AppEffect list>([ ReadFileSlice("file-1", 0L, ChunkBytes) ], reads)
    let second, more = run [ PdfChunkRead(Ok(Convert.ToBase64String(bytes[.. ChunkBytes - 1]), false)) ] first
    Assert.Equal<AppEffect list>([ ReadFileSlice("file-1", int64 ChunkBytes, 10) ], more)
    let storing, puts = run [ PdfChunkRead(Ok(Convert.ToBase64String(bytes[ChunkBytes ..]), true)) ] second

    let sha = Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()
    Assert.Equal<AppEffect list>([ ReleaseFile "file-1"; PutArtifact("summa-artifacts-local", sha, Convert.ToBase64String bytes, int64 bytes.Length) ], puts)
    Assert.Equal("True", value "isPdfWorking" storing)

    let stored, saves = run [ PdfStored(Ok()) ] storing
    Assert.Equal("The PDF is stored with the invoice.", value "notice" stored)
    Assert.Contains(saves, function SaveBooks _ -> true | _ -> false)
    Assert.Equal($"Stored. SHA-256 {sha.Substring(0, 12)}…", value "pdfStatus" stored)
    Assert.Equal("False", value "canAttachPdf" stored)
    Assert.Equal("True", value "canDownloadPdf" stored)

    let fetching, gets = run [ PdfDownloadRequested ] stored
    Assert.Equal<AppEffect list>([ GetArtifact("summa-artifacts-local", sha) ], gets)
    let number = stored.Books.Value.Books.Invoices["INV-0001"].Number
    Assert.Equal<AppEffect list>([ OfferDownload($"{number}.pdf", "application/pdf", "AAAA") ], run [ PdfFetched(Ok(Some "AAAA")) ] fetching |> snd)
    let gone = run [ PdfFetched(Ok None) ] fetching |> fst
    Assert.StartsWith("This browser no longer holds the PDF.", value "error" gone)

[<Fact>]
let ``a file that is not a PDF, too large, or unreadable is refused and released`` () =
    let model = run [ ArtifactStoreOpened(Ok()) ] (opened all) |> fst
    let html = Encoding.ASCII.GetBytes "<html>not a pdf</html>"
    let reading = run [ picked html ] model |> fst
    let refused, effects = run [ PdfChunkRead(Ok(Convert.ToBase64String html, true)) ] reading
    Assert.Equal<AppEffect list>([ ReleaseFile "file-1" ], effects)
    Assert.StartsWith("The file is not a PDF.", value "error" refused)
    Assert.Equal("True", value "canAttachPdf" refused)

    let large, released = run [ PdfPicked(Some { Id = "big"; Name = "big.pdf"; Size = 20_000_000L; Type = "application/pdf" }) ] model
    Assert.Equal<AppEffect list>([ ReleaseFile "big" ], released)
    Assert.Equal("The file is larger than 10 MB.", value "error" large)

    let failed = run [ picked html; PdfChunkRead(Error "Unreadable: gone") ] model |> fst
    Assert.Equal("The file could not be read (Unreadable: gone).", value "error" failed)
    let quota = run [ picked (pdfBytes 100); PdfChunkRead(Ok(Convert.ToBase64String(pdfBytes 100), true)); PdfStored(Error "Aborted") ] model |> fst
    Assert.Equal("This browser could not store the PDF (Aborted).", value "error" quota)
    Assert.Equal("True", value "canAttachPdf" quota)

[<Fact>]
let ``a reviewed draft shows the document it will be issued as`` () =
    let started =
        run [ Started(match go "" with LocationChanged p -> p | _ -> failwith "page"); ConfigurationRead(Ok configuration); Loaded None ] initial
        |> fst
        |> run [ go "#/customers"; CustomerNameChanged "Acme"; CustomerAddressChanged "1 Main"; CustomerAdded; go "#/invoices/new?customer=CUST-0001" ]
        |> fst

    let key = started.Draft.Lines.Head.Key
    let editing = run [ LineDescriptionChanged(key, "Work"); LineHoursChanged(key, "2"); LineRateChanged(key, "100") ] started |> fst
    Assert.Equal("False", value "hasPreview" editing)
    let reviewed = run [ DraftSubmitted ] editing |> fst
    Assert.Equal("True", value "hasPreview" reviewed)
    Assert.Equal("200.00 USD", value "previewTotal" reviewed)
    Assert.Equal("ACH to account ending 0000 (demo)", value "previewPayment" reviewed)
    let issued = run [ DraftIssued ] reviewed |> fst
    // The preview's number is the one the invoice is issued with.
    Assert.Equal(issued.Books.Value.Books.Invoices["INV-0001"].Number, value "previewNumber" reviewed)

// ---- The wire ------------------------------------------------------------------------------------------

let private aegis = Summa.Web.Application.Boundary.configure [ (Aegis.Sinks.Collector()).Sink() ]

/// The string at a path of object keys.
let private textAt (path: string list) (node: JsonNode) =
    (path |> List.fold (fun (n: JsonNode) key -> n.AsObject()[key] |> Option.ofObj |> Option.get) node).GetValue<string>()

let private send (session: Wire.Session) (message: string) =
    let next, reply = Wire.handle aegis (fun () -> ctx.Now) "local-person" session message
    next, JsonNode.Parse(reply) |> Option.ofObj |> Option.get

[<Fact>]
let ``the wire selects the files and store packs, and turns their results into the engine's messages`` () =
    let initialize =
        """{"kind":"Initialize","location":{"origin":"https://summa.example","path":"/app/","query":"","hash":""},"handshake":{"protocol":{"major":1,"minor":4},"contract":{"unit":"limen.core","version":1,"fingerprint":"sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"},"capabilities":[{"id":"limen.files","version":1,"fingerprint":"sha256:2cf28b1016994b11955c412282ec6038a6c8c205a056b14c6039ef6fd5bfabdf"},{"id":"limen.store","version":1,"fingerprint":"sha256:0ba8d199066c4ef37ec8a3fd0767bf191644f581b0faa6c545234a1b5f7c4cf6"}]}}"""

    let session, reply = send Wire.initial initialize
    let capabilities = (reply.AsObject().["handshake"]).AsObject().["capabilities"]
    let selected = capabilities.AsArray() |> Seq.map (textAt [ "id" ]) |> List.ofSeq
    Assert.Equal<string list>([ "limen.files"; "limen.store" ], selected)
    Assert.Equal({ Files = true; Store = true }, session.Model.Packs)

    let correlationOf (reply: JsonNode) kind =
        reply["effects"].AsArray() |> Seq.find (fun e -> textAt [ "kind" ] e = kind) |> textAt [ "correlationId" ]

    let configured, reply =
        send session ("{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"HttpResult\",\"correlationId\":\"" + correlationOf reply "Http" + "\",\"outcome\":{\"kind\":\"Success\",\"status\":200,\"body\":{\"environment\":\"local\",\"environmentName\":\"test\"}}}}")

    let opening = reply["effects"].AsArray() |> Seq.find (fun e -> textAt [ "kind" ] e = "Capability")
    Assert.Equal("limen.store", textAt [ "capability" ] opening)
    Assert.Equal("open", textAt [ "request"; "operation" ] opening)
    Assert.Equal("summa-artifacts-local", textAt [ "request"; "database" ] opening)

    let opened, _ =
        send configured ("{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"CapabilityResult\",\"correlationId\":\"" + textAt [ "correlationId" ] opening + "\",\"capability\":\"limen.store\",\"version\":1,\"outcome\":{\"kind\":\"Completed\",\"result\":{\"kind\":\"Opened\",\"version\":1,\"upgradedFrom\":0}}}}")

    Assert.Equal(StoreOpen, opened.Model.Artifacts)

    // A file picked elsewhere on the page is not the PDF input's.
    let other, _ = send opened """{"kind":"CapabilityFact","capability":"limen.files","version":1,"fact":{"kind":"Selected","input":{"name":"elsewhere"},"files":[]}}"""
    Assert.Equal(PdfIdle, other.Model.Pdf)
