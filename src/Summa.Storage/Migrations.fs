/// Explicit, versioned migrations of Summa's stored records (SUM0-032,
/// SUM3-010, DF-SUMMA-2026-0012). Nothing older is ever silently read as
/// current: a record at an older schema is migrated by a function named for
/// the version it produces, checked, and written as its own commit.
///
/// Pure.
module Summa.Storage.Migrations

open Arca
open Summa.Ledger.Ledger
open Summa.Storage.Diagnostics

/// The accounts schema 2 of the organization manifest adds, and what each
/// must be in the chart: the default chart's customer credits (2100) and
/// customer deposits (2200) liabilities, and bad debt (6500) expense.
let private addedAccounts =
    [ "customerCreditsAccount", "2100", Liability
      "customerDepositsAccount", "2200", Liability
      "badDebtAccount", "6500", Expense ]

/// Organization manifest schema 1 -> 2: names the customer credits, customer
/// deposits and bad-debt accounts, from the books' own chart. Each must
/// exist in the chart with the right type, or the migration refuses: it
/// never invents an account, and a chart that differs needs them chosen.
let organizationV2 (ledger: Ledger) (body: Json) : Result<Json, Diagnostic> =
    let account code = ledger.Accounts |> Map.tryPick (fun _ a -> if a.Code = code then Some a else None)

    let missing =
        addedAccounts
        |> List.choose (fun (_, code, kind) ->
            match account code with
            | Some a when a.Type = kind -> None
            | Some a -> Some $"account {code} is {a.Type}, not {kind}"
            | None -> Some $"there is no account {code}")

    let reasons = String.concat "; " missing

    match body, missing with
    | _, _ :: _ -> Error(MigrationUnsafe $"the organization manifest cannot be migrated to schema 2: {reasons}; name the credit, deposit and bad-debt accounts explicitly")
    | Json.Object members, [] ->
        match members |> List.tryFind (fst >> (=) "accounting") with
        | Some(_, Json.Object accounting) when accounting |> List.exists (fun (key, _) -> addedAccounts |> List.exists (fun (name, _, _) -> name = key)) ->
            Error(MigrationUnsafe "the organization manifest already names its credit, deposit or bad-debt accounts; it is not schema 1")
        | Some(_, Json.Object accounting) ->
            let added = addedAccounts |> List.map (fun (name, code, _) -> name, Json.String code)

            Ok(Json.Object(members |> List.map (fun (key, value) -> if key = "accounting" then key, Json.Object(accounting @ added) else key, value)))
        | _ -> Error(MigrationUnsafe "the organization manifest has no accounting section")
    | _ -> Error(MigrationUnsafe "the organization manifest is not an object")

/// A stored manifest at the current schema: as it is, or migrated from an
/// older one with the books' ledger. The flag says whether it was migrated,
/// so the caller writes it back.
let currentManifest (ledger: Ledger) (stored: Organization.StoredManifest) : Result<Organization.OrganizationManifest * bool, Diagnostic> =
    match stored with
    | Organization.CurrentManifest manifest -> Ok(manifest, false)
    | Organization.NeedsMigration(1, body) ->
        organizationV2 ledger body
        |> Result.bind (fun migrated -> Organization.ofBody migrated |> Result.mapError InvalidOrganizationManifest)
        |> Result.map (fun manifest -> manifest, true)
    | Organization.NeedsMigration(version, _) -> Error(IncompatibleSchema(RecordType.value Organization.manifestType, $"no migration from schema {version}"))

/// The commit that migrates an organization's folder on its store: the
/// manifest record rewritten at the current schema, and the folder's Arca
/// manifest declaring it, both conditioned on the revisions read. It moves
/// no financial record, so the books are the same books before and after,
/// which `Verification.checkMigration` confirms.
let organizationOperation
    (ns: Namespace)
    (metadata: OperationMetadata)
    (manifest: Organization.OrganizationManifest)
    (manifestRevision: Revision)
    (folder: Manifest)
    (folderRevision: Revision)
    (books: Summa.Ledger.Payments.Receivables)
    : Result<Operation, Diagnostic list> =
    match Verification.checkMigration { Lineage = Map.empty; ChangesBalances = false } books books with
    | _ :: _ as unsafe -> Error unsafe
    | [] ->
        match Organization.encode manifest, Organization.path manifest.OrganizationId, Layout.manifestPath with
        | Error problems, _, _ -> Error problems
        | _, Error problem, _ -> Error [ problem ]
        | _, _, Error _ -> Error [ InvalidOrganizationManifest "the folder manifest path" ]
        | Ok content, Ok path, Ok arcaPath ->
            let declared =
                { folder with RecordSchemas = folder.RecordSchemas.Add(RecordType.value Organization.manifestType, Organization.schema.Current) }

            Operation.create
                ns
                metadata
                [ Change.Update(path, content, manifestRevision)
                  Change.Update(arcaPath, Manifest.encode declared, folderRevision) ]
            |> Result.mapError (fun error -> [ StorageOperationRefused $"%A{error}" ])

