/// Matching a payment to the invoices it pays (WI-0044, v0.4 §12): a
/// suggestion only when exactly one reading fits; otherwise a person chooses.
module Summa.Tests.MatchingTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Matching
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

/// ABC Corp owing one invoice per amount (in cents), issued in order:
/// EF-2026-0001, EF-2026-0002 and so on, due later in that order.
let private owing (amounts: int64 list) =
    amounts
    |> List.indexed
    |> List.fold
        (fun books (i, cents) ->
            let draftId = $"D-{i + 1}"
            let drafted = saveDraft context { draftFor [ consulting 1000L cents ] with DraftId = draftId } books |> ok
            let n = i + 1
            issue context { request with DraftId = draftId; IssueDate = request.IssueDate.AddDays i; InvoiceId = $"INV-00{n}"; JournalEntryId = $"JE-00000{n}"; ObligationId = $"OBL-00{n}" } drafted |> ok |> fst)
        books
    |> start

let private received (amount: int64) (reference: string) (r: Receivables) =
    let p = { payment "PAY-1" amount with Reference = reference }
    recordPayment context p r |> ok, p

let private numbersOf (suggestion: Suggestion) =
    match suggestion with
    | ApplyTo(applications, _) -> applications |> List.map (fun (i, m) -> i.Number, m.Minor)
    | Choose(candidates, _) -> candidates |> List.map (fun i -> i.Number, 0L)
    | NothingToApplyTo -> []

[<Fact>]
let ``SUM4-012: a payment that one invoice owes exactly is suggested for that invoice`` () =
    // The requirement's own example: 5,000 and 2,500 owed, 5,000 received.
    let r, p = owing [ 500000L; 250000L ] |> received 500000L "ACH 4471"

    match suggest r p with
    | ApplyTo([ invoice, amount ], because) ->
        Assert.Equal("EF-2026-0001", invoice.Number)
        Assert.Equal(usd 500000L, amount)
        Assert.Equal("EF-2026-0001 owes exactly 5,000.00 USD", because)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``SUM4-012: when two invoices owe the same amount, Summa does not guess`` () =
    let r, p = owing [ 250000L; 250000L; 100000L ] |> received 250000L "ACH 4472"

    match suggest r p with
    | Choose(candidates, because) ->
        Assert.Equal<string list>([ "EF-2026-0001"; "EF-2026-0002" ], candidates |> List.map _.Number)
        Assert.Equal("2 invoices each owe exactly 2,500.00 USD", because)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``SUM4-012: an amount no reading explains is left to a person, with every open invoice to choose from`` () =
    let r, p = owing [ 500000L; 250000L ] |> received 300000L ""

    match suggest r p with
    | Choose(candidates, because) ->
        Assert.Equal(2, candidates.Length)
        Assert.Equal("no open invoice owes 3,000.00 USD, and it is not what they owe together", because)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``SUM4-012: the reference naming an invoice, the whole balance, and the only open invoice are each one reading`` () =
    // The reference names the second invoice: it is suggested, even though the first owes the same.
    let named, p = owing [ 250000L; 250000L ] |> received 250000L "Payment for EF-2026-0002"
    Assert.Equal<(string * int64) list>([ "EF-2026-0002", 250000L ], numbersOf (suggest named p))
    // Exactly everything owed: each invoice in full.
    let all, p = owing [ 500000L; 250000L ] |> received 750000L ""
    Assert.Equal<(string * int64) list>([ "EF-2026-0001", 500000L; "EF-2026-0002", 250000L ], numbersOf (suggest all p))
    // One open invoice: part of it.
    let only, p = owing [ 500000L ] |> received 120000L ""
    Assert.Equal<(string * int64) list>([ "EF-2026-0001", 120000L ], numbersOf (suggest only p))
    // Nothing owed: nothing to apply to.
    let none, p = owing [] |> received 120000L ""
    Assert.Equal(NothingToApplyTo, suggest none p)

[<Fact>]
let ``SUM4-012: a suggestion applies nothing, and what is already applied is not suggested again`` () =
    let r, p = owing [ 500000L; 250000L ] |> received 500000L ""
    let before = r
    suggest r p |> ignore
    Assert.True((before = r))
    let first = r.Books.Invoices |> Map.findKey (fun _ i -> i.Number = "EF-2026-0001")
    let applied = allocate context (allocation "AL-1" "PAY-1" first 500000L) r |> ok
    Assert.Equal(NothingToApplyTo, suggest applied p)
