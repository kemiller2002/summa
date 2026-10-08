/// Schema compatibility (SUM0-031, SUM3-009): an organization's manifests
/// declare its storage version and the schema version of every record type;
/// this Summa reads and writes only what it supports, opens older data
/// read-only, and refuses newer data. Nothing is silently rewritten into a
/// newer structure: that needs an explicit migration.
///
/// Pure.
module Summa.Storage.Compatibility

open Arca
open Summa.Storage.Diagnostics

/// How this Summa may use an organization's data.
type Access =
    | ReadWrite
    /// Older schemas this Summa still reads: no command may write until an
    /// explicit migration runs.
    | ReadOnly of reasons: Diagnostic list
    | Refused of reasons: Diagnostic list

/// The access the organization's manifests allow (SUM0-031, SUM3-009): the
/// storage version, then each record type's schema version.
let access (supported: SchemaSupport list) (organization: Organization.OrganizationManifest) (manifest: Manifest) : Access =
    let storage =
        if organization.StorageVersion <> Organization.StorageVersion then
            [ UnsupportedStorageVersion(organization.StorageVersion, Organization.StorageVersion) ]
        else
            []

    let judged =
        manifest.RecordSchemas
        |> Map.toList
        |> List.map (fun (name, version) ->
            match supported |> List.tryFind (fun s -> RecordType.value s.Type = name) with
            | None -> Error(IncompatibleSchema(name, "this Summa does not know the record type"))
            | Some support ->
                match SchemaSupport.access support version with
                | SchemaAccess.ReadWrite -> Ok None
                | SchemaAccess.ReadOnly -> Ok(Some(IncompatibleSchema(name, $"schema {version} is older than {support.Current}; migrate before writing")))
                | SchemaAccess.UnsupportedFuture(found, newest) -> Error(IncompatibleSchema(name, $"schema {found} is newer than {newest}"))
                | SchemaAccess.UnsupportedPast(found, oldest) -> Error(IncompatibleSchema(name, $"schema {found} is older than {oldest}, which this Summa no longer reads")))

    let refused = storage @ (judged |> List.choose (function Error d -> Some d | Ok _ -> None))
    let readOnly = judged |> List.choose (function Ok(Some d) -> Some d | _ -> None)

    match refused, readOnly with
    | [], [] -> ReadWrite
    | [], reasons -> ReadOnly reasons
    | reasons, _ -> Refused reasons