/// What migrating an organization's folder did.
type Migrated =
    /// The folder was already current: nothing was written.
    | AlreadyCurrent
    /// The migration was committed.
    | Committed of CommitReceipt

/// Migrates an organization's folder on its store (SUM0-032): an
/// administrator's explicit act (`ManageSettings`), on books that pass every
/// integrity check, as one commit conditioned on what was read. Books stored
/// on GitHub are never read as current until this has run: until then the
/// folder is read-only (`Compatibility.storedAccess`).
let migrate (provider: StorageProvider) (gates: Set<Summa.Access.Access.Capability>) (ns: Namespace) (actor: Summa.Access.Actor.Actor) (key: string) : Async<Result<Migrated, Commands.CommandFailure<unit>>> =
    let organizationId = ns.Dataset |> Option.map DatasetId.value |> Option.defaultValue ""

    async {
        match Layout.manifestPath, Organization.path organizationId with
        | Ok arcaPath, Ok organizationPath ->
            let! arca = provider.Read ns arcaPath
            let! organization = provider.Read ns organizationPath
            let! objects = Commands.readAll provider ns

            match arca, organization, objects with
            | Error failure, _, _
            | _, Error failure, _ -> return Error(Commands.StorageFailed failure)
            | _, _, Error failure -> return Error failure
            | Ok ReadOutcome.Absent, _, _
            | _, Ok ReadOutcome.Absent, _ -> return Error(Commands.Untrustworthy [ NamespaceNotInitialized(RelativePath.render ns.Root) ])
            | Ok(ReadOutcome.Found arcaStored as arcaFound), Ok(ReadOutcome.Found organizationFound), Ok objects ->
                let members, financial = objects |> List.partition (fun o -> match Layout.keyOf o.Path with Some k -> k.Type = MemberRecord.recordType | None -> false)

                match Storage.openNamespace ns arcaFound, Organization.decodeStored organizationId organizationFound, MemberRecord.roster organizationId members with
                | Error problems, _, _ -> return Error(Commands.Untrustworthy problems)
                | _, Error problem, _ -> return Error(Commands.Untrustworthy [ problem ])
                | _, _, Error problems -> return Error(Commands.Untrustworthy problems)
                | Ok folder, Ok stored, Ok(roster, _) ->
                    match Summa.Access.Access.decide gates roster organizationId actor.ActorId Summa.Access.Access.ManageSettings with
                    | Summa.Access.Access.Refused refusal -> return Error(Commands.NotAuthorized refusal)
                    | Summa.Access.Access.NeedsHumanApproval -> return Error(Commands.NeedsApproval Summa.Access.Access.ManageSettings)
                    | Summa.Access.Access.Allowed ->
                        let loaded = FinancialRecords.load financial

                        if not loaded.Problems.IsEmpty then
                            return Error(Commands.Untrustworthy loaded.Problems)
                        else
                            match currentManifest loaded.State.Books.Ledger stored with
                            | Error problem -> return Error(Commands.Untrustworthy [ problem ])
                            | Ok(_, false) -> return Ok AlreadyCurrent
                            | Ok(manifest, true) ->
                                let operation =
                                    Commands.metadata actor $"migrate organization {organizationId} manifest to schema {Organization.schema.Current}" key
                                    |> Result.bind (fun meta -> organizationOperation ns meta manifest organizationFound.Revision folder arcaStored.Revision loaded.State)

                                match operation with
                                | Error problems -> return Error(Commands.Unstorable problems)
                                | Ok operation ->
                                    match! provider.Commit operation with
                                    | Ok receipt -> return Ok(Committed receipt)
                                    | Error failure -> return Error(Commands.StorageFailed failure)
        | _ -> return Error(Commands.Untrustworthy [ InvalidOrganizationId organizationId ])
    }
