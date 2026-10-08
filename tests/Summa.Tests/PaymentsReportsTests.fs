/// Payments, allocation, AR aging and the core reports (WI-0014): v0.1
/// §10-15, §19, §20 and the First Vertical Slice end to end.
module Summa.Tests.PaymentsReportsTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Reports
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let payment id amount =
    { Id = id
      CustomerId = abc.Id
      DateReceived = DateOnly(2026, 10, 20)
      Amount = usd amount
      Method = Ach
      Reference = $"ACH-{id}"
      Memo = None }

let allocation id paymentId invoiceId amount =
    { AllocationId = id
      PaymentId = paymentId
      InvoiceId = invoiceId
      Amount = usd amount
      JournalEntryId = $"JE-{id}"
      CashAccountId = "cash"
      ReceivableAccountId = "ar" }

let receivables () = start (fst (issued ()))
let invoiceOf (r: Receivables) = r.Books.Invoices["INV-001"]

[<Fact>]
let ``the first vertical slice: issue, pay, Paid, and the books balance`` () =
    // Create client, enter and issue a 6,050 consulting invoice (AR/revenue posted).
    let r = receivables ()
    Assert.Equal(Issued, status r (invoiceOf r))
    Assert.Equal(usd 605000L, outstanding r (invoiceOf r))
    // Record the payment and apply it: Cash/AR posted.
    let paid =
        r
        |> recordPayment context (payment "PAY-1" 605000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 605000L) |> ok
    Assert.Equal(Paid, status paid (invoiceOf paid))
    Assert.Equal(Settled, obligationStatus paid paid.Books.Obligations["OBL-001"])
    let ledger = paid.Books.Ledger
    let asOf = DateOnly(2026, 10, 31)
    // Trial balance balances; P&L and balance sheet reflect it.
    let tb = trialBalance "USD" asOf ledger
    Assert.Equal(tb.TotalDebits, tb.TotalCredits)
    Assert.Equal<(string * Money * Money) list>([ "1000", usd 605000L, zero "USD"; "4000", zero "USD", usd 605000L ], tb.Rows |> List.map (fun r -> r.Code, r.Debit, r.Credit))
    Assert.Equal({ Revenue = usd 605000L; Expenses = zero "USD"; NetIncome = usd 605000L }, incomeStatement "USD" (DateOnly(2026, 10, 1)) asOf ledger)
    let sheet = balanceSheet "USD" asOf ledger
    Assert.Equal(usd 605000L, sheet.Assets)
    Assert.True(balances sheet)

[<Fact>]
let ``partial and multiple payments; status follows the facts`` () =
    let r =
        receivables ()
        |> recordPayment context (payment "PAY-1" 200000L) |> ok
        |> recordPayment context (payment "PAY-2" 500000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 200000L) |> ok
    Assert.Equal(PartiallyPaid, status r (invoiceOf r))
    Assert.Equal(PartiallySettled, obligationStatus r r.Books.Obligations["OBL-001"])
    Assert.Equal(usd 405000L, outstanding r (invoiceOf r))
    // Neither the invoice's outstanding balance nor the payment's
    // unallocated balance may be exceeded.
    Assert.Equal<PaymentProblem list>([ ExceedsUnallocated(usd 0L) ], allocate context (allocation "AL-2" "PAY-1" "INV-001" 1L) r |> refused)
    Assert.Equal<PaymentProblem list>([ ExceedsOutstanding(usd 405000L) ], allocate context (allocation "AL-2" "PAY-2" "INV-001" 500000L) r |> refused)
    let done' = allocate context (allocation "AL-2" "PAY-2" "INV-001" 405000L) r |> ok
    Assert.Equal(Paid, status done' (invoiceOf done'))
    Assert.Equal(usd 95000L, unallocated done' done'.Payments["PAY-2"])

[<Fact>]
let ``recording and allocating are idempotent and validated`` () =
    let once = receivables () |> recordPayment context (payment "PAY-1" 605000L) |> ok
    let again = [ 1..5 ] |> List.fold (fun r _ -> recordPayment context (payment "PAY-1" 605000L) r |> ok) once
    Assert.Equal(1, again.Payments.Count)
    Assert.Equal<PaymentProblem list>([ PaymentIdReused "PAY-1" ], recordPayment context (payment "PAY-1" 1L) once |> refused)
    let allocated = [ 1..5 ] |> List.fold (fun r _ -> allocate context (allocation "AL-1" "PAY-1" "INV-001" 100000L) r |> ok) once
    Assert.Equal(1, allocated.Allocations.Length)
    Assert.Equal(2, allocated.Books.Ledger.Entries.Count) // the invoice and one allocation
    Assert.Equal<PaymentProblem list>([ PaymentNotPositive ], recordPayment context (payment "PAY-9" 0L) once |> refused)
    Assert.Equal<PaymentProblem list>([ UnknownPaymentCustomer "CUST-ZZZ" ], recordPayment context { payment "PAY-9" 5L with CustomerId = "CUST-ZZZ" } once |> refused)
    let other = { abc with Id = "CUST-XYZ" }
    let withOther = { once with Books = saveCustomer context other once.Books } |> recordPayment context { payment "PAY-X" 5L with CustomerId = "CUST-XYZ" } |> ok
    Assert.Equal<PaymentProblem list>([ CustomerMismatch ], allocate context (allocation "AL-X" "PAY-X" "INV-001" 5L) withOther |> refused)
    Assert.Equal<string list>([ "payment-recorded"; "payment-allocated" ], allocated.Books.Ledger.Audit |> List.map _.What |> List.filter (fun w -> w.StartsWith "payment"))

