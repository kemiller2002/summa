/// The external invoice command contract, `summa.invoice-commands` 1.2
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
    Assert.True(valid.Length >= 13)

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
    Assert.True(invalid.Length >= 20)

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
        """{"contract":"summa.invoice-commands","kind":"request","message":{"command":{"draftId":"D-1","expectedVersion":1,"kind":"submit-for-review","note":"added in 1.3"},"priority":"high","requestId":"req-9"},"version":"1.3"}"""

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
      AllocationEntryId = fun allocationId -> $"JE-AL-{allocationId}"
      Receivables = { Cash = "cash"; Receivable = "ar"; CustomerCredits = "credits"; CustomerDeposits = "deposits"; BadDebt = "baddebt" }
      ApplicationEntryId = fun applicationId -> $"JE-AP-{applicationId}"
      ReversalEntryPrefix = fun paymentId -> $"JE-REV-{paymentId}"
      Currency = "USD" }

/// A caller whose role grants every capability, so a test sees the domain's answer.
let private everything: Caller = { Context = context; Capabilities = Set.ofList Summa.Access.Access.allCapabilities }

let private run (r: Receivables) (text: string) =
    match decodeRequest text with
    | Ok parsed ->
        let next, response = handle everything host parsed r
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

// ---- Domain operations, not generic CRUD (SUM2-031, version 1.2) -----------------------------------

/// Books with the liability and expense accounts credits and reversals post to.
let private withCreditAccounts () =
    let extra: Ledger.Account list =
        [ { Id = "credits"; Code = "2100"; Name = "Customer Credits"; Type = Ledger.Liability; Active = true }
          { Id = "deposits"; Code = "2200"; Name = "Customer Deposits"; Type = Ledger.Liability; Active = true }
          { Id = "baddebt"; Code = "6500"; Name = "Bad Debt"; Type = Ledger.Expense; Active = true } ]

    { books with Ledger = extra |> List.fold (fun l (a: Ledger.Account) -> Ledger.addAccount context a l |> ok) books.Ledger }

[<Fact>]
let ``SUM2-031: every operation the requirement names is a domain command of the contract, and no generic record operation is`` () =
    let named =
        [ IssueInvoice("D-1", 1, DateOnly(2026, 10, 7))
          RecordPayment { Id = "PAY-1"; CustomerId = abc.Id; DateReceived = DateOnly(2026, 10, 20); Amount = usd 100L; Method = Ach; Reference = ""; Memo = None }
          AllocatePayment("AL-1", "PAY-1", "INV-001", usd 100L)
          VoidInvoice("INV-001", "Duplicate", DateOnly(2026, 10, 8))
          ApplyCreditMemo("AP-1", "CM-1", "INV-001", usd 100L, DateOnly(2026, 10, 21))
          ReversePayment("PAY-1", "Returned", DateOnly(2026, 10, 22))
          ClosePeriod(2026, 10) ]

    for command in named do
        Assert.Equal(Ok { RequestId = "r"; Command = command }, decodeRequest (message "r" command))

    // PUT, PATCH and DELETE on records are not commands of the contract.
    for kind in [ "put-invoice"; "patch-payment"; "delete-journal-entry" ] do
        let text = (message "r" (SubmitForReview("D-1", 1))).Replace("\"submit-for-review\"", $"\"{kind}\"")

        match decodeRequest text with
        | Error problems -> Assert.Contains(problems, fun p -> p.Path = "$.message.command.kind")
        | Ok _ -> failwith $"{kind} was accepted"

[<Fact>]
let ``SUM2-031: a credit memo is applied, a payment reversed and a period closed through the contract, each by its business rules`` () =
    let issued, invoice = issue context request (saveDraft context (draftFor [ consulting 10000L 15000L ]) (withCreditAccounts ()) |> ok) |> ok
    let receivables: Credits.ReceivableAccounts = { Cash = "cash"; Receivable = "ar"; CustomerCredits = "credits"; CustomerDeposits = "deposits"; BadDebt = "baddebt" }

    let memo: CreditMemo =
        { Id = "CM-1"; CustomerId = abc.Id; InvoiceId = Some invoice.InvoiceId; Amount = usd 20000L; RevenueAccountId = "revenue"; Reason = "Goodwill"; IssueDate = DateOnly(2026, 10, 9); JournalEntryId = "JE-CM-1"; Lines = [] }

    let r = start issued |> Credits.issueCreditMemo context receivables memo |> ok
    let applied, application = run r (message "req-1" (ApplyCreditMemo("AP-1", "CM-1", invoice.InvoiceId, usd 20000L, DateOnly(2026, 10, 10))))
    Assert.Equal(Accepted { Kind = "application"; Id = "AP-1"; Version = None; Number = None }, application.Outcome)
    Assert.Equal(usd 130000L, outstanding applied invoice)

    // A payment received in October and not fully applied stops October from closing.
    let paid = applied |> recordPayment context { PaymentsReportsTests.payment "PAY-1" 50000L with CustomerId = abc.Id } |> ok
    let blocked, notReady = run paid (message "req-2" (ClosePeriod(2026, 10)))

    match notReady.Outcome with
    | Refused [ reason ] -> Assert.Equal("period-not-ready", reason.Code)
    | other -> failwith $"%A{other}"

    Assert.True((blocked = paid))
    let allocated = paid |> allocate context (PaymentsReportsTests.allocation "AL-1" "PAY-1" invoice.InvoiceId 50000L) |> ok
    let closed, closing = run allocated (message "req-3" (ClosePeriod(2026, 10)))
    Assert.Equal(Accepted { Kind = "period"; Id = "2026-10"; Version = None; Number = None }, closing.Outcome)
    Assert.Equal(Ledger.Closed, Ledger.periodState closed.Books.Ledger (DateOnly(2026, 10, 1)))
    // Closing again is the same; a locked period is not closed again.
    Assert.Equal(closing.Outcome, (run closed (message "req-4" (ClosePeriod(2026, 10))) |> snd).Outcome)
    let locked = { closed with Books = { closed.Books with Ledger = Ledger.lockPeriod context (2026, 10) closed.Books.Ledger } }
    Assert.Equal("period-locked", (match (run locked (message "req-5" (ClosePeriod(2026, 10))) |> snd).Outcome with Refused [ r ] -> r.Code | other -> $"%A{other}"))
    // A reversal undoes the payment's allocation by new entries; the payment stays in history.
    let reversed, reversal = run allocated (message "req-6" (ReversePayment("PAY-1", "Returned: insufficient funds", DateOnly(2026, 10, 22))))
    Assert.Equal(Accepted { Kind = "payment-reversal"; Id = "PAY-1"; Version = None; Number = None }, reversal.Outcome)
    Assert.True(reversed.Payments.ContainsKey "PAY-1")
    Assert.Equal(usd 130000L, outstanding reversed invoice)

