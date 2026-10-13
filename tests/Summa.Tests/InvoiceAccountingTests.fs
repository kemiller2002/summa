/// Invoice accounting, settlement and currency (INV-ACC, INV-SET, INV-FX),
/// one test per requirement, each asserting what the requirement says.
module Summa.Tests.InvoiceAccountingTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests
open Summa.Tests.PaymentsReportsTests

let private issuedEntry () =
    let books, invoice = issued ()
    books, invoice, books.Ledger.Entries[invoice.JournalEntryId]

[<Fact>]
let ``INV-ACC-001: issuing debits receivables and credits revenue`` () =
    let _, invoice, entry = issuedEntry ()
    let debits = entry.Lines |> List.choose (fun l -> match l.Side with Debit m -> Some(l.AccountId, m) | _ -> None)
    let credits = entry.Lines |> List.choose (fun l -> match l.Side with Credit m -> Some(l.AccountId, m) | _ -> None)
    Assert.Equal<(string * Money) list>([ "ar", invoice.Total ], debits)
    Assert.True(credits |> List.forall (fun (account, _) -> account = "revenue"))
    Assert.Equal(invoice.Total, credits |> List.map snd |> sum "USD")

[<Fact>]
let ``INV-ACC-002: the entry references the invoice by id and number, and the invoice its entry`` () =
    let books, invoice, entry = issuedEntry ()
    Assert.Equal(Some { Kind = "invoice"; Id = invoice.InvoiceId }, entry.Reference)
    Assert.Contains(invoice.Number, entry.Description)
    Assert.Equal($"invoice:{invoice.Number}", entry.Source)
    Assert.Equal(invoice.JournalEntryId, entry.Id)
    // Recoverable without reading text: the invoice an entry was posted for.
    Assert.Equal(Some invoice, entry.Reference |> Option.bind (fun r -> books.Invoices.TryFind r.Id))

[<Fact>]
let ``INV-ACC-003: each invoice line is traceable to its own revenue line, with its project`` () =
    let lines =
        [ consulting 10000L 15000L
          { consulting 2000L 20000L with Description = "Workshop"; Project = Some "PRJ-WORK" } ]

    let books, invoice = issue context request (withDraft (draftFor lines)) |> ok
    let entry = books.Ledger.Entries[invoice.JournalEntryId]

    for l in invoice.Lines do
        let journal = entry.Lines |> List.filter (fun j -> j.Memo = Some l.Description)
        Assert.Equal(1, journal.Length)
        Assert.Equal(l.RevenueAccountId, journal.Head.AccountId)
        Assert.Equal(l.Project, journal.Head.Dimensions.Project)
        Assert.Equal(Credit(lineAmount l), journal.Head.Side)

[<Fact>]
let ``INV-ACC-004: settling posts its own entries and never changes the issuance entry`` () =
    let r = receivables ()
    let invoice = invoiceOf r
    let original = r.Books.Ledger.Entries[invoice.JournalEntryId]

    let paid =
        r
        |> recordPayment context (payment "PAY-1" 100000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok

    Assert.Equal(original, paid.Books.Ledger.Entries[invoice.JournalEntryId])
    Assert.Equal(r.Books.Ledger.Entries.Count + 1, paid.Books.Ledger.Entries.Count)
    let allocationEntry = paid.Books.Ledger.Entries[paid.Allocations.Head.JournalEntryId]
    Assert.NotEqual<string>(invoice.JournalEntryId, allocationEntry.Id)

[<Fact>]
let ``INV-ACC-005: the receivable projection reconciles with the receivables control balance`` () =
    let r =
        receivables ()
        |> recordPayment context (payment "PAY-1" 250000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 250000L) |> ok

    let owed = r.Books.Invoices |> Map.toList |> List.map (snd >> outstanding r) |> sum "USD"
    Assert.Equal(owed, balance r.Books.Ledger "USD" (DateOnly(2026, 12, 31)) "ar")

[<Fact>]
let ``INV-ACC-006: a generated entry is explained in business language, line by line`` () =
    let books, invoice, entry = issuedEntry ()
    let sentences = Explanations.entry books.Ledger entry
    Assert.Equal($"This entry was posted for invoice {invoice.InvoiceId}: {entry.Description}.", sentences.Head)
    Assert.Contains("Accounts Receivable (1100) goes up by 6,050.00 USD for EF-2026-0001: the business owns, or is owed, that much more.", sentences)
    Assert.Contains(sentences, fun s -> s.StartsWith "Consulting Revenue (4000): " && s.Contains "earned for Architecture assessment")
    Assert.Equal("Debits and credits are equal: 6,050.00 USD each, so the books still balance.", List.last sentences)
    Assert.Equal(entry.Lines.Length + 2, sentences.Length)

[<Fact>]
let ``INV-SET-001 and INV-SET-006: the settlement timeline runs from the total to what is outstanding`` () =
    let r =
        receivables ()
        |> recordPayment context (payment "PAY-1" 100000L) |> ok
        |> recordPayment context { payment "PAY-2" 200000L with DateReceived = DateOnly(2026, 10, 25) } |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) |> ok
        |> allocate context (allocation "AL-2" "PAY-2" "INV-001" 200000L) |> ok

    let invoice = invoiceOf r
    let timeline = settlement r invoice
    Assert.Equal<SettlementEvent list>([ PaymentApplied("PAY-1", (payment "PAY-1" 0L).Reference); PaymentApplied("PAY-2", (payment "PAY-2" 0L).Reference) ], timeline |> List.map _.Event)
    Assert.Equal<Money list>([ usd 505000L; usd 305000L ], timeline |> List.map _.Balance)
    // The last balance is the outstanding amount, derived from the facts.
    Assert.Equal(outstanding r invoice, (List.last timeline).Balance)
    Assert.Equal(subtract invoice.Total (usd 300000L), outstanding r invoice)

