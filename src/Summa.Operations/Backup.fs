/// Backups of an organization's folder (SUM3-006, SUM3-024): Arca's
/// canonical export (every object, byte for byte, each with its SHA-256, as
/// of one change token), sealed with AES-256-GCM so the backup is encrypted
/// wherever it is kept, and verified before it is trusted. Keys belong to
/// the operator's key store; nothing here creates, stores or logs one.
module Summa.Operations.Backup

open System
open System.Security.Cryptography
open System.Text
open Arca

/// A sealed backup: the format, the nonce, the authentication tag and the
/// encrypted canonical export, as text safe to store anywhere.
type Sealed = private Sealed of string

module Sealed =
    let text (Sealed value) = value
    let ofText (value: string) = Sealed value

[<Literal>]
let private Prefix = "summa-backup-v1"

/// Why a backup could not be taken, opened or trusted.
type BackupError =
    | ExportFailed of ExportError
    | KeyInvalid
    | NotABackup
    /// The ciphertext or its tag does not verify: wrong key or tampering.
    | AuthenticationFailed
    | ArchiveCorrupt of ExportError

/// A 256-bit key.
type BackupKey = private BackupKey of byte array

module BackupKey =
    let create (bytes: byte array) =
        if bytes.Length <> 32 then Error KeyInvalid else Ok(BackupKey(Array.copy bytes))

/// Encrypts an export. `nonce` must be 12 fresh random bytes for every backup
/// (the caller draws it, so this function stays deterministic and testable).
let seal (BackupKey key) (nonce: byte array) (archive: ExportArchive) : Result<Sealed, BackupError> =
    if nonce.Length <> 12 then
        Error KeyInvalid
    else
        let plain = Encoding.UTF8.GetBytes(Export.encode archive)
        let cipher = Array.zeroCreate<byte> plain.Length
        let tag = Array.zeroCreate<byte> 16
        use aes = new AesGcm(key, 16)
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes Prefix)
        Ok(Sealed(String.Join(".", [ Prefix; Convert.ToBase64String nonce; Convert.ToBase64String tag; Convert.ToBase64String cipher ])))

/// Decrypts and verifies a backup: the tag must verify under the key, and
/// every object must still have the hash the export recorded.
let unseal (BackupKey key) (Sealed text) : Result<ExportArchive, BackupError> =
    match text.Split('.') with
    | [| prefix; nonce; tag; cipher |] when prefix = Prefix ->
        try
            let nonce = Convert.FromBase64String nonce
            let tag = Convert.FromBase64String tag
            let cipher = Convert.FromBase64String cipher
            let plain = Array.zeroCreate<byte> cipher.Length
            use aes = new AesGcm(key, 16)
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes Prefix)
            Export.decode (Encoding.UTF8.GetString plain) |> Result.mapError ArchiveCorrupt
        with
        | :? AuthenticationTagMismatchException
        | :? CryptographicException -> Error AuthenticationFailed
        | :? FormatException -> Error NotABackup
    | _ -> Error NotABackup

/// Takes a backup of a namespace through a provider.
let take (provider: StorageProvider) (ns: Namespace) (key: BackupKey) (nonce: byte array) : Async<Result<Sealed, BackupError>> =
    async {
        match! Export.take provider ns 3 with
        | Error error -> return Error(ExportFailed error)
        | Ok archive -> return seal key nonce archive
    }

/// The operation that restores an archive into an empty namespace at the
/// same location: one commit that creates every object exactly as exported.
let restoreOperation (ns: Namespace) (metadata: OperationMetadata) (archive: ExportArchive) =
    archive.Objects
    |> List.map (fun item -> RelativePath.parse item.Path |> Result.map (fun path -> Change.Create(path, item.Content)))
    |> List.fold (fun state item -> state |> Result.bind (fun acc -> item |> Result.map (fun c -> acc @ [ c ]))) (Ok [])
    |> Result.mapError LocationError.describe
    |> Result.bind (fun changes -> Operation.create ns metadata changes |> Result.mapError (fun error -> $"%A{error}"))

// ---- Retention ------------------------------------------------------------------

/// How long backups are kept: every backup of the last `Daily` days, the
/// newest of each of the last `Weekly` weeks and of the last `Monthly` months.
type RetentionPolicy = { Daily: int; Weekly: int; Monthly: int }

/// The default: 14 days, 8 weeks, 12 months.
let defaultRetention = { Daily = 14; Weekly = 8; Monthly = 12 }

/// The backups (id, taken at) to keep under the policy as of `now`; the rest may be deleted.
let retain (policy: RetentionPolicy) (now: DateTimeOffset) (backups: (string * DateTimeOffset) list) : Set<string> =
    let newestPer (period: DateTimeOffset -> string) (within: TimeSpan) =
        backups
        |> List.filter (fun (_, at) -> now - at <= within)
        |> List.groupBy (snd >> period)
        |> List.map (fun (_, group) -> group |> List.maxBy snd |> fst)

    let week (at: DateTimeOffset) =
        $"{Globalization.ISOWeek.GetYear at.UtcDateTime}-{Globalization.ISOWeek.GetWeekOfYear at.UtcDateTime}"

    [ yield! backups |> List.filter (fun (_, at) -> now - at <= TimeSpan.FromDays(float policy.Daily)) |> List.map fst
      yield! newestPer week (TimeSpan.FromDays(float (7 * policy.Weekly)))
      yield! newestPer (fun at -> $"{at.UtcDateTime.Year}-{at.UtcDateTime.Month}") (TimeSpan.FromDays(float (31 * policy.Monthly))) ]
    |> Set.ofList
