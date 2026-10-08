/// The rebuildable runtime index (SUM0-015, SUM0-014): account balances and
/// receivables, projected from the authoritative records into an Arca
/// derived index under `derived/indexes/summa-balances.json`.
///
/// The index is never authoritative: commands never read it, it is stored
/// apart from the records so it can never overwrite one, it records the
/// fingerprint of the records it was built from so a stale index is
/// detected, and a corrupt or stale index is simply rebuilt.
///
/// Pure, apart from `Derived.rebuild`/`Derived.read`, which take a provider.
module Summa.Storage.Projection

open Arca
open Summa.Storage.FinancialRecords

let private segment =
    match Segment.create "summa-balances" with
    | Ok found -> found
    | Error _ -> invalidOp "internal: not a segment"

let private number (value: Json) name =
    match Json.field name value with
    | Some(Json.Number n) -> n
    | _ -> 0m

let private text (value: Json) name =
    match Json.field name value with
    | Some(Json.String s) -> s
    | _ -> ""

/// One record's index entries: each journal line as a signed amount on its
/// account, each invoice's total and each allocation's amount against it.
let private project (record: Record) : (string * Json) list =
    let id = RecordId.value record.Id
    let body = record.Body

    match RecordType.value record.Type with
    | "summa.entry" ->
        match Json.field "lines" body with
        | Some(Json.Array lines) ->
            lines
            |> List.mapi (fun i line ->
                let amount =
                    match Json.field "amount" line with
                    | Some m -> m
                    | None -> Json.Null

                let sign = if text line "side" = "debit" then 1m else -1m

                $"line:{id}:{i:D3}",
                Json.objectOf
                    [ "account", Json.String(text line "accountId")
                      "currency", Json.String(text amount "currency")
                      "debitMinusCredit", Json.Number(sign * number amount "minor")
                      "date", Json.String(text body "date") ])
        | _ -> []
    | "summa.invoice" ->
        let total =
            match Json.field "total" body with
            | Some m -> m
            | None -> Json.Null

        [ $"invoice:{id}",
          Json.objectOf
              [ "customer", Json.String(text body "customerId")
                "currency", Json.String(text total "currency")
                "total", Json.Number(number total "minor")
                "dueDate", Json.String(text body "dueDate") ] ]
    | "summa.allocation" ->
        let amount =
            match Json.field "amount" body with
            | Some m -> m
            | None -> Json.Null

        [ $"allocation:{id}", Json.objectOf [ "invoice", Json.String(text body "invoiceId"); "amount", Json.Number(number amount "minor") ] ]
    | _ -> []

/// The index definition. Change `Version` whenever `project` changes.
let definition: IndexDefinition =
    { Name = segment
      Version = 1
      Sources = schemas |> List.filter (fun s -> List.contains (RecordType.value s.Type) [ "summa.entry"; "summa.invoice"; "summa.allocation" ])
      Project = project }

/// Each account's balance (debits minus credits, in minor units) per currency.
let balances (index: DerivedIndex) : Map<string * string, int64> =
    index.Entries
    |> List.filter (fun (key, _) -> key.StartsWith "line:")
    |> List.groupBy (fun (_, v) -> text v "account", text v "currency")
    |> List.map (fun (key, lines) -> key, lines |> List.sumBy (fun (_, v) -> int64 (number v "debitMinusCredit")))
    |> Map.ofList

/// Each invoice's outstanding amount, in minor units.
let outstanding (index: DerivedIndex) : Map<string, int64> =
    let allocated =
        index.Entries
        |> List.filter (fun (key, _) -> key.StartsWith "allocation:")
        |> List.groupBy (fun (_, v) -> text v "invoice")
        |> List.map (fun (invoice, rows) -> invoice, rows |> List.sumBy (fun (_, v) -> int64 (number v "amount")))
        |> Map.ofList

    index.Entries
    |> List.filter (fun (key, _) -> key.StartsWith "invoice:")
    |> List.map (fun (key, v) ->
        let id = key.Substring "invoice:".Length
        id, int64 (number v "total") - (allocated.TryFind id |> Option.defaultValue 0L))
    |> Map.ofList