[<Fact>]
let ``INV-SET-002 and INV-SET-007: partial payments leave it partly paid; Paid only at zero outstanding`` () =
    let partly =
        receivables ()
        |> recordPayment context (payment "PAY-1" 605000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 300000L) |> ok

    Assert.Equal(PartiallyPaid, status partly (invoiceOf partly))
    let paid = allocate context (allocation "AL-2" "PAY-1" "INV-001" 305000L) partly |> ok
    Assert.Equal(zero "USD", outstanding paid (invoiceOf paid))
    Assert.Equal(Paid, status paid (invoiceOf paid))

[<Fact>]
let ``INV-FX-001: every amount, draft, invoice and entry line names its currency`` () =
    let books, invoice, entry = issuedEntry ()
    Assert.Equal("USD", invoice.Currency)
    Assert.Equal("USD", invoice.Total.Currency)
    Assert.True(invoice.Lines |> List.forall (fun l -> l.UnitPrice.Currency = "USD"))
    Assert.True(entry.Lines |> List.forall (fun l -> match l.Side with Debit m | Credit m -> m.Currency = "USD"))
    Assert.Equal("USD", books.IssuedFrom |> Map.tryFindKey (fun _ id -> id = invoice.InvoiceId) |> Option.map (fun _ -> invoice.Currency) |> Option.defaultValue "")

[<Fact>]
let ``INV-FX-002: an invoice with a line in another currency is refused, and nothing is posted`` () =
    let euro = { consulting 1000L 10000L with UnitPrice = { Currency = "EUR"; Minor = 10000L } }
    let draft = draftFor [ consulting 1000L 10000L; euro ]
    let attempt = try Choice1Of2(issue context request (withDraft draft)) with ex -> Choice2Of2 ex

    match attempt with
    | Choice1Of2(Error problems) ->
        Assert.Equal<InvoiceProblem list>([ MixedCurrency "'Architecture assessment' is in EUR, not USD" ], problems)
    | Choice1Of2(Ok _) -> failwith "a mixed-currency invoice was issued"
    | Choice2Of2 ex -> failwith $"refused only by an exception, not a problem: {ex.Message}"

[<Fact>]
let ``INV-FX-003: amounts in different currencies are never added or converted implicitly`` () =
    Assert.Throws<InvalidOperationException>(fun () -> add (usd 1L) { Currency = "EUR"; Minor = 1L } |> ignore) |> ignore
    let mixed = { Date = DateOnly(2026, 10, 7); Description = "mixed"; Lines = [ line "cash" (Debit(usd 100L)); line "revenue" (Credit { Currency = "EUR"; Minor = 100L }) ]; Source = "manual" }
    Assert.Contains(MixedCurrencies, post context "k-fx" "JE-FX" mixed chart |> refused)
