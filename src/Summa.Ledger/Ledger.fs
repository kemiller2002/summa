/// The double-entry general ledger (v0.1 §1, §2, §16, §18, §19).
///
/// The ledger is the accounting source of truth. Posted entries are
/// immutable and never deleted; corrections are reversals. Posting checks
/// balance, accounts and the accounting period, and is idempotent by key, so
/// a retried command never posts twice. Every material operation appends an
/// audit record that is never edited.
module Summa.Ledger.Ledger

open System
open Summa.Ledger.Money

type AccountType =
    | Asset
    | Liability
    | Equity
    | Revenue
    | Expense

type NormalBalance =
    | DebitNormal
    | CreditNormal

let normalBalance =
    function
    | Asset
    | Expense -> DebitNormal
    | Liability
    | Equity
    | Revenue -> CreditNormal

type Account =
    { Id: string
      Code: string
      Name: string
      Type: AccountType
      Active: bool }

/// A line is a debit or a credit, never both (the type cannot say both).
type Side =
    | Debit of Money
    | Credit of Money

/// Analysis dimensions on a line, not separate accounts (§16).
type Dimensions =
    { Client: string option
      Project: string option
      Engagement: string option
      WorkItem: string option
      Product: string option }

let noDimensions =
    { Client = None
      Project = None
      Engagement = None
      WorkItem = None
      Product = None }

type Line =
    { AccountId: string
      Side: Side
      Memo: string option
      Dimensions: Dimensions }

type EntryDraft =
    { Date: DateOnly
      Description: string
      Lines: Line list
      /// What produced the entry: "manual", "invoice:EF-2026-0001", ...
      Source: string }

type EntryState =
    | Posted
    | Reversed of by: string

type PostedEntry =
    { Id: string
      Date: DateOnly
      Description: string
      Lines: Line list
      Source: string
      State: EntryState
      /// The entry this one reverses, if it is a reversal.
      Reverses: string option
      PostedAt: DateTimeOffset }

type PeriodState =
    | Open
    | Closed
    | Locked

type AuditRecord =
    { Who: string
      What: string
      When: DateTimeOffset
      Source: string
      CorrelationId: string option
      Subject: string }

/// Who, when and through what a command arrives (§18).
type Context =
    { Who: string
      When: DateTimeOffset
      Source: string
      CorrelationId: string option }

type Problem =
    | Unbalanced of debits: Money * credits: Money
    | TooFewLines
    | NonPositiveAmount of accountId: string
    | UnknownAccount of accountId: string
    | InactiveAccount of accountId: string
    | MixedCurrencies
    | PeriodNotOpen of year: int * month: int
    | UnknownEntry of entryId: string
    | AlreadyReversed of entryId: string
    | DuplicateAccountCode of code: string
    | ReopenRequiresPrivilege
    | IdempotencyKeyReused of key: string

type Ledger =
    { Accounts: Map<string, Account>
      /// Monthly periods by (year, month); a missing period is open.
      Periods: Map<int * int, PeriodState>
      Entries: Map<string, PostedEntry>
      /// Entries in posting order (ids), for reports and exports.
      Journal: string list
      /// Idempotency key -> the entry it posted.
      Keys: Map<string, string>
      Audit: AuditRecord list }

let empty =
    { Accounts = Map.empty
      Periods = Map.empty
      Entries = Map.empty
      Journal = []
      Keys = Map.empty
      Audit = [] }

let private audit (context: Context) (what: string) (subject: string) (ledger: Ledger) =
    { ledger with
        Audit =
            ledger.Audit
            @ [ { Who = context.Who
                  What = what
                  When = context.When
                  Source = context.Source
                  CorrelationId = context.CorrelationId
                  Subject = subject } ] }

let addAccount (context: Context) (account: Account) (ledger: Ledger) =
    if ledger.Accounts |> Map.exists (fun id a -> a.Code = account.Code && id <> account.Id) then
        Error [ DuplicateAccountCode account.Code ]
    else
        Ok({ ledger with Accounts = ledger.Accounts.Add(account.Id, account) } |> audit context "account-saved" account.Id)

let periodState (ledger: Ledger) (date: DateOnly) =
    ledger.Periods.TryFind(date.Year, date.Month) |> Option.defaultValue Open

let private setPeriod (context: Context) (what: string) (year: int, month: int) state (ledger: Ledger) =
    { ledger with Periods = ledger.Periods.Add((year, month), state) } |> audit context what $"{year:D4}-{month:D2}"

/// Closing or locking never changes existing entries (§2).
let closePeriod context period ledger = setPeriod context "period-closed" period Closed ledger
let lockPeriod context period ledger = setPeriod context "period-locked" period Locked ledger

/// Reopening is an explicit privileged operation (§2).
let reopenPeriod (context: Context) (privileged: bool) period ledger =
    if privileged then Ok(setPeriod context "period-reopened" period Open ledger) else Error [ ReopenRequiresPrivilege ]

