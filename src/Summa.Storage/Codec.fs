/// Small, total readers for Summa's stored and configured JSON (Arca's
/// `Json`). Everything read is untrusted: each read names the field and what
/// was wrong, and objects are closed so a field this version does not define
/// is refused rather than ignored.
module Summa.Storage.Codec

open System
open System.Globalization
open Arca

/// A decoded value, or one sentence saying why the input is not one.
type Decoded<'a> = Result<'a, string>

let field (name: string) (value: Json) : Decoded<Json> =
    match Json.field name value with
    | Some found -> Ok found
    | None -> Error $"'{name}' is missing"

let text name value : Decoded<string> =
    field name value
    |> Result.bind (function
        | Json.String found -> Ok found
        | _ -> Error $"'{name}' is not text")

let optionalText name value : Decoded<string option> =
    match Json.field name value with
    | None
    | Some Json.Null -> Ok None
    | Some(Json.String found) -> Ok(Some found)
    | Some _ -> Error $"'{name}' is not text or null"

let integer name value : Decoded<int> =
    field name value
    |> Result.bind (function
        | Json.Number number when number = Math.Floor number && number >= decimal Int32.MinValue && number <= decimal Int32.MaxValue ->
            Ok(int number)
        | _ -> Error $"'{name}' is not a whole number")

/// The object's members, when every key is one of `names`.
let closed (names: string list) (value: Json) : Decoded<unit> =
    match value with
    | Json.Object members ->
        match members |> List.tryFind (fun (key, _) -> not (List.contains key names)) with
        | Some(key, _) -> Error $"'{key}' is not a field this version reads"
        | None -> Ok()
    | _ -> Error "expected an object"

/// Every element decoded, or the first failure.
let traverse (decode: 'a -> Decoded<'b>) (items: 'a list) : Decoded<'b list> =
    List.foldBack (fun item state -> Result.bind (fun rest -> decode item |> Result.map (fun d -> d :: rest)) state) items (Ok [])

/// A UTC instant to millisecond precision, `yyyy-MM-ddTHH:mm:ss.fffZ`.
let timestamp (at: DateTimeOffset) =
    at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

let instant name value : Decoded<DateTimeOffset> =
    text name value
    |> Result.bind (fun s ->
        match DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, at -> Ok(at.ToUniversalTime())
        | _ -> Error $"'{name}' is not a UTC timestamp")

let number (n: int) = Json.Number(decimal n)

/// Sequencing for decoders: `decode { let! a = ... ; return ... }`.
type DecodeBuilder() =
    member _.Bind(value: Decoded<'a>, next: 'a -> Decoded<'b>) = Result.bind next value
    member _.Return(value: 'a) : Decoded<'a> = Ok value
    member _.ReturnFrom(value: Decoded<'a>) = value

let decode = DecodeBuilder()

/// An instant to the tick, in UTC: `yyyy-MM-ddTHH:mm:ss.fffffffZ`, so a
/// stored instant reads back equal.
let preciseTimestamp (at: DateTimeOffset) =
    at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)

let preciseInstant name value : Decoded<DateTimeOffset> =
    text name value
    |> Result.bind (fun s ->
        match DateTimeOffset.TryParseExact(s, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, at -> Ok(at.ToUniversalTime())
        | _ -> Error $"'{name}' is not a UTC instant")

let dateText (d: DateOnly) = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

let date name value : Decoded<DateOnly> =
    text name value
    |> Result.bind (fun s ->
        match DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, d -> Ok d
        | _ -> Error $"'{name}' is not a date")

let long name value : Decoded<int64> =
    field name value
    |> Result.bind (function
        | Json.Number n when n = Math.Floor n && n >= decimal Int64.MinValue && n <= decimal Int64.MaxValue -> Ok(int64 n)
        | _ -> Error $"'{name}' is not a whole number")

let flag name value : Decoded<bool> =
    field name value
    |> Result.bind (function
        | Json.Bool b -> Ok b
        | _ -> Error $"'{name}' is not true or false")

let optionalDate name value : Decoded<DateOnly option> =
    match Json.field name value with
    | None
    | Some Json.Null -> Ok None
    | Some _ -> date name value |> Result.map Some

let optionalInstant name value : Decoded<DateTimeOffset option> =
    match Json.field name value with
    | None
    | Some Json.Null -> Ok None
    | Some _ -> preciseInstant name value |> Result.map Some

let list name (item: Json -> Decoded<'a>) value : Decoded<'a list> =
    field name value
    |> Result.bind (function
        | Json.Array items -> traverse item items
        | _ -> Error $"'{name}' is not a list")

let optionalString (value: string option) = value |> Option.map Json.String |> Option.defaultValue Json.Null
