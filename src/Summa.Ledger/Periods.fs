/// Opening balances, CPA adjusting entries, year-end closing and the
/// cash-basis projection (v0.2 §21-24).
///
/// Every journal entry says what kind it is through its source:
/// - `opening-balance:<date>`;
/// - `adjustment:<kind>[:<reference>]`;
/// - `year-end-close:<year>`;
/// - everything else is operational.
///
/// The kind is derived from the entry, so nothing extra is stored and old
/// entries keep their meaning. The ledger stays the one accrual ledger.
/// Cash basis is a projection over it, never a second set of books
/// (v0.2 §24).
module Summa.Ledger.Periods

open System
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Reports

/// What a CPA adjusting entry is for (v0.2 §22).
type AdjustingKind =
    | Depreciation
    | Accrual
    | Prepaid
    | TaxAdjustment
    | Reclassification
    | OtherAdjusting

let private adjustingNames =
    [ Depreciation, "depreciation"
      Accrual, "accrual"
      Prepaid, "prepaid"
      TaxAdjustment, "tax"
      Reclassification, "reclassification"
      OtherAdjusting, "other" ]

let adjustingName (kind: AdjustingKind) = adjustingNames |> List.find (fun (k, _) -> k = kind) |> snd

type EntryKind =
    | Operational
    | OpeningBalance of migrationDate: DateOnly
    | Adjusting of kind: AdjustingKind * reference: string option
    | YearEndClose of year: int
    | Reversal of original: string

[<Literal>]
let OpeningSource = "opening-balance:"

[<Literal>]
let AdjustingSource = "adjustment:"