[<Fact>]
let ``overdue is orthogonal to status and aging uses the outstanding amount`` () =
    let r =
        receivables ()
        |> recordPayment context (payment "PAY-1" 105000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 105000L) |> ok
    let invoice = invoiceOf r // due 2026-11-06
    Assert.Equal(Current, timing (DateOnly(2026, 10, 20)) invoice.DueDate (outstanding r invoice))
    Assert.Equal(DueSoon, timing (DateOnly(2026, 11, 1)) invoice.DueDate (outstanding r invoice))
    Assert.Equal(Overdue, timing (DateOnly(2026, 12, 1)) invoice.DueDate (outstanding r invoice))
    Assert.Equal(PartiallyPaid, status r invoice) // still partially paid while overdue

    let row today = aging "USD" today r |> List.exactlyOne
    Assert.Equal(usd 500000L, (row (DateOnly(2026, 10, 20))).Current)
    Assert.Equal(usd 500000L, (row (DateOnly(2026, 12, 1))).Days1To30)
    Assert.Equal(usd 500000L, (row (DateOnly(2027, 1, 5))).Days31To60)
    Assert.Equal(usd 500000L, (row (DateOnly(2027, 2, 1))).Days61To90)
    Assert.Equal(usd 500000L, (row (DateOnly(2027, 3, 1))).Over90)
    Assert.Equal<(string * AgingBucket * Money) list>([ "EF-2026-0001", Over90, usd 500000L ], (row (DateOnly(2027, 3, 1))).Invoices)
    Assert.Equal(usd 500000L, (row (DateOnly(2027, 3, 1))).Total)

[<Fact>]
let ``receivables as of a date leave out what happened later`` () =
    let r =
        receivables ()
        |> recordPayment context (payment "PAY-1" 105000L) |> ok // received 2026-10-20
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 105000L) |> ok
    let invoice = invoiceOf r
    let before = asOf (DateOnly(2026, 10, 19)) r
    Assert.Equal(usd 605000L, outstanding before invoice)
    Assert.Equal(Issued, status before invoice)
    Assert.Empty before.Payments
    let on = asOf (DateOnly(2026, 10, 20)) r
    Assert.Equal(usd 500000L, outstanding on invoice)
    Assert.Equal(usd 500000L, (aging "USD" (DateOnly(2026, 10, 20)) on |> List.exactlyOne).Total)
    // Before the invoice was issued there was nothing to collect.
    let earlier = asOf (invoice.IssueDate.AddDays -1) r
    Assert.Empty earlier.Books.Invoices
    Assert.Empty earlier.Allocations
    Assert.Empty(aging "USD" (invoice.IssueDate.AddDays -1) earlier)

[<Fact>]
let ``the general ledger runs a balance and the exports are CSV`` () =
    let r =
        receivables ()
        |> recordPayment context (payment "PAY-1" 105000L) |> ok
        |> allocate context (allocation "AL-1" "PAY-1" "INV-001" 105000L) |> ok
    let ar = generalLedger "USD" "ar" r.Books.Ledger
    Assert.Equal<Money list>([ usd 605000L; usd 500000L ], ar |> List.map _.RunningBalance)
    let tb = trialBalance "USD" (DateOnly(2026, 10, 31)) r.Books.Ledger
    let csv = trialBalanceCsv tb
    Assert.StartsWith("Code,Account,Debit,Credit\n1000,Operating Cash,1050.00,0.00\n", csv)
    Assert.EndsWith("Total,,6050.00,6050.00\n", csv)
    let journal = journalCsv r.Books.Ledger
    Assert.Contains("JE-000001,2026-10-07,Invoice EF-2026-0001,1100,6050.00,,invoice:EF-2026-0001", journal)
    Assert.Equal(5, journal.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length)

[<Fact>]
let ``expenses and equity keep the balance sheet in balance`` () =
    let ledger =
        [ "k1", "JE-1", [ line "cash" (Debit(usd 1000000L)); line "equity" (Credit(usd 1000000L)) ]
          "k2", "JE-2", [ line "software" (Debit(usd 4900L)); line "cash" (Credit(usd 4900L)) ] ]
        |> List.fold (fun l (k, id, lines) -> post context k id (draft lines) l |> ok |> fst) chart
    let sheet = balanceSheet "USD" day ledger
    Assert.Equal((usd 995100L, usd 1000000L, usd -4900L), (sheet.Assets, sheet.Equity, sheet.CurrentEarnings))
    Assert.True(balances sheet)
    Assert.Equal(usd -4900L, (incomeStatement "USD" day day ledger).NetIncome)
