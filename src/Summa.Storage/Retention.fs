/// Deletion and retention (SUM3-034, SUM3-035): what Summa keeps and for how
/// long. Financial records are not physically deleted: a mistake is
/// corrected with a new record (a void, a reversal, a credit memo, a
/// write-off) and an entity that is no longer used becomes inactive. Only
/// drafts, which have no financial effect, may be deleted, and their history
/// stays in the repository's Git history. Tax and business record rules may
/// later set limits; until then nothing financial expires.
///
/// Pure.
module Summa.Storage.Retention

open Arca

type Retention =
    /// Kept for as long as the organization's data exists.
    | Permanent of reason: string
    /// Kept until it has served its purpose; deleting it changes no
    /// financial fact, and Git keeps its history.
    | UntilResolved of reason: string

/// The retention of a financial record type.
let ofType (recordType: RecordType) : Retention =
    match RecordType.value recordType with
    | "summa.entry" -> Permanent "the books of account"
    | "summa.invoice"
    | "summa.invoice-void"
    | "summa.credit-memo" -> Permanent "issued documents and their corrections"
    | "summa.payment"
    | "summa.allocation"
    | "summa.payment-reversal"
    | "summa.refund"
    | "summa.application"
    | "summa.credit"
    | "summa.deposit"
    | "summa.write-off" -> Permanent "money received, applied, returned or written off"
    | "summa.audit" -> Permanent "the audit trail"
    | "summa.time"
    | "summa.time-withdrawal"
    | "summa.expense"
    | "summa.artifact"
    | "summa.delivery"
    | "summa.billing-review" -> Permanent "supporting references"
    | "summa.draft" -> UntilResolved "a draft becomes an invoice or is given up"
    | _ -> Permanent "organization data: inactive, never deleted"

/// What a deployment keeps outside the records: backups and exports.
let backups = "sealed backups: 14 daily, 8 weekly, 12 monthly (Backup.defaultRetention), each verified before it is trusted"
let exports = "accounting exports are kept by the person who made them; each names the data version it was made from, so it can be made again from the same books"
