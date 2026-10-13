/// One search over the books (WI-0044, v0.4 §18, SUM4-018).
module Summa.Tests.SearchTests

open System
open System.Diagnostics
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Search
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

/// ABC Corp owing EF-2026-0001 (6,050.00 USD, project PRJ-ARCH, journal
/// entry JE-000001), and a 1,000.00 payment "ACH 4471" with a memo.
let private searched () =
    let p = { payment "PAY-1" 100000L with Reference = "ACH 4471"; Memo = Some "Wire for the October retainer" }
    start (fst (issued ())) |> recordPayment context p |> ok

let private kinds (hits: Hit list, _) = hits |> List.map (fun h -> h.Kind, h.Title)

[<Fact>]
let ``SUM4-018: search finds customers, invoices, payments, projects, journal entries and memos`` () =
    let r = searched ()
    Assert.Equal<(Kind * string) list>([ CustomerHit, "ABC Corp" ], find r "abc corp" |> kinds |> List.filter (fun (k, _) -> k = CustomerHit))
    Assert.Equal((InvoiceHit, "EF-2026-0001"), find r "EF-2026-0001" |> kinds |> List.head)
    Assert.Equal<(Kind * string) list>([ PaymentHit, "ACH 4471" ], find r "ACH 4471" |> kinds)
    Assert.Equal<(Kind * string) list>([ PaymentHit, "ACH 4471" ], find r "october retainer" |> kinds)
    Assert.Equal("memo: Wire for the October retainer", (find r "october retainer" |> fst |> List.head).Matched)
    Assert.Contains((ProjectHit, "PRJ-ARCH"), find r "prj-arch" |> kinds)
    Assert.Equal((JournalEntryHit, "JE-000001"), find r "JE-000001" |> kinds |> List.head)

[<Fact>]
let ``SUM4-018: an amount finds what is exactly that amount, written either way, and not numbers that merely contain it`` () =
    let r = searched ()
    let byAmount = find r "6,050.00" |> kinds
    Assert.Contains((InvoiceHit, "EF-2026-0001"), byAmount)
    Assert.Contains((JournalEntryHit, "JE-000001"), byAmount)
    Assert.Equal<(Kind * string) list>(byAmount, find r "6050" |> kinds)
    Assert.Equal("total: 6,050.00 USD", (find r "6050" |> fst |> List.find (fun h -> h.Kind = InvoiceHit)).Matched)
    Assert.Equal<(Kind * string) list>([ PaymentHit, "ACH 4471" ], find r "1000" |> kinds)
    Assert.Empty(find r "605" |> fst |> List.filter (fun h -> h.Kind = InvoiceHit))

[<Fact>]
let ``SUM4-018: the whole field first, then its start, then inside it; an empty query finds nothing`` () =
    let r = searched ()
    let withMore = { r with Books = { r.Books with Customers = r.Books.Customers.Add("CUST-ABZ", { abc with Id = "CUST-ABZ"; Name = "Grabco"; BillingName = "Grabco"; Email = "x@grabco.example" }) } }
    let customers = find withMore "abc" |> kinds |> List.filter (fun (k, _) -> k = CustomerHit) |> List.map snd
    Assert.Equal<string list>([ "ABC Corp"; "Grabco" ], customers)
    Assert.Empty(find r "   " |> fst)

[<Fact>]
let ``SUM4-018: searching thousands of invoices is fast enough to navigate by, and says how many matched beyond the limit`` () =
    let r = searched ()
    let one = r.Books.Invoices["INV-001"]
    let many =
        [ 1..5000 ] |> List.fold (fun (m: Map<string, IssuedInvoice>) n -> m.Add($"INV-X{n}", { one with InvoiceId = $"INV-X{n}"; Number = $"EF-2025-{n:D4}" })) r.Books.Invoices

    let big = { r with Books = { r.Books with Invoices = many } }
    find big "warm-up" |> ignore
    let clock = Stopwatch.StartNew()
    let hits, count = find big "EF-2025"
    clock.Stop()
    Assert.Equal(limit, hits.Length)
    Assert.Equal(5000, count)
    Assert.True(clock.ElapsedMilliseconds < 1000L, $"search took {clock.ElapsedMilliseconds} ms")
