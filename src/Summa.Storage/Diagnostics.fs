/// Every reason Summa's storage refuses something, with a stable code that
/// tests, records and the interface can name.
module Summa.Storage.Diagnostics

type Diagnostic =
    // Configuration -------------------------------------------------------
    | InvalidDeploymentConfig of detail: string
    | InvalidDataLocation of detail: string
    | MissingField of field: string
    // Organizations -------------------------------------------------------
    | InvalidOrganizationId of id: string
    | InvalidSlug of slug: string
    | InvalidCurrency of code: string
    | InvalidOrganizationManifest of detail: string
    | UnsupportedStorageVersion of found: int * supported: int
    | UnknownOrganization of id: string
    // Storage -------------------------------------------------------------
    | PublicProductionRepository
    | OverrideWithoutReason
    | NamespaceNotInitialized of root: string
    | NamespaceUnusable of root: string * detail: string
    | InvalidStoredRecord of path: string * detail: string
    | StorageOperationRefused of detail: string
    // Integrity -----------------------------------------------------------
    | InvariantViolated of rule: string * subject: string * detail: string
    | EditedOutsideSumma of path: string
    | ImmutableRecordChanged of path: string
    | IncompatibleSchema of recordType: string * detail: string
    | MigrationUnsafe of detail: string

/// The diagnostic's stable code.
let code =
    function
    | InvalidDeploymentConfig _ -> "SUMMA.STORAGE.INVALID_CONFIGURATION"
    | InvalidDataLocation _ -> "SUMMA.STORAGE.INVALID_LOCATION"
    | MissingField _ -> "SUMMA.STORAGE.MISSING_FIELD"
    | InvalidOrganizationId _ -> "SUMMA.ORGANIZATION.INVALID_ID"
    | InvalidSlug _ -> "SUMMA.ORGANIZATION.INVALID_SLUG"
    | InvalidCurrency _ -> "SUMMA.ORGANIZATION.INVALID_CURRENCY"
    | InvalidOrganizationManifest _ -> "SUMMA.ORGANIZATION.INVALID_MANIFEST"
    | UnsupportedStorageVersion _ -> "SUMMA.STORAGE.UNSUPPORTED_VERSION"
    | UnknownOrganization _ -> "SUMMA.ORGANIZATION.UNKNOWN"
    | PublicProductionRepository -> "SUMMA.STORAGE.PUBLIC_PRODUCTION_REPOSITORY"
    | OverrideWithoutReason -> "SUMMA.STORAGE.OVERRIDE_WITHOUT_REASON"
    | NamespaceNotInitialized _ -> "SUMMA.STORAGE.NOT_INITIALIZED"
    | NamespaceUnusable _ -> "SUMMA.STORAGE.NAMESPACE_UNUSABLE"
    | InvalidStoredRecord _ -> "SUMMA.STORAGE.INVALID_RECORD"
    | StorageOperationRefused _ -> "SUMMA.STORAGE.OPERATION_REFUSED"
    | InvariantViolated _ -> "SUMMA.INTEGRITY.INVARIANT_VIOLATED"
    | EditedOutsideSumma _ -> "SUMMA.INTEGRITY.EDITED_OUTSIDE"
    | ImmutableRecordChanged _ -> "SUMMA.INTEGRITY.IMMUTABLE_CHANGED"
    | IncompatibleSchema _ -> "SUMMA.INTEGRITY.INCOMPATIBLE_SCHEMA"
    | MigrationUnsafe _ -> "SUMMA.INTEGRITY.MIGRATION_UNSAFE"

/// One sentence for people.
let describe =
    function
    | InvalidDeploymentConfig detail -> $"The deployment configuration is not usable: {detail}."
    | InvalidDataLocation detail -> $"The data location is not valid: {detail}."
    | MissingField field -> $"'{field}' is required."
    | InvalidOrganizationId id -> $"'{id}' is not a valid organization id."
    | InvalidSlug slug -> $"'{slug}' is not a valid slug: lower-case letters, digits and hyphens, starting with a letter."
    | InvalidCurrency code -> $"'{code}' is not a three-letter currency code."
    | InvalidOrganizationManifest detail -> $"The organization manifest is not valid: {detail}."
    | UnsupportedStorageVersion(found, supported) -> $"Storage version {found} is not the supported {supported}."
    | UnknownOrganization id -> $"'{id}' is not an organization this deployment serves."
    | PublicProductionRepository -> "Production data is never initialized in a public repository."
    | OverrideWithoutReason -> "A decision to use a public repository for production needs a reason."
    | NamespaceNotInitialized root -> $"'{root}' is not initialized."
    | NamespaceUnusable(root, detail) -> $"'{root}' cannot be used: {detail}."
    | InvalidStoredRecord(path, detail) -> $"'{path}' is not a valid record: {detail}."
    | StorageOperationRefused detail -> $"The change was refused: {detail}."
    | InvariantViolated(rule, subject, detail) -> $"Integrity failure ({rule}) in {subject}: {detail}."
    | EditedOutsideSumma path -> $"'{path}' was last changed outside Summa; it is validated, not trusted."
    | ImmutableRecordChanged path -> $"'{path}' is a posted record that was changed after it was written."
    | IncompatibleSchema(recordType, detail) -> $"{recordType}: {detail}."
    | MigrationUnsafe detail -> $"The migration is not safe: {detail}."
