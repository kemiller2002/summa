/// The contract's golden vectors, shipped inside the package so a producer
/// (Chrona) and the receiver (Summa) test against the same bytes.
///
/// - `valid`: canonical messages; decoding then encoding gives back exactly
///   these bytes.
/// - `invalid`: messages a reader must refuse; each names the JSON path of
///   one problem the refusal must report.
module Summa.Contracts.GoldenVectors

open System.IO
open System.Reflection
open System.Text

/// One vector: its name (the file name without `.json`) and its UTF-8 text.
type Vector = { Name: string; Text: string }

/// A vector a reader must refuse, and a path its problems must include.
type Refusal = { Vector: Vector; ExpectedPath: string }

let private assembly = typeof<Vector>.Assembly

let private read (resource: string) =
    match assembly.GetManifestResourceStream resource with
    | null -> ""
    | stream ->
        use reader = new StreamReader(stream, Encoding.UTF8)
        reader.ReadToEnd()

let private under (folder: string) =
    assembly.GetManifestResourceNames()
    |> Array.filter (fun name -> name.StartsWith($"vectors/{folder}/", System.StringComparison.Ordinal))
    |> Array.sort
    |> Array.toList
    |> List.map (fun resource ->
        { Name = Path.GetFileNameWithoutExtension resource |> string
          Text = (read resource).TrimEnd('\n') })

/// Canonical messages of every kind, Chrona's and Summa's.
let valid: Vector list = under "valid"

/// Messages a reader must refuse. The file name is
/// `<case>@<expected path>.json`, for example
/// `blank-project@$.message.classification.projectId.json`.
let invalid: Refusal list =
    under "invalid"
    |> List.map (fun vector ->
        match vector.Name.Split('@', 2) with
        | [| case; path |] ->
            { Vector = { vector with Name = case }
              ExpectedPath = path }
        | _ -> { Vector = vector; ExpectedPath = "$" })
