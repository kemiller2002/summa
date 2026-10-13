/// The external invoice command contract, `summa.invoice-commands` 1.0
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
    Assert.True(valid.Length >= 7)

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
    Assert.True(invalid.Length >= 12)

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
        """{"contract":"summa.invoice-commands","kind":"request","message":{"command":{"draftId":"D-1","expectedVersion":1,"kind":"submit-for-review","note":"added in 1.1"},"priority":"high","requestId":"req-9"},"version":"1.1"}"""

    Assert.Equal(Ok { RequestId = "req-9"; Command = SubmitForReview("D-1", 1) }, decodeRequest newer)
    let response = { RequestId = "req-9"; Outcome = Conflict 2 }
    Assert.Equal(Ok response, decodeResponse (encodeResponse response))

/// The organization's defaults, as the host supplies them.
let private host: Host =
    { Accounts = { TimeRevenue = "revenue"; FeeRevenue = "revenue"; ReimbursedExpenses = "revenue" }
      IssueRequest = fun draftId version date -> { request with DraftId = draftId; ExpectedVersion = version; IssueDate = date }
      VoidEntryId = fun invoiceId -> $"JE-VOID-{invoiceId}" }

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
