/// Shared vocabulary of the two Summa web engines (the work backlog and the
/// project-administration hub).
///
/// Everything here is a pure function of its arguments. Nothing in this
/// project touches the browser, the network, the clock or the file system:
/// it is the authority side of Limen's boundary (see limen.config.json). The
/// engines *describe* requests as data; the Limen kernel performs them and the
/// application tier (Summa.Web.Application) feeds the outcomes back in.
module Summa.Web.Engine.Common

open System
open System.Globalization
open System.Text

/// A JSON value the engine wants sent. Requests are data, so their bodies are
/// data too; the application tier writes them out.
type Json =
    | JNull
    | JBool of bool
    | JNumber of decimal
    | JString of string
    | JArray of Json list
    | JObject of (string * Json) list

type Method =
    | Get
    | Post
    | Delete

/// An HTTP request against the local Summa server, described as data.
type Request =
    { Method: Method
      Path: string
      Json: Json option }

/// Identifies one requested effect so its outcome can be matched to the
/// question it answers.
type Correlation = string

/// An opaque id the Limen files pack issued for a file the user picked.
/// The engine never sees the file itself, only this id and its name.
type FileId = string

type SelectedFile = { Id: FileId; Name: string }

/// One field of a multipart/form-data body.
type Part =
    | Field of name: string * value: string
    | FilePart of name: string * file: FileId * fileName: string

/// An effect the engine asks the kernel to perform.
type Effect =
    /// A JSON request through Limen's core Http effect.
    | Send of Correlation * Request
    /// A multipart POST through Limen's transfer pack, carrying picked files by id.
    | Upload of Correlation * path: string * parts: Part list
    /// Gives a picked file's id back to the files pack once it is no longer needed.
    | Release of Correlation * FileId

/// How a request ended, from the page's point of view. `Refused` is an
/// expected, typed outcome (the server said no, or nothing came back); it is
/// shown to the user and is never an Aegis fault.
type Reply<'response> =
    | Answered of 'response
    | Refused of string

/// One "file + optional name" row of an upload form. `Key` is the row's
/// identity in the page (Limen's data-key and the files pack's
/// data-files-key), so a picked file can be matched to its row.
type FileRow =
    { Key: string
      File: SelectedFile option
      Name: string }

/// Splits a comma-separated tag list, trimming and dropping empty entries.
let parseTags (raw: string) =
    raw.Split(',')
    |> Array.map _.Trim()
    |> Array.filter (String.IsNullOrEmpty >> not)
    |> Array.toList

let private hex (b: byte) = "%" + b.ToString("X2", CultureInfo.InvariantCulture)

let private escapeWith (keep: char -> bool) (spaceAsPlus: bool) (value: string) =
    let rune (r: Rune) =
        let text = r.ToString()

        if r.IsAscii && keep (char r.Value) then text
        elif spaceAsPlus && r.Value = int ' ' then "+"
        else Encoding.UTF8.GetBytes text |> Array.map hex |> String.concat ""

    value.EnumerateRunes() |> Seq.map rune |> String.concat ""

let private alphanumeric (c: char) =
    (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')

/// JavaScript's `encodeURIComponent`, so a path the engine builds is the one
/// the browser page always built.
let encodeComponent =
    escapeWith (fun c -> alphanumeric c || "-_.!~*'()".Contains c) false

/// The `application/x-www-form-urlencoded` serializer `URLSearchParams` uses.
let private formEncode = escapeWith (fun c -> alphanumeric c || "*-._".Contains c) true

/// `URLSearchParams(pairs).toString()`, with a leading `?` when non-empty.
let queryString (pairs: (string * string) list) =
    match pairs with
    | [] -> ""
    | _ ->
        pairs
        |> List.map (fun (key, value) -> formEncode key + "=" + formEncode value)
        |> String.concat "&"
        |> fun query -> "?" + query

/// Query pairs for a comma-separated tag filter: one `tag` per trimmed tag.
let tagPairs (raw: string) =
    if String.IsNullOrWhiteSpace raw then
        []
    else
        parseTags raw |> List.map (fun tag -> "tag", tag)

/// The message shown for a failed request: the server's `error`, or the status.
let responseError (status: int) (serverError: string option) =
    match serverError with
    | Some message -> message
    | None -> $"request failed ({status})"

/// A file's associated name: the optional override, else the picked file's own name.
let uploadName (overrideName: string) (fileName: string) =
    match overrideName.Trim() with
    | "" -> fileName
    | trimmed -> trimmed

/// The (file, name) pairs a set of upload rows submits: rows with no picked
/// file are skipped.
let uploads (rows: FileRow list) =
    rows
    |> List.choose (fun row -> row.File |> Option.map (fun file -> file, uploadName row.Name file.Name))

/// The ids of every picked file in a set of rows.
let pickedFiles (rows: FileRow list) =
    rows |> List.choose (fun row -> row.File |> Option.map _.Id)

/// Records a pick (or a cleared pick) against the row it belongs to.
let filePicked (key: string) (file: SelectedFile option) (rows: FileRow list) =
    rows |> List.map (fun row -> if row.Key = key then { row with File = file } else row)

let fileNamed (key: string) (name: string) (rows: FileRow list) =
    rows |> List.map (fun row -> if row.Key = key then { row with Name = name } else row)

/// A row-key source: page-unique keys are drawn from a counter the state
/// threads, so the engine stays deterministic.
let nextKey (prefix: string) (counter: int) = $"{prefix}-{counter}", counter + 1

let emptyFileRow key = { Key = key; File = None; Name = "" }

/// A value a page binds to. Named values and named lists of flat items are
/// all Limen's view can carry (its items admit only scalars), so the engine
/// projects into exactly that shape.
type Scalar =
    | Text of string
    | Flag of bool
    | Number of decimal

type ViewValue =
    | Value of Scalar
    | Items of (string * Scalar) list list

type View = (string * ViewValue) list

/// Forma's status cue for a work status (`data-state` on `.ef-status-lozenge`):
/// done, blocked, in flight, or (empty) the lozenge's neutral form. The status
/// text is always shown inside the lozenge, so the cue never carries meaning on
/// colour alone, and it does not decide anything: the server owns status.
let statusTone (status: string) =
    match status with
    | "complete" -> "ok"
    | "blocked" -> "blocked"
    | "active" -> "attention"
    | _ -> ""
