/// The external invoice command contract, `summa.invoice-commands` 1.1
/// (WI-0029 slice 15, INV-CONTRACT-002): versioned requests and responses,
/// golden vectors, and each command run as a domain operation.
module Summa.Tests.InvoiceCommandsTests

open System
open System.IO
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.InvoiceCommands
open Summa.Tests.Support
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

let private vectors (folder: string) =
    Directory.GetFiles(Path.Combine(repositoryRoot, "tests", "Summa.Tests", "vectors", "invoice-commands", folder), "*.json")
    |> Array.sort
    |> Array.map (fun path -> string (Path.GetFileNameWithoutExtension path), (File.ReadAllText path).TrimEnd('\n'))
    |> List.ofArray

let private isResponse (text: string) = text.Contains "\"kind\":\"response\""

[<Fact>]
let ``INV-CONTRACT-002: every valid vector decodes, and encodes back to exactly its bytes`` () =
    let valid = vectors "valid"
    Assert.True(valid.Length >= 10)

    for name, text in valid do
        let again =
            if isResponse text then
                decodeResponse text |> Result.map encodeResponse
            else
                decodeRequest text |> Result.map encodeRequest

        match again with
        | Ok bytes -> Assert.True((text = bytes), $"{name} did not encode back to its bytes:\n{bytes}")
        | Error problems -> failwith $"{name}: %A{problems}"

[<Fact>]
let ``INV-CONTRACT-002: every invalid vector is refused at the path it names`` () =
    let invalid = vectors "invalid"
    Assert.True(invalid.Length >= 16)

    for name, text in invalid do
        let path = name.Split('@', 2)[1]

        let problems =
            match (if isResponse text then decodeResponse text |> Result.map ignore else decodeRequest text |> Result.map ignore) with
            | Ok() -> failwith $"{name} was accepted"
            | Error problems -> problems

        Assert.True(problems |> List.exists (fun p -> p.Path = path), $"{name}: %A{problems}")

[<Fact>]
let ``INV-CONTRACT-002: a newer minor of major 1 is read, ignoring what it adds; the response names the request it answers`` () =
    let newer =
        """{"contract":"summa.invoice-commands","kind":"request","message":{"command":{"draftId":"D-1","expectedVersion":1,"kind":"submit-for-review","note":"added in 1.2"},"priority":"high","requestId":"req-9"},"version":"1.2"}"""

    Assert.Equal(Ok { RequestId = "req-9"; Command = SubmitForReview("D-1", 1) }, decodeRequest newer)
    let response = { RequestId = "req-9"; Outcome = Conflict 2 }
    Assert.Equal(Ok response, decodeResponse (encodeResponse response))

/// The organization's defaults, as the host supplies them.
let private host: Host =
    { Accounts = { TimeRevenue = "revenue"; FeeRevenue = "revenue"; ReimbursedExpenses = "revenue" }
      IssueRequest = fun draftId version date -> { request with DraftId = draftId; ExpectedVersion = version; IssueDate = date }
      VoidEntryId = fun invoiceId -> $"JE-VOID-{invoiceId}"
      CashAccountId = "cash"
      ReceivableAccountId = "ar"
      AllocationEntryId = fun allocationId -> $"JE-AL-{allocationId}" }

let private run (r: Receivables) (text: string) =
    match decodeRequest text with
    | Ok parsed ->
        let next, response = handle context host parsed r
        next, decodeResponse (encodeResponse response) |> Result.defaultWith (fun e -> failwith $"%A{e}")
    | Error problems -> failwith $"%A{problems}"

let private message (requestId: string) (command: Command) = encodeRequest { RequestId = requestId; Command = command }

