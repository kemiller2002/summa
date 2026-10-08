namespace Summa.Contracts

open System
open System.Globalization
open System.Text.RegularExpressions

/// One reason a message is not acceptable: where (a JSON path such as
/// `$.message.source.revision`) and what is wrong, in one sentence.
type Problem = { Path: string; Message: string }

/// A decoded value, or every problem found.
type Decoded<'a> = Result<'a, Problem list>

/// Applicative decoding of untrusted JSON: every field is read, and every
/// problem is reported, not only the first.
module Decode =

    let problem (path: string) (message: string) : Decoded<'a> = Error [ { Path = path; Message = message } ]

    let map2 (f: 'a -> 'b -> 'c) (a: Decoded<'a>) (b: Decoded<'b>) : Decoded<'c> =
        match a, b with
        | Ok a, Ok b -> Ok(f a b)
        | Error x, Error y -> Error(x @ y)
        | Error x, _
        | _, Error x -> Error x

    /// Applies a decoded function to a decoded argument, keeping the
    /// problems of both.
    let apply (f: Decoded<'a -> 'b>) (a: Decoded<'a>) : Decoded<'b> = map2 (fun f a -> f a) f a

    /// Every element, or every element's problems.
    let all (items: Decoded<'a> list) : Decoded<'a list> =
        List.foldBack (map2 (fun item rest -> item :: rest)) items (Ok [])

    /// Combines the problems of checks that produce nothing.
    let checks (results: Decoded<unit> list) : Decoded<unit> = all results |> Result.map ignore

    let field (path: string) (name: string) (json: Json) : Decoded<Json> =
        match json with
        | Json.Object _ ->
            match Json.tryField name json with
            | Some value -> Ok value
            | None -> problem $"{path}.{name}" "is required"
        | _ -> problem path "must be an object"

    /// An object whose members are all named in `names`. When `strict` is
    /// false (a newer minor version of the same major), members this version
    /// does not define are tolerated and ignored; otherwise they are refused.
    let closed (strict: bool) (path: string) (names: string list) (json: Json) : Decoded<unit> =
        match json with
        | Json.Object members when strict ->
            members
            |> List.filter (fun (key, _) -> not (List.contains key names))
            |> List.map (fun (key, _) -> problem $"{path}.{key}" "is not a field of this contract version")
            |> checks
        | Json.Object _ -> Ok()
        | _ -> problem path "must be an object"

    let text (path: string) (json: Json) : Decoded<string> =
        match json with
        | Json.String s -> Ok s
        | _ -> problem path "must be text"

    let integer (path: string) (json: Json) : Decoded<int> =
        match json with
        | Json.Integer n when n >= int64 Int32.MinValue && n <= int64 Int32.MaxValue -> Ok(int n)
        | Json.Integer _ -> problem path "is out of range"
        | _ -> problem path "must be a whole number"

    let optional (decode: string -> Json -> Decoded<'a>) (path: string) (json: Json) : Decoded<'a option> =
        match json with
        | Json.Null -> Ok None
        | value -> decode path value |> Result.map Some

    let list (decode: string -> Json -> Decoded<'a>) (path: string) (json: Json) : Decoded<'a list> =
        match json with
        | Json.Array items -> items |> List.mapi (fun i item -> decode $"{path}[{i}]" item) |> all
        | _ -> problem path "must be a list"

    /// Reads member `name` of an object with `decode`.
    let member' (decode: string -> Json -> Decoded<'a>) (path: string) (name: string) (json: Json) : Decoded<'a> =
        field path name json |> Result.bind (decode $"{path}.{name}")

    let private datePattern = Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)

    /// A calendar date, `yyyy-MM-dd`.
    let date (path: string) (json: Json) : Decoded<DateOnly> =
        text path json
        |> Result.bind (fun s ->
            match datePattern.IsMatch s, DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, (true, d) -> Ok d
            | _ -> problem path "must be a calendar date, yyyy-MM-dd")

    let private instantPattern =
        Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)

    /// An instant with an explicit UTC offset (RFC 3339).
    let instant (path: string) (json: Json) : Decoded<DateTimeOffset> =
        text path json
        |> Result.bind (fun s ->
            match instantPattern.IsMatch s, DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, (true, t) -> Ok t
            | _ -> problem path "must be an instant with an explicit offset, for example 2026-10-08T09:30:00.0000000+00:00")

/// The JSON a value encodes to.
module Encode =

    let text (s: string) = Json.String s
    let integer (n: int) = Json.Integer(int64 n)
    let optional (encode: 'a -> Json) (value: 'a option) = value |> Option.map encode |> Option.defaultValue Json.Null
    let list (encode: 'a -> Json) (items: 'a list) = Json.Array(List.map encode items)
    let date (d: DateOnly) = Json.String(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

    /// Round-trip form: seven fractional digits and the offset, so decoding
    /// gives back the same instant and offset.
    let instant (t: DateTimeOffset) = Json.String(t.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture))