// ---- Capabilities (SUM2-031, INV-SEC-001) ------------------------------------------------------

[<Fact>]
let ``every command, 1.0 to 1.2, is refused for a caller without the capability the page requires, and the books are unchanged`` () =
    let issued, invoice = issue context request (saveDraft context (draftFor [ consulting 10000L 15000L ]) (withCreditAccounts ()) |> ok) |> ok
    let drafted = start (saveDraft context { draftFor [ consulting 1000L 10000L ] with DraftId = "D-2" } (withCreditAccounts ()) |> ok)
    let r = start issued |> recordPayment context { PaymentsReportsTests.payment "PAY-1" 50000L with CustomerId = abc.Id } |> ok

    let table: (Command * Summa.Access.Access.Capability * Receivables) list =
        [ ProposeInvoice("P-1", abc.Id, None, "USD", [ "pub-1" ]), Summa.Access.Access.ProposeInvoice, r
          SubmitForReview("D-2", 1), Summa.Access.Access.CreateDraftInvoice, drafted
          IssueInvoice("D-2", 1, DateOnly(2026, 10, 7)), Summa.Access.Access.IssueInvoice, drafted
          VoidInvoice(invoice.InvoiceId, "Duplicate", DateOnly(2026, 10, 8)), Summa.Access.Access.VoidInvoice, start issued
          RecordPayment { PaymentsReportsTests.payment "PAY-2" 100L with CustomerId = abc.Id }, Summa.Access.Access.RecordPayment, r
          AllocatePayment("AL-1", "PAY-1", invoice.InvoiceId, usd 100L), Summa.Access.Access.AllocatePayment, r
          ApplyCreditMemo("AP-1", "CM-1", invoice.InvoiceId, usd 100L, DateOnly(2026, 10, 10)), Summa.Access.Access.ApplyCreditMemo, r
          ReversePayment("PAY-1", "Returned", DateOnly(2026, 10, 22)), Summa.Access.Access.ReversePayment, r
          ClosePeriod(2026, 9), Summa.Access.Access.ClosePeriod, r ]

    // Every command of the contract is in the table, each with the page's capability.
    Assert.Equal(9, table.Length)

    for command, capability, books in table do
        Assert.Equal(capability, capabilityOf command)
        let request = { RequestId = "req-x"; Command = command }
        let without = { everything with Capabilities = everything.Capabilities.Remove capability }
        let after, response = handle without host request books

        match response.Outcome with
        | Refused [ reason ] -> Assert.Equal("not-authorized", reason.Code)
        | other -> failwith $"%A{command} without {capability}: %A{other}"

        Assert.True((after = books), $"%A{command} changed the books without {capability}")
        // With the capability, it is not refused for authority.
        match (handle everything host request books |> snd).Outcome with
        | Refused reasons -> Assert.DoesNotContain(reasons, fun r -> r.Code = "not-authorized")
        | _ -> ()

[<Fact>]
let ``refusal codes are an open set: a 1.0 response carrying not-authorized, or a code no version defines, is read`` () =
    for code in [ "not-authorized"; "some-code-a-later-summa-adds" ] do
        let text =
            $"""{{"contract":"summa.invoice-commands","kind":"response","message":{{"outcome":{{"kind":"refused","reasons":[{{"code":"{code}","message":"Why."}}]}},"requestId":"req-1"}},"version":"1.0"}}"""

        Assert.Equal(Ok { RequestId = "req-1"; Outcome = Refused [ { Code = code; Message = "Why." } ] }, decodeResponse text)
        // Written by this Summa, the response is still a 1.0 message.
        Assert.Equal(text, encodeResponse { RequestId = "req-1"; Outcome = Refused [ { Code = code; Message = "Why." } ] })
