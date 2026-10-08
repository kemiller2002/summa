/// One organization member as an Arca record:
/// `records/summa.member/<principal>.json` inside the organization's folder,
/// mutable under its revision. The roster is the set of member records; a
/// removed member's record is deleted. Capabilities are stored by stable
/// name; one this version does not know is refused, never dropped.
///
/// Pure.
module Summa.Storage.MemberRecord

open System
open Arca
open Summa.Access.Access
open Summa.Storage.Diagnostics

let recordType =
    match RecordType.create "summa.member" with
    | Ok found -> found
    | Error _ -> invalidOp "internal: not a record type"

let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// A principal id as a record id: `github:583231` becomes `github_583231`;
/// any character outside `A-Z a-z 0-9 - _` becomes `_xx` (hex), so distinct
/// ids stay distinct and the id is a safe path segment.
let idOf (principalId: string) =
    principalId
    |> Seq.map (fun c ->
        if Char.IsAsciiLetterOrDigit c || c = '-' then string c
        elif c = ':' then "_"
        else $"_{int c:x2}")
    |> String.concat ""

let key (principalId: string) : Result<RecordKey, Diagnostic> =
    match RecordId.create (idOf principalId) with
    | Ok id ->
        Ok
            { Type = recordType
              Partition = []
              Id = id }
    | Error _ -> Error(StorageOperationRefused $"'{principalId}' cannot be stored as a member")

let path (principalId: string) : Result<RelativePath, Diagnostic> =
    key principalId |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder holding an organization's members.
let folder: RelativePath =
    match RelativePath.parse (Layout.RecordsFolder + "/" + RecordType.value recordType) with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

let body (membership: Membership) =
    Json.objectOf
        [ "principalId", Json.String membership.Principal.PrincipalId
          "kind", Json.String(kindName membership.Principal.Kind)
          "displayName", Json.String membership.Principal.DisplayName
          "capabilities",
          Json.Array(allCapabilities |> List.filter membership.Capabilities.Contains |> List.map (capabilityName >> Json.String))
          "revision", Codec.number membership.Revision ]

let encode (membership: Membership) : Result<string, Diagnostic> =
    key membership.Principal.PrincipalId
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body membership }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> StorageOperationRefused "the membership record is too large"))

let private texts name value : Codec.Decoded<string list> =
    Codec.field name value
    |> Result.bind (function
        | Json.Array items ->
            items
            |> Codec.traverse (function
                | Json.String s -> Ok s
                | _ -> Error $"'{name}' holds something other than text")
        | _ -> Error $"'{name}' is not a list")

let ofBody (value: Json) : Codec.Decoded<Membership> =
    Codec.closed [ "capabilities"; "displayName"; "kind"; "principalId"; "revision" ] value
    |> Result.bind (fun () ->
        match Codec.text "principalId" value, Codec.text "kind" value, Codec.text "displayName" value, texts "capabilities" value, Codec.integer "revision" value with
        | Ok principalId, Ok kind, Ok displayName, Ok capabilities, Ok revision ->
            match kindOf kind, capabilities |> List.map capabilityOf with
            | None, _ -> Error $"'{kind}' is not a kind of principal"
            | Some _, found when found |> List.exists Option.isNone -> Error "a capability is not one this version knows"
            | Some _, _ when revision < 1 -> Error "'revision' must be at least 1"
            | Some kind, found ->
                let held = found |> List.choose id |> Set.ofList

                if held |> Set.exists (permitsKind kind >> not) then
                    Error "a person-only capability is held by a principal that is not a person"
                else
                    Ok
                        { Principal =
                            { PrincipalId = principalId
                              Kind = kind
                              DisplayName = displayName }
                          Capabilities = held
                          Revision = revision }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)

/// A stored member, checked: canonical record at the path its principal
/// names, readable schema, valid body.
let decode (stored: StoredObject) : Result<Membership, Diagnostic> =
    let where = RelativePath.render stored.Path

    match Layout.keyOf stored.Path with
    | Some found when found.Type = recordType ->
        Integrity.validate found schema Record.DefaultMaxBytes stored
        |> Result.mapError (Organization.describeIntegrity >> fun detail -> InvalidStoredRecord(where, detail))
        |> Result.bind (fun valid -> ofBody valid.Record.Body |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail)))
        |> Result.bind (fun membership ->
            if path membership.Principal.PrincipalId = Ok stored.Path then
                Ok membership
            else
                Error(InvalidStoredRecord(where, "the member is stored under another principal's path")))
    | _ -> Error(InvalidStoredRecord(where, "not a member record's path"))

/// The roster the stored members make, or every problem. A roster with an
/// unusable member is not used: authorization must never act on a guess.
let roster (organizationId: string) (stored: StoredObject list) : Result<Roster * Map<string, Revision>, Diagnostic list> =
    let decoded = stored |> List.map (fun s -> s, decode s)

    match decoded |> List.choose (fun (_, r) -> match r with Error d -> Some d | Ok _ -> None) with
    | [] ->
        let found = decoded |> List.choose (fun (s, r) -> match r with Ok m -> Some(m, s.Revision) | Error _ -> None)

        Ok(
            { OrganizationId = organizationId
              Members = found |> List.map (fun (m, _) -> m.Principal.PrincipalId, m) |> Map.ofList },
            found |> List.map (fun (m, revision) -> m.Principal.PrincipalId, revision) |> Map.ofList
        )
    | problems -> Error problems

/// The changes that store a roster change: one create or update per member
/// that changed, one delete per member removed. `revisions` are the stored
/// revisions as last read; a member changed since is a conflict.
let changes (revisions: Map<string, Revision>) (before: Roster) (after: Roster) : Result<Change list, Diagnostic list> =
    let written, removed = changed before after

    let writes =
        written
        |> List.map (fun membership ->
            path membership.Principal.PrincipalId
            |> Result.bind (fun p ->
                encode membership
                |> Result.map (fun content ->
                    match revisions.TryFind membership.Principal.PrincipalId with
                    | Some revision -> Change.Update(p, content, revision)
                    | None -> Change.Create(p, content))))

    let deletes =
        removed
        |> List.map (fun id ->
            match revisions.TryFind id with
            | Some revision -> path id |> Result.map (fun p -> Change.Delete(p, revision))
            | None -> Error(StorageOperationRefused $"'{id}' was never read, so it cannot be removed"))

    let all = writes @ deletes

    match all |> List.choose (function Error d -> Some d | Ok _ -> None) with
    | [] -> Ok(all |> List.choose Result.toOption)
    | problems -> Error problems
