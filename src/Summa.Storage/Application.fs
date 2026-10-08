/// Summa's application manifest (SUM0-007): one record at Summa's
/// application root, beside Arca's own manifest, naming the application,
/// its storage and schema versions, where organizations live, and which
/// Summa versions may use the data. Summa reads it instead of inferring its
/// structure by scanning (SUM0-033).
///
/// Pure.
module Summa.Storage.Application

open System
open Arca
open Summa.Storage.Diagnostics

/// The application id, which is also the folder Summa owns under the
/// configured base path (SUM-DATALOC-002, SUM0-006).
let id =
    match AppId.create "summa" with
    | Ok found -> found
    | Error error -> invalidOp ("internal: Summa's application id is invalid: " + LocationError.describe error)

/// The version of Summa that writes this layout.
[<Literal>]
let ApplicationVersion = "0.1.0"

type ApplicationManifest =
    { Name: string
      ApplicationId: string
      /// The version of Summa's storage layout (organization folders, record
      /// types and partitions).
      StorageVersion: int
      /// The current schema version of each Summa record type.
      RecordSchemas: Map<string, int>
      /// Where organizations live, relative to the application root.
      OrganizationsPath: string
      /// The oldest Summa version that may read and write this data.
      MinimumApplicationVersion: string
      CreatedAt: DateTimeOffset }

let private recordType =
    match RecordType.create "summa.application" with
    | Ok found -> found
    | Error _ -> invalidOp "internal: not a record type"

/// The record type of the application manifest.
let manifestType = recordType

let schema =
    { Type = manifestType
      OldestReadable = 1
      Current = 1 }

/// The manifest this Summa writes.
let current (recordSchemas: Map<string, int>) (createdAt: DateTimeOffset) =
    { Name = "Summa"
      ApplicationId = AppId.value id
      StorageVersion = Organization.StorageVersion
      RecordSchemas = recordSchemas
      OrganizationsPath = Namespace.DatasetsFolder
      MinimumApplicationVersion = ApplicationVersion
      CreatedAt = createdAt }

let private body (manifest: ApplicationManifest) =
    Json.objectOf
        [ "name", Json.String manifest.Name
          "applicationId", Json.String manifest.ApplicationId
          "storageVersion", Codec.number manifest.StorageVersion
          "recordSchemas", Json.objectOf (manifest.RecordSchemas |> Map.toList |> List.map (fun (k, v) -> k, Codec.number v))
          "organizationsPath", Json.String manifest.OrganizationsPath
          "minimumApplicationVersion", Json.String manifest.MinimumApplicationVersion
          "createdAt", Json.String(Codec.timestamp manifest.CreatedAt) ]

let private recordId =
    match RecordId.create "summa" with
    | Ok found -> found
    | Error _ -> invalidOp "internal: not a record id"

let private key =
    { Type = manifestType
      Partition = []
      Id = recordId }

/// `records/summa.application/summa.json` in Summa's application namespace.
let path =
    match Layout.recordPath key with
    | Ok found -> found
    | Error error -> invalidOp ("internal: " + LocationError.describe error)

/// The manifest's canonical stored text.
let encode (manifest: ApplicationManifest) : Result<string, Diagnostic> =
    { Id = recordId
      Type = manifestType
      SchemaVersion = schema.Current
      Mutability = Mutability.Mutable
      Body = body manifest }
    |> Record.encode Record.DefaultMaxBytes
    |> Result.mapError (fun _ -> StorageOperationRefused "the application manifest is too large")

let private ofBody (value: Json) : Result<ApplicationManifest, string> =
    Codec.closed
        [ "applicationId"
          "createdAt"
          "minimumApplicationVersion"
          "name"
          "organizationsPath"
          "recordSchemas"
          "storageVersion" ]
        value
    |> Result.bind (fun () ->
        let schemas =
            Codec.field "recordSchemas" value
            |> Result.bind (function
                | Json.Object members ->
                    members
                    |> Codec.traverse (fun (k, v) -> Codec.integer k (Json.objectOf [ k, v ]) |> Result.map (fun n -> k, n))
                    |> Result.map Map.ofList
                | _ -> Error "'recordSchemas' is not an object")

        match
            Codec.text "name" value,
            Codec.text "applicationId" value,
            Codec.integer "storageVersion" value,
            schemas,
            Codec.text "organizationsPath" value,
            Codec.text "minimumApplicationVersion" value,
            Codec.instant "createdAt" value
        with
        | Ok name, Ok app, Ok storage, Ok schemas, Ok orgs, Ok minimum, Ok created ->
            Ok
                { Name = name
                  ApplicationId = app
                  StorageVersion = storage
                  RecordSchemas = schemas
                  OrganizationsPath = orgs
                  MinimumApplicationVersion = minimum
                  CreatedAt = created }
        | Error e, _, _, _, _, _, _
        | _, Error e, _, _, _, _, _
        | _, _, Error e, _, _, _, _
        | _, _, _, Error e, _, _, _
        | _, _, _, _, Error e, _, _
        | _, _, _, _, _, Error e, _
        | _, _, _, _, _, _, Error e -> Error e)

/// The stored application manifest, checked against this Summa: Summa's,
/// at the storage version this Summa reads, with organizations where this
/// Summa looks for them.
let decode (stored: StoredObject) : Result<ApplicationManifest, Diagnostic> =
    let where = RelativePath.render stored.Path

    Integrity.validate key schema Record.DefaultMaxBytes stored
    |> Result.mapError (Organization.describeIntegrity >> fun detail -> InvalidStoredRecord(where, detail))
    |> Result.bind (fun valid -> ofBody valid.Record.Body |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail)))
    |> Result.bind (fun manifest ->
        if manifest.ApplicationId <> AppId.value id then
            Error(NamespaceUnusable(where, $"the folder belongs to '{manifest.ApplicationId}'"))
        elif manifest.StorageVersion <> Organization.StorageVersion then
            Error(UnsupportedStorageVersion(manifest.StorageVersion, Organization.StorageVersion))
        elif manifest.OrganizationsPath <> Namespace.DatasetsFolder then
            Error(NamespaceUnusable(where, $"organizations are kept in '{manifest.OrganizationsPath}'"))
        else
            Ok manifest)