/// The kind of a posted entry, from its source.
let kindOf (entry: PostedEntry) =
    let after (prefix: string) = entry.Source.Substring prefix.Length

    match entry.Reverses with
    | Some original -> Reversal original
    | None when entry.Source.StartsWith OpeningSource ->
        match DateOnly.TryParseExact(after OpeningSource, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
        | true, date -> OpeningBalance date
        | _ -> Operational
    | None when entry.Source.StartsWith AdjustingSource ->
        let parts = (after AdjustingSource).Split(':', 2)

        match adjustingNames |> List.tryFind (fun (_, n) -> n = parts[0]) with
        | Some(kind, _) -> Adjusting(kind, (if parts.Length = 2 then Some parts[1] else None))
        | None -> Operational
    | None when isClosingEntry entry ->
        match Int32.TryParse(entry.Source.Substring ClosingSource.Length) with
        | true, year -> YearEndClose year
        | _ -> Operational
    | None -> Operational

/// Who posted an entry, from the audit trail (v0.2 §22).
let createdBy (ledger: Ledger) (entryId: string) =
    ledger.Audit |> List.tryFind (fun a -> a.What = "journal-entry-posted" && a.Subject = entryId) |> Option.map _.Who

type PeriodProblem =
    | AlreadyMigrated of entryId: string
    | NotBalanceSheetAccount of accountId: string
    /// Opening balances come before any other activity.
    | ActivityBeforeMigration of entryId: string
    | NoOpeningLines
    | ClosingAccountNotEquity of accountId: string
    | AlreadyClosed of year: int
    | NothingToClose
    | DescriptionRequired
    | PeriodPosting of Problem list

let private isoDate (d: DateOnly) = d.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

let private lineOf (accountId: string, side: Side) memo =
    { AccountId = accountId
      Side = side
      Memo = memo
      Dimensions = noDimensions }

// ---- Opening balances (§21) --------------------------------------------------------------

type OpeningBalances =
    { EntryId: string
      MigrationDate: DateOnly
      Lines: (string * Side) list
      /// The system or file the balances came from.
      Reference: string option }

/// The opening trial balance before anything is posted: debits and credits,
/// which must be equal (§21).
let openingTotals (currency: string) (opening: OpeningBalances) =
    totals currency (opening.Lines |> List.map (fun l -> lineOf l None))

/// Posts the opening balances as one identifiable entry on the migration
/// date. History before it is not recreated: income to date belongs in
/// retained earnings, so only balance sheet accounts open. Idempotent.
let postOpeningBalances (context: Context) (opening: OpeningBalances) (ledger: Ledger) =
    let existing = ledger.Entries |> Map.toList |> List.map snd |> List.tryFind (fun e -> e.Source.StartsWith OpeningSource)

    match existing with
    | Some e when e.Id = opening.EntryId -> Ok ledger
    | Some e -> Error [ AlreadyMigrated e.Id ]
    | None ->
        let problems =
            [ if opening.Lines.IsEmpty then NoOpeningLines
              for account, _ in opening.Lines do
                  match ledger.Accounts.TryFind account with
                  | Some a when a.Type = Revenue || a.Type = Expense -> NotBalanceSheetAccount account
                  | _ -> ()
              for e in ledger.Entries |> Map.toList |> List.map snd |> List.sortBy _.Id do
                  if e.Date < opening.MigrationDate then ActivityBeforeMigration e.Id ]

        if not problems.IsEmpty then
            Error problems
        else
            let memo = opening.Reference |> Option.map (fun r -> $"Opening balance from {r}") |> Option.orElse (Some "Opening balance")

            let draft =
                { Date = opening.MigrationDate
                  Description = "Opening balances at " + isoDate opening.MigrationDate
                  Lines = opening.Lines |> List.map (fun l -> lineOf l memo)
                  Source = OpeningSource + isoDate opening.MigrationDate }

            post context $"opening:{opening.EntryId}" opening.EntryId draft ledger
            |> Result.mapError (PeriodPosting >> List.singleton)
            |> Result.map (fun (next, _) -> audit context "opening-balances-posted" opening.EntryId next)

// ---- CPA adjusting entries (§22) ----------------------------------------------------------------

type AdjustingEntry =
    { EntryId: string
      Date: DateOnly
      Description: string
      Kind: AdjustingKind
      Lines: Line list
      /// A CPA workpaper reference, for example `WP-14.2`.
      Reference: string option }

/// Posts an adjusting entry, marked as such and attributed to whoever
/// posts it. Idempotent by entry id.
let postAdjusting (context: Context) (entry: AdjustingEntry) (ledger: Ledger) =
    if String.IsNullOrWhiteSpace entry.Description then
        Error [ DescriptionRequired ]
    else
        let source =
            AdjustingSource
            + adjustingName entry.Kind
            + (entry.Reference |> Option.map (fun r -> ":" + r) |> Option.defaultValue "")

        post context $"adjusting:{entry.EntryId}" entry.EntryId { Date = entry.Date; Description = entry.Description; Lines = entry.Lines; Source = source } ledger
        |> Result.mapError (PeriodPosting >> List.singleton)
        |> Result.map fst

// ---- Year-end closing (§23) --------------------------------------------------------------------------

/// The entry that closes revenue and expense accounts into retained
/// earnings at the fiscal year end. It is proposed, never posted
/// automatically (§23); `postClosing` posts it when a person decides.
let closingEntry (currency: string) (yearEnd: DateOnly) (retainedEarnings: string) (ledger: Ledger) =
    match ledger.Accounts.TryFind retainedEarnings with
    | Some a when a.Type <> Equity -> Error [ ClosingAccountNotEquity retainedEarnings ]
    | None -> Error [ ClosingAccountNotEquity retainedEarnings ]
    | Some _ ->
        let temporary =
            ledger.Accounts
            |> Map.toList
            |> List.map snd
            |> List.filter (fun a -> a.Type = Revenue || a.Type = Expense)
            |> List.sortBy _.Code
            |> List.choose (fun a ->
                // Balances in each account's normal direction, closings included.
                let b = balance ledger currency yearEnd a.Id

                if b.Minor = 0L then
                    None
                else
                    let positive = { b with Minor = abs b.Minor }

                    let side =
                        match a.Type, b.Minor > 0L with
                        | Revenue, true
                        | Expense, false -> Debit positive
                        | _ -> Credit positive

                    Some(lineOf (a.Id, side) (Some "Year-end close")))

        if temporary.IsEmpty then
            Error [ NothingToClose ]
        else
            let debits, credits = totals currency temporary

            let equityLine =
                if debits > credits then [ lineOf (retainedEarnings, Credit(subtract debits credits)) (Some "Net income") ]
                elif credits > debits then [ lineOf (retainedEarnings, Debit(subtract credits debits)) (Some "Net loss") ]
                else []

            Ok
                { Date = yearEnd
                  Description = "Close fiscal year ended " + isoDate yearEnd + " into retained earnings"
                  Lines = temporary @ equityLine
                  Source = $"{ClosingSource}{yearEnd.Year}" }

/// Posts the year-end closing entry for the fiscal year ending `yearEnd`.
/// Once per year.
let postClosing (context: Context) (currency: string) (yearEnd: DateOnly) (retainedEarnings: string) (entryId: string) (ledger: Ledger) =
    let source = $"{ClosingSource}{yearEnd.Year}"

    match ledger.Entries |> Map.exists (fun _ e -> e.Source = source) with
    | true -> Error [ AlreadyClosed yearEnd.Year ]
    | false ->
        closingEntry currency yearEnd retainedEarnings ledger
        |> Result.bind (fun draft ->
            post context $"close:{yearEnd.Year}" entryId draft ledger
            |> Result.mapError (PeriodPosting >> List.singleton)
            |> Result.map (fun (next, _) -> audit context "year-end-closed" (string yearEnd.Year) next))

// ---- Cash basis projection (§24) -----------------------------------------------------------------

type CashBasisStatement =
    { Revenue: Money
      Expenses: Money
      NetIncome: Money
      /// Revenue by account, in account order.
      RevenueByAccount: (string * Money) list }

/// Splits an amount across weights exactly: proportional shares rounded
/// down, the remainder going to the largest fractions, ties by order.
let private apportion (amount: int64) (weights: (string * int64) list) (whole: int64) =
    if whole = 0L then
        []
    else
        let raw = weights |> List.map (fun (k, w) -> k, decimal amount * decimal w / decimal whole)
        let floors = raw |> List.map (fun (k, x) -> k, int64 (Math.Floor x), x - Math.Floor x)
        let target = int64 (Math.Round(decimal amount * decimal (weights |> List.sumBy snd) / decimal whole, MidpointRounding.AwayFromZero))
        let left = target - (floors |> List.sumBy (fun (_, f, _) -> f))
        let order = floors |> List.mapi (fun i (k, _, frac) -> k, i, frac) |> List.sortBy (fun (_, i, frac) -> -frac, i) |> List.map (fun (k, i, _) -> i)
        let bonus = order |> List.truncate (int (max 0L left)) |> Set.ofList
        floors |> List.mapi (fun i (k, f, _) -> k, (if bonus.Contains i then f + 1L else f))

/// The net revenue an invoice recognized, by revenue account.
let private invoiceRevenue (ledger: Ledger) (invoice: IssuedInvoice) =
    match ledger.Entries.TryFind invoice.JournalEntryId with
    | None -> []
    | Some entry ->
        entry.Lines
        |> List.filter (fun l -> ledger.Accounts.TryFind l.AccountId |> Option.exists (fun a -> a.Type = Revenue))
        |> List.groupBy _.AccountId
        |> List.map (fun (account, lines) ->
            account,
            lines
            |> List.sumBy (fun l ->
                match l.Side with
                | Credit m -> m.Minor
                | Debit m -> -m.Minor))
        |> List.sortBy fst

/// Revenue when cash arrives and expenses when cash leaves, projected from
/// the accrual ledger. Cash received for an invoice recognizes that
/// invoice's revenue in proportion; the share that paid a tax is not
/// revenue. Credit memos and write-offs move no cash and are left out.
let cashBasis (currency: string) (from: DateOnly) (until: DateOnly) (cashAccounts: string list) (r: Receivables) =
    let ledger = r.Books.Ledger
    let within (d: DateOnly) = d >= from && d <= until

    let receipts =
        [ for a in liveAllocations r do
              match r.Payments.TryFind a.PaymentId, r.Books.Invoices.TryFind a.InvoiceId with
              | Some p, Some i when within p.DateReceived && i.Currency = currency -> i, a.Amount.Minor
              | _ -> ()
          for a in r.Applications do
              match a.Source, r.Books.Invoices.TryFind a.InvoiceId with
              | FromCredit _, Some i
              | FromDeposit _, Some i when within a.Date && i.Currency = currency -> i, a.Amount.Minor
              | _ -> () ]

    let revenue =
        receipts
        |> List.collect (fun (invoice, amount) -> apportion amount (invoiceRevenue ledger invoice) invoice.Total.Minor)
        |> List.groupBy fst
        |> List.map (fun (account, shares) -> account, { Currency = currency; Minor = shares |> List.sumBy snd })
        |> List.sortBy (fun (account, _) -> ledger.Accounts[account].Code)

    let expenses =
        ledger.Journal
        |> List.map (fun id -> ledger.Entries[id])
        |> List.filter (fun e -> within e.Date && not (isClosingEntry e))
        |> List.filter (fun e -> e.Lines |> List.exists (fun l -> List.contains l.AccountId cashAccounts && (match l.Side with Credit _ -> true | _ -> false)))
        |> List.collect _.Lines
        |> List.filter (fun l -> ledger.Accounts.TryFind l.AccountId |> Option.exists (fun a -> a.Type = Expense))
        |> List.sumBy (fun l ->
            match l.Side with
            | Debit m when m.Currency = currency -> m.Minor
            | Credit m when m.Currency = currency -> -m.Minor
            | _ -> 0L)

    let totalRevenue = revenue |> List.map snd |> sum currency
    let totalExpenses = { Currency = currency; Minor = expenses }

    { Revenue = totalRevenue
      Expenses = totalExpenses
      NetIncome = subtract totalRevenue totalExpenses
      RevenueByAccount = revenue }
