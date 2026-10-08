namespace Summa.Contracts

open System
open System.Text
open System.Text.Json

/// The JSON values the contract reads and writes. Numbers are whole numbers
/// only: the contract carries minutes, revisions and versions, never money
/// or fractions, so a fractional number is malformed rather than rounded.
[<RequireQualifiedAccess>]
type Json =
    | Null
    | Bool of bool
    | Integer of int64
    | String of string
    | Array of Json list
    | Object of (string * Json) list

/// Canonical serialization and strict parsing.
///
/// Canonical form (the bytes a digest is taken over, and the bytes golden
/// vectors hold): no insignificant whitespace; object members sorted by key
/// in UTF-16 code-unit order; strings escape only `"`, `\` and control
/// characters (the two-character forms for \b \t \n \f \r, otherwise
/// lowercase `\u00xx`); everything else is literal UTF-8. For the value types
/// above this is the JSON Canonicalization Scheme (RFC 8785).
[<RequireQualifiedAccess>]
module Json =

    let private escape (text: string) =
        text
        |> Seq.map (fun c ->
            match c with
            | '"' -> "\\\""
            | '\\' -> "\\\\"
            | '\b' -> "\\b"
            | '\t' -> "\\t"
            | '\n' -> "\\n"
            | '\f' -> "\\f"
            | '\r' -> "\\r"
            | c when c < ' ' -> sprintf "\\u%04x" (int c)
            | c -> string c)
        |> String.concat ""

    let rec private render (json: Json) : string =
        match json with
        | Json.Null -> "null"
        | Json.Bool true -> "true"
        | Json.Bool false -> "false"
        | Json.Integer n -> string n
        | Json.String s -> "\"" + escape s + "\""
        | Json.Array items -> "[" + (items |> List.map render |> String.concat ",") + "]"
        | Json.Object members ->
            let body =
                members
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                |> List.map (fun (key, value) -> "\"" + escape key + "\":" + render value)
                |> String.concat ","

            "{" + body + "}"

    /// The canonical text of a value.
    let serialize (json: Json) : string = render json

    /// The canonical UTF-8 bytes of a value.
    let toUtf8 (json: Json) : byte array = Encoding.UTF8.GetBytes(serialize json)

    let private options =
        JsonDocumentOptions(AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32)

    /// Every result, or the first error.
    let private sequence (results: Result<'a, string> list) : Result<'a list, string> =
        List.foldBack
            (fun item state ->
                match item, state with
                | Ok value, Ok rest -> Ok(value :: rest)
                | Error e, _
                | _, Error e -> Error e)
            results
            (Ok [])

    let rec private ofElement (path: string) (element: JsonElement) : Result<Json, string> =
        match element.ValueKind with
        | JsonValueKind.Null -> Ok Json.Null
        | JsonValueKind.True -> Ok(Json.Bool true)
        | JsonValueKind.False -> Ok(Json.Bool false)
        | JsonValueKind.String -> Ok(Json.String(element.GetString() |> Option.ofObj |> Option.defaultValue ""))
        | JsonValueKind.Number ->
            match element.TryGetInt64() with
            | true, n -> Ok(Json.Integer n)
            | _ -> Error $"{path}: only whole numbers are allowed"
        | JsonValueKind.Array ->
            element.EnumerateArray()
            |> Seq.mapi (fun i item -> ofElement $"{path}[{i}]" item)
            |> Seq.toList
            |> sequence
            |> Result.map Json.Array
        | JsonValueKind.Object ->
            let members = element.EnumerateObject() |> Seq.toList
            let names = members |> List.map _.Name

            match names |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
            | Some(name, _) -> Error $"{path}.{name}: the member appears more than once"
            | None ->
                members
                |> List.map (fun m -> ofElement $"{path}.{m.Name}" m.Value |> Result.map (fun v -> m.Name, v))
                |> sequence
                |> Result.map Json.Object
        | other -> Error $"{path}: unexpected {other}"

    /// Parses JSON text strictly: no comments, no trailing commas, no
    /// duplicate member names, whole numbers only, at most 32 levels deep.
    let parse (text: string) : Result<Json, string> =
        try
            use document = JsonDocument.Parse(text, options)
            ofElement "$" document.RootElement
        with
        | :? JsonException as e -> Error $"$: not valid JSON ({e.Message})"
        // An escaped lone surrogate parses but is not text.
        | :? InvalidOperationException -> Error "$: a string is not valid Unicode text"

    /// A member of an object, if present.
    let tryField (name: string) (json: Json) : Json option =
        match json with
        | Json.Object members -> members |> List.tryFind (fst >> (=) name) |> Option.map snd
        | _ -> None
