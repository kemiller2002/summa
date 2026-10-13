/// Attention-first receivables (WI-0044, v0.4 §10): open invoices that need
/// action come first, each with its reason.
module Summa.Tests.AttentionRankTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Attention
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let private day0 = DateOnly(2026, 10, 7)

/// ABC Corp (net 30) owing one invoice per (issue day, cents), numbered in
/// order: EF-2026-0001, EF-2026-0002 and so on.
let private issuedOn (invoices: (int * int64) list) =
    invoices
    |> List.indexed
    |> List.fold
        (fun books (i, (day, cents)) ->
            let n = i + 1
            let draftId = $"D-{n}"
            let drafted = saveDraft context { draftFor [ consulting 1000L cents ] with DraftId = draftId } books |> ok
            issue context { request with DraftId = draftId; IssueDate = day0.AddDays day; InvoiceId = $"INV-00{n}"; JournalEntryId = $"JE-00000{n}"; ObligationId = $"OBL-00{n}" } drafted |> ok |> fst)
        books
    |> start

/// Pays invoice n in full, this many days after it was issued.
let private paidAfter (n: int) (days: int) (r: Receivables) =
    let invoice = r.Books.Invoices[$"INV-00{n}"]
    let p = { payment $"PAY-{n}" invoice.Total.Minor with DateReceived = invoice.IssueDate.AddDays days }
    recordPayment context p r |> ok |> allocate context (allocation $"AL-{n}" p.Id invoice.InvoiceId invoice.Total.Minor) |> ok

let private ranked today r = rank today r |> List.map (fun x -> x.Invoice.Number, x.Reasons)

[<Fact>]
let ``SUM4-010: overdue first, then due this week, then large balances, then the rest by due date`` () =
    // Due on days 30, 40, 50 and 51; today is day 35.
    let r = issuedOn [ 0, 10000L; 10, 10000L; 20, 100000L; 21, 10000L ]
    let today = day0.AddDays 35

    Assert.Equal<(string * Reason list) list>(
        [ "EF-2026-0001", [ Overdue 5 ]
          "EF-2026-0002", [ DueThisWeek 5 ]
          "EF-2026-0003", [ LargeBalance 76 ]
          "EF-2026-0004", [] ],
        ranked today r
    )

[<Fact>]
let ``SUM4-010: within a group the more pressing comes first, and an invoice keeps every reason it has`` () =
    // Two overdue (by 20 and 5 days), one of them also a large balance.
    let r = issuedOn [ 15, 10000L; 0, 100000L; 30, 10000L ]
    let today = day0.AddDays 50

    match rank today r with
    | [ first; second; third ] ->
        Assert.Equal("EF-2026-0002", first.Invoice.Number)
        Assert.Equal<Reason list>([ Overdue 20; LargeBalance 83 ], first.Reasons)
        Assert.Equal("EF-2026-0001", second.Invoice.Number)
        Assert.Equal<Reason list>([ Overdue 5 ], second.Reasons)
        Assert.Equal("EF-2026-0003", third.Invoice.Number)
        Assert.Equal<Reason list>([], third.Reasons)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``SUM4-010: an invoice open more than a week longer than the customer usually takes is an unusual delay`` () =
    // Paid in 10, 12 and 14 days: usually 12. The fourth is open on day 21, due day 30.
    let r = issuedOn [ 0, 10000L; 0, 10000L; 0, 10000L; 0, 10000L ] |> paidAfter 1 10 |> paidAfter 2 12 |> paidAfter 3 14
    Assert.Equal(Some 12, usualDaysToPay r abc.Id)
    Assert.Equal<(string * Reason list) list>([ "EF-2026-0004", [ UnusualDelay(21, 12) ] ], ranked (day0.AddDays 21) r)
    // A week past usual is not yet unusual.
    Assert.Equal<(string * Reason list) list>([ "EF-2026-0004", [] ], ranked (day0.AddDays 19) r)

[<Fact>]
let ``SUM4-010: with too little history or too few open invoices, nothing is called unusual or large`` () =
    // Two paid invoices are not enough to know what is usual.
    let r = issuedOn [ 0, 10000L; 0, 10000L; 0, 900000L ] |> paidAfter 1 2 |> paidAfter 2 2
    Assert.Equal(None, usualDaysToPay r abc.Id)
    // One open invoice is all that is owed, but alone it is not called large; paid ones are not ranked.
    Assert.Equal<(string * Reason list) list>([ "EF-2026-0003", [] ], ranked (day0.AddDays 20) r)

[<Fact>]
let ``SUM4-010: each reason reads as a sentence fragment`` () =
    Assert.Equal("Overdue 1 day", reasonText (Overdue 1))
    Assert.Equal("Overdue 12 days", reasonText (Overdue 12))
    Assert.Equal("Due today", reasonText (DueThisWeek 0))
    Assert.Equal("Due tomorrow", reasonText (DueThisWeek 1))
    Assert.Equal("Due in 5 days", reasonText (DueThisWeek 5))
    Assert.Equal("Large balance: 40% of everything owed", reasonText (LargeBalance 40))
    Assert.Equal("Open 40 days; this customer usually pays in 21", reasonText (UnusualDelay(40, 21)))