let private amount =
    function
    | Debit m
    | Credit m -> m

/// Totals of a set of lines: (debits, credits).
let totals (currency: string) (lines: Line list) =
    let debits = lines |> List.choose (fun l -> match l.Side with Debit m -> Some m | _ -> None) |> sum currency
    let credits = lines |> List.choose (fun l -> match l.Side with Credit m -> Some m | _ -> None) |> sum currency
    debits, credits

/// Every reason a draft cannot be posted, in a stable order.
let validate (ledger: Ledger) (draft: EntryDraft) : Problem list =
    let currencies = draft.Lines |> List.map (fun l -> (amount l.Side).Currency) |> List.distinct

    [ if draft.Lines.Length < 2 then TooFewLines
      if currencies.Length > 1 then MixedCurrencies
      for line in draft.Lines do
          if not (isPositive (amount line.Side)) then NonPositiveAmount line.AccountId

          match ledger.Accounts.TryFind line.AccountId with
          | None -> UnknownAccount line.AccountId
          | Some a when not a.Active -> InactiveAccount line.AccountId
          | Some _ -> ()
      if currencies.Length = 1 then
          let debits, credits = totals currencies.Head draft.Lines
          if debits <> credits then Unbalanced(debits, credits)
      match periodState ledger draft.Date with
      | Open -> ()
      | Closed
      | Locked -> PeriodNotOpen(draft.Date.Year, draft.Date.Month) ]

/// Whether a draft is in the "Balanced" state: postable apart from period.
let isBalanced (ledger: Ledger) (draft: EntryDraft) =
    validate ledger draft |> List.forall (function PeriodNotOpen _ -> true | _ -> false)

/// Posts a draft under an idempotency key. The same key with the same
/// draft returns the entry already posted and changes nothing; the same key
/// with a different draft is refused.
let post (context: Context) (key: string) (entryId: string) (draft: EntryDraft) (ledger: Ledger) : Result<Ledger * PostedEntry, Problem list> =
    match ledger.Keys.TryFind key |> Option.map (fun id -> ledger.Entries[id]) with
    | Some existing when existing.Date = draft.Date && existing.Lines = draft.Lines && existing.Source = draft.Source -> Ok(ledger, existing)
    | Some _ -> Error [ IdempotencyKeyReused key ]
    | None ->
        match validate ledger draft with
        | (_ :: _) as problems -> Error problems
        | [] ->
            let entry =
                { Id = entryId
                  Date = draft.Date
                  Description = draft.Description
                  Lines = draft.Lines
                  Source = draft.Source
                  State = Posted
                  Reverses = None
                  PostedAt = context.When }

            Ok(
                { ledger with
                    Entries = ledger.Entries.Add(entryId, entry)
                    Journal = ledger.Journal @ [ entryId ]
                    Keys = ledger.Keys.Add(key, entryId) }
                |> audit context "journal-entry-posted" entryId,
                entry
            )

/// Corrects a posted entry by posting its mirror image on `date`; the
/// original stays, marked Reversed (§1.3).
let reverse (context: Context) (entryId: string) (reversalId: string) (date: DateOnly) (ledger: Ledger) =
    match ledger.Entries.TryFind entryId with
    | None -> Error [ UnknownEntry entryId ]
    | Some { State = Reversed _ } -> Error [ AlreadyReversed entryId ]
    | Some original ->
        let mirrored =
            original.Lines
            |> List.map (fun l ->
                { l with
                    Side =
                        match l.Side with
                        | Debit m -> Credit m
                        | Credit m -> Debit m })

        let draft =
            { Date = date
              Description = $"Reversal of {entryId}: {original.Description}"
              Lines = mirrored
              Source = $"reversal:{entryId}" }

        post context $"reverse:{entryId}" reversalId draft ledger
        |> Result.map (fun (next, reversal) ->
            let reversal = { reversal with Reverses = Some entryId }

            { next with
                Entries =
                    next.Entries
                    |> Map.add reversalId reversal
                    |> Map.add entryId { original with State = Reversed reversalId } }
            |> audit context "journal-entry-reversed" entryId,
            reversal)

/// Signed balance of an account in its normal direction, from posted lines
/// on or before `asOf` (reversed entries and their reversals both count, so
/// they cancel).
let balance (ledger: Ledger) (currency: string) (asOf: DateOnly) (accountId: string) =
    let account = ledger.Accounts[accountId]

    ledger.Journal
    |> List.map (fun id -> ledger.Entries[id])
    |> List.filter (fun e -> e.Date <= asOf)
    |> List.collect _.Lines
    |> List.filter (fun l -> l.AccountId = accountId)
    |> List.fold
        (fun total line ->
            match normalBalance account.Type, line.Side with
            | DebitNormal, Debit m
            | CreditNormal, Credit m -> add total m
            | DebitNormal, Credit m
            | CreditNormal, Debit m -> subtract total m)
        (zero currency)
