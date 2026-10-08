/// The core financial reports (v0.1 §15) and CSV export (§20), computed
/// from posted journal entries only, so every report agrees with the ledger.
module Summa.Ledger.Reports

open System
open System.Globalization
open Summa.Ledger.Money
open Summa.Ledger.Ledger

type TrialBalanceRow =
    { AccountId: string
      Code: string
      Name: string
      Debit: Money
      Credit: Money }

type TrialBalance =
    { Rows: TrialBalanceRow list
      TotalDebits: Money
      TotalCredits: Money }

let private accountsByCode (ledger: Ledger) = ledger.Accounts |> Map.toList |> List.map snd |> List.sortBy _.Code

/// Each account's balance on its natural side; a contra balance moves to
/// the other column. Total debits always equal total credits.
let trialBalance (currency: string) (asOf: DateOnly) (ledger: Ledger) =
    let rows =
        accountsByCode ledger
        |> List.map (fun a ->
            let b = balance ledger currency asOf a.Id
            let positive = { b with Minor = abs b.Minor }

            let debit, credit =
                match normalBalance a.Type, b.Minor >= 0L with
                | DebitNormal, true
                | CreditNormal, false -> positive, zero currency
                | DebitNormal, false
                | CreditNormal, true -> zero currency, positive

            { AccountId = a.Id; Code = a.Code; Name = a.Name; Debit = debit; Credit = credit })
        |> List.filter (fun r -> r.Debit.Minor <> 0L || r.Credit.Minor <> 0L)

    { Rows = rows
      TotalDebits = rows |> List.map _.Debit |> sum currency
      TotalCredits = rows |> List.map _.Credit |> sum currency }

type LedgerRow =
    { Date: DateOnly
      EntryId: string
      Description: string
      Debit: Money
      Credit: Money
      RunningBalance: Money }

/// One account's general ledger with a running balance in its normal
/// direction.
let generalLedger (currency: string) (accountId: string) (ledger: Ledger) =
    let normal = normalBalance ledger.Accounts[accountId].Type

    ledger.Journal
    |> List.map (fun id -> ledger.Entries[id])
    |> List.collect (fun e -> e.Lines |> List.filter (fun l -> l.AccountId = accountId) |> List.map (fun l -> e, l))
    |> List.sortBy (fun (e, _) -> e.Date)
    |> List.mapFold
        (fun running (entry, line) ->
            let debit, credit =
                match line.Side with
                | Ledger.Debit m -> m, zero currency
                | Ledger.Credit m -> zero currency, m

            let next =
                match normal with
                | DebitNormal -> subtract (add running debit) credit
                | CreditNormal -> subtract (add running credit) debit

            { Date = entry.Date; EntryId = entry.Id; Description = entry.Description; Debit = debit; Credit = credit; RunningBalance = next }, next)
        (zero currency)
    |> fst

let private activity (ledger: Ledger) (currency: string) (from: DateOnly) (until: DateOnly) (accountType: AccountType) =
    ledger.Accounts
    |> Map.toList
    |> List.map snd
    |> List.filter (fun a -> a.Type = accountType)
    |> List.map (fun a -> subtract (balance ledger currency until a.Id) (balance ledger currency (from.AddDays -1) a.Id))
    |> sum currency

type IncomeStatement =
    { Revenue: Money
      Expenses: Money
      NetIncome: Money }

/// The source prefix of year-end closing entries (v0.2 §23).
[<Literal>]
let ClosingSource = "year-end-close:"

let isClosingEntry (entry: PostedEntry) = entry.Source.StartsWith ClosingSource

/// The ledger without its year-end closing entries: what the year earned.
let withoutClosing (ledger: Ledger) =
    { ledger with Journal = ledger.Journal |> List.filter (fun id -> not (isClosingEntry ledger.Entries[id])) }

/// Revenue and expenses over a period. Year-end closing entries move the
/// result into equity; they are not the year's activity, so they are left
/// out and a closed year still shows what it earned.
let incomeStatement (currency: string) (from: DateOnly) (until: DateOnly) (ledger: Ledger) =
    let operating = withoutClosing ledger
    let revenue = activity operating currency from until Revenue
    let expenses = activity operating currency from until Expense

    { Revenue = revenue
      Expenses = expenses
      NetIncome = subtract revenue expenses }

type BalanceSheet =
    { Assets: Money
      Liabilities: Money
      Equity: Money
      /// Revenue less expenses to date, not yet closed into equity.
      CurrentEarnings: Money }

let balanceSheet (currency: string) (asOf: DateOnly) (ledger: Ledger) =
    let total accountType =
        ledger.Accounts
        |> Map.toList
        |> List.map snd
        |> List.filter (fun a -> a.Type = accountType)
        |> List.map (fun a -> balance ledger currency asOf a.Id)
        |> sum currency

    { Assets = total Asset
      Liabilities = total Liability
      Equity = total Equity
      CurrentEarnings = subtract (total Revenue) (total Expense) }

/// Assets = Liabilities + Equity (including current earnings).
let balances (sheet: BalanceSheet) =
    sheet.Assets = add sheet.Liabilities (add sheet.Equity sheet.CurrentEarnings)

let private decimalText (m: Money) =
    (decimal m.Minor / 100m).ToString("0.00", CultureInfo.InvariantCulture)

let private csvField (text: string) =
    if text.IndexOfAny [| ','; '"'; '\n'; '\r' |] >= 0 then "\"" + text.Replace("\"", "\"\"") + "\"" else text

/// The trial balance as CSV (§20): code, name, debit, credit.
let trialBalanceCsv (tb: TrialBalance) =
    let lines =
        [ "Code,Account,Debit,Credit" ]
        @ (tb.Rows |> List.map (fun r -> String.Join(",", [ csvField r.Code; csvField r.Name; decimalText r.Debit; decimalText r.Credit ])))
        @ [ $"Total,,{decimalText tb.TotalDebits},{decimalText tb.TotalCredits}" ]

    String.Join("\n", lines) + "\n"

/// Journal entries as CSV, one row per line, in posting order (§20).
let journalCsv (ledger: Ledger) =
    let rows =
        ledger.Journal
        |> List.map (fun id -> ledger.Entries[id])
        |> List.collect (fun e ->
            e.Lines
            |> List.map (fun l ->
                let debit, credit =
                    match l.Side with
                    | Ledger.Debit m -> decimalText m, ""
                    | Ledger.Credit m -> "", decimalText m

                String.Join(",", [ csvField e.Id; e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); csvField e.Description; csvField ledger.Accounts[l.AccountId].Code; debit; credit; csvField e.Source ])))

    String.Join("\n", "Entry,Date,Description,Account,Debit,Credit,Source" :: rows) + "\n"