[<Fact>]
let ``INV-CONTRACT-002: requests run as domain commands; a stale version is a conflict and a refusal changes nothing`` () =
    let drafted = start (saveDraft context (draftFor [ consulting 10000L 15000L ]) books |> ok)

    // Submitting a version other than the draft's is a conflict, naming the current one.
    let same, conflict = run drafted (message "req-1" (SubmitForReview("D-1", 3)))
    Assert.Equal({ RequestId = "req-1"; Outcome = Conflict 1 }, conflict)
    Assert.True((same = drafted))

    let submitted, accepted = run drafted (message "req-2" (SubmitForReview("D-1", 1)))
    Assert.Equal(Accepted { Kind = "draft"; Id = "D-1"; Version = Some 1; Number = None }, accepted.Outcome)

    let issued, response = run submitted (message "req-3" (IssueInvoice("D-1", 1, DateOnly(2026, 10, 7))))

    match response.Outcome with
    | Accepted produced ->
        Assert.Equal("invoice", produced.Kind)
        Assert.Equal(Some issued.Books.Invoices[produced.Id].Number, produced.Number)
    | other -> failwith $"%A{other}"

    // The same request again returns the same invoice and changes nothing (INV-CON-007).
    let again, repeat = run issued (message "req-3" (IssueInvoice("D-1", 1, DateOnly(2026, 10, 7))))
    Assert.Equal(response, repeat)
    Assert.True((again = issued))

    // A paid invoice cannot be voided: refused with a reason, books unchanged.
    let invoiceId = issued.Books.Invoices |> Map.keys |> Seq.exactlyOne
    let paid = issued |> recordPayment context { PaymentsReportsTests.payment "PAY-1" 1000L with CustomerId = abc.Id } |> ok |> allocate context (PaymentsReportsTests.allocation "AL-1" "PAY-1" invoiceId 1000L) |> ok
    let refusedBooks, refused = run paid (message "req-4" (VoidInvoice(invoiceId, "Duplicate", DateOnly(2026, 10, 8))))

    match refused.Outcome with
    | Refused [ reason ] -> Assert.Equal("void-refused", reason.Code)
    | other -> failwith $"%A{other}"

    Assert.True((refusedBooks = paid))
    // Unpaid, it is voided.
    let _, voided = run issued (message "req-5" (VoidInvoice(invoiceId, "Duplicate", DateOnly(2026, 10, 8))))
    Assert.Equal(Accepted { Kind = "invoice-void"; Id = invoiceId; Version = None; Number = None }, voided.Outcome)
    // A proposal for time Summa does not hold is refused.
    let _, unknown = run (start books) (message "req-6" (ProposeInvoice("P-1", abc.Id, None, "USD", [ "pub-missing" ])))

    match unknown.Outcome with
    | Refused reasons -> Assert.All(reasons, fun r -> Assert.Equal("proposal-refused", r.Code))
    | other -> failwith $"%A{other}"

// ---- Payments over the contract (SUM3-011, version 1.1) -----------------------------------------

[<Fact>]
let ``SUM3-011: payment operations have explicit, versioned commands; older messages keep their version and bytes`` () =
    let payment: Payment = { Id = "PAY-1"; CustomerId = abc.Id; DateReceived = DateOnly(2026, 10, 20); Amount = usd 100000L; Method = Ach; Reference = "ACH-1"; Memo = None }
    // Each message is written at the oldest version that defines it.
    Assert.Contains("\"version\":\"1.0\"", message "r" (SubmitForReview("D-1", 1)))
    Assert.Contains("\"version\":\"1.1\"", message "r" (RecordPayment payment))
    Assert.Contains("\"version\":\"1.1\"", message "r" (AllocatePayment("AL-1", "PAY-1", "INV-001", usd 100000L)))
    Assert.Equal(Ok { RequestId = "r"; Command = RecordPayment payment }, decodeRequest (message "r" (RecordPayment payment)))
    // A payment command claiming version 1.0 is refused at its kind, never guessed at.
    let claimed = (message "r" (RecordPayment payment)).Replace("\"version\":\"1.1\"", "\"version\":\"1.0\"")

    match decodeRequest claimed with
    | Error problems -> Assert.Contains(problems, fun p -> p.Path = "$.message.command.kind" && p.Message.Contains "1.1")
    | Ok _ -> failwith "a 1.0 message carried a 1.1 command"

[<Fact>]
let ``SUM3-011: a payment is recorded and allocated through the contract; an over-allocation is refused and changes nothing`` () =
    let issued, invoice = issue context request (withDraft (draftFor [ consulting 10000L 15000L ])) |> ok
    let r = start issued
    let payment: Payment = { Id = "PAY-1"; CustomerId = abc.Id; DateReceived = DateOnly(2026, 10, 20); Amount = usd 100000L; Method = Check; Reference = "CHK-1001"; Memo = Some "October" }
    let recorded, response = run r (message "req-p" (RecordPayment payment))
    Assert.Equal(Accepted { Kind = "payment"; Id = "PAY-1"; Version = None; Number = None }, response.Outcome)
    Assert.Equal(payment, recorded.Payments["PAY-1"])
    let allocated, applied = run recorded (message "req-a" (AllocatePayment("AL-1", "PAY-1", invoice.InvoiceId, usd 100000L)))
    Assert.Equal(Accepted { Kind = "allocation"; Id = "AL-1"; Version = None; Number = None }, applied.Outcome)
    Assert.Equal(usd 50000L, outstanding allocated invoice)
    Assert.True(allocated.Books.Ledger.Entries.ContainsKey "JE-AL-AL-1")
    // More than the payment has left is refused, and the books are unchanged.
    let same, refused = run allocated (message "req-b" (AllocatePayment("AL-2", "PAY-1", invoice.InvoiceId, usd 100L)))

    match refused.Outcome with
    | Refused [ reason ] -> Assert.Equal("allocation-refused", reason.Code)
    | other -> failwith $"%A{other}"

    Assert.True((same = allocated))
