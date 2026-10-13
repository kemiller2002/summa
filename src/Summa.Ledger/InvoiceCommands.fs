/// Version 1 of Summa's external invoice command contract (INV-CONTRACT-002,
/// SUM3-011):
/// a request names one domain command and a response names its outcome,
/// both in a versioned envelope:
/// `{"contract":"summa.invoice-commands","kind":"request","message":{...},"version":"1.0"}`,
/// each written at the oldest version that defines it
/// (`docs/contracts/invoice-commands-1.1.md`).
///
/// - The commands are domain operations, never record patches
///   (INV-CONTRACT-003): propose an invoice, submit a draft for review,
///   issue a reviewed draft, void an invoice; and, from 1.1, record a payment
///   and allocate it to an invoice (SUM3-011).
/// - Who acts is not in the message. The host authenticates the caller and
///   passes its `Context`, so a message cannot claim to be someone else.
/// - Encoding is canonical (`Summa.Contracts.Json`), so equal messages have
///   equal bytes. Decoding treats the text as untrusted: every problem is
///   reported with its JSON path. A newer minor of major 1 is read, ignoring
///   members this version does not define; any other major is refused.
/// - `requestId` is the caller's idempotency key. The response carries it
///   back unchanged.
///
/// Pure.
module Summa.Ledger.InvoiceCommands

open System
open System.Text.RegularExpressions
open Summa.Contracts
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments

[<Literal>]
let Name = "summa.invoice-commands"

[<Literal>]
let Major = 1

/// 1.1 adds `record-payment` and `allocate-payment` (SUM3-011).
[<Literal>]
let Minor = 1

/// `1.1`, the newest version this reader knows.
let Version = $"{Major}.{Minor}"

/// What an external caller asks Summa to do.
type Command =
    /// Propose an invoice from imported, approved time (`Billing.propose`).
    | ProposeInvoice of proposalId: string * customerId: string * engagementId: string option * currency: string * time: string list
    /// Submit the draft version the caller reviewed (`Issuance.submitForReview`).
    | SubmitForReview of draftId: string * expectedVersion: int
    /// Issue the reviewed draft version (`Issuance.issueInvoice`, INV-CON-001).
    | IssueInvoice of draftId: string * expectedVersion: int * issueDate: DateOnly
    /// Void an unpaid invoice with a reason (`Corrections.voidInvoice`).
    | VoidInvoice of invoiceId: string * reason: string * date: DateOnly
    /// Since 1.1: record a payment received (`Payments.recordPayment`).
    | RecordPayment of Payment
    /// Since 1.1: apply part of a payment to an invoice (`Payments.allocate`).
    | AllocatePayment of allocationId: string * paymentId: string * invoiceId: string * amount: Money.Money

/// The contract minor version that first defines a command. A message is
/// written at the oldest version that defines it, so 1.0 messages keep
/// their bytes and a 1.0 reader can read them.
let sinceMinor =
    function
    | RecordPayment _
    | AllocatePayment _ -> 1
    | _ -> 0

type Request = { RequestId: string; Command: Command }

/// What the command produced.
type Produced =
    { Kind: string
      Id: string
      /// The draft's version, for a draft.
      Version: int option
      /// The customer-facing number, for an invoice.
      Number: string option }

/// Why Summa refused, in words a caller can act on.
type Refusal = { Code: string; Message: string }

type Outcome =
    | Accepted of Produced
    | Refused of Refusal list
    /// The draft is not the version the caller named; nothing changed.
    | Conflict of currentVersion: int

type Response = { RequestId: string; Outcome: Outcome }

// ---- Encoding --------------------------------------------------------------------------

let private envelope (minor: int) (kind: string) (message: Json) =
    Json.Object [ "contract", Json.String Name; "version", Json.String $"{Major}.{minor}"; "kind", Json.String kind; "message", message ]

let private money (m: Money.Money) =
    Json.Object [ "currency", Encode.text m.Currency; "minor", Json.Integer m.Minor ]

let private methods =
    [ Ach, "ach"; Check, "check"; Wire, "wire"; CreditCard, "credit-card"; Cash, "cash"; Other, "other" ]

let private command =
    function
    | ProposeInvoice(proposal, customer, engagement, currency, time) ->
        Json.Object
            [ "kind", Json.String "propose-invoice"
              "proposalId", Encode.text proposal
              "customerId", Encode.text customer
              "engagementId", Encode.optional Encode.text engagement
              "currency", Encode.text currency
              "time", Encode.list Encode.text time ]
    | SubmitForReview(draft, version) ->
        Json.Object [ "kind", Json.String "submit-for-review"; "draftId", Encode.text draft; "expectedVersion", Encode.integer version ]
    | IssueInvoice(draft, version, date) ->
        Json.Object
            [ "kind", Json.String "issue-invoice"
              "draftId", Encode.text draft
              "expectedVersion", Encode.integer version
              "issueDate", Encode.date date ]
    | VoidInvoice(invoice, reason, date) ->
        Json.Object [ "kind", Json.String "void-invoice"; "invoiceId", Encode.text invoice; "reason", Encode.text reason; "date", Encode.date date ]
    | RecordPayment p ->
        Json.Object
            [ "kind", Json.String "record-payment"
              "paymentId", Encode.text p.Id
              "customerId", Encode.text p.CustomerId
              "dateReceived", Encode.date p.DateReceived
              "amount", money p.Amount
              "method", Json.String(methods |> List.find (fun (m, _) -> m = p.Method) |> snd)
              "reference", Encode.text p.Reference
              "memo", Encode.optional Encode.text p.Memo ]
    | AllocatePayment(allocation, payment, invoice, amount) ->
        Json.Object
            [ "kind", Json.String "allocate-payment"
              "allocationId", Encode.text allocation
              "paymentId", Encode.text payment
              "invoiceId", Encode.text invoice
              "amount", money amount ]

let encodeRequest (request: Request) =
    envelope (sinceMinor request.Command) "request" (Json.Object [ "requestId", Encode.text request.RequestId; "command", command request.Command ])
    |> Json.serialize

let private outcome =
    function
    | Accepted produced ->
        Json.Object
            [ "kind", Json.String "accepted"
              "produced",
              Json.Object
                  [ "kind", Encode.text produced.Kind
                    "id", Encode.text produced.Id
                    "version", Encode.optional Encode.integer produced.Version
                    "number", Encode.optional Encode.text produced.Number ] ]
    | Refused reasons ->
        Json.Object
            [ "kind", Json.String "refused"
              "reasons", reasons |> Encode.list (fun r -> Json.Object [ "code", Encode.text r.Code; "message", Encode.text r.Message ]) ]
    | Conflict current -> Json.Object [ "kind", Json.String "conflict"; "currentVersion", Encode.integer current ]

/// Responses have not changed since 1.0, so they are written at 1.0.
let encodeResponse (response: Response) =
    envelope 0 "response" (Json.Object [ "requestId", Encode.text response.RequestId; "outcome", outcome response.Outcome ])
    |> Json.serialize

// ---- Decoding --------------------------------------------------------------------------

/// Applies a decoded function to a decoded argument, keeping every problem.
let private (<*>) (f: Decoded<'a -> 'b>) (a: Decoded<'a>) : Decoded<'b> = Decode.apply f a

let private versionPattern = Regex(@"^(\d+)\.(\d+)$", RegexOptions.CultureInvariant)
let private currencyPattern = Regex(@"^[A-Z]{3}$", RegexOptions.CultureInvariant)

/// Text that is not blank.
let private name (path: string) (json: Json) =
    Decode.text path json
    |> Result.bind (fun s -> if String.IsNullOrWhiteSpace s then Decode.problem path "must not be blank" else Ok s)

let private positive (path: string) (json: Json) =
    Decode.integer path json |> Result.bind (fun n -> if n >= 1 then Ok n else Decode.problem path "must be 1 or more")

/// The envelope checked, and the message's minor version: a minor newer than
/// this reader's is read leniently, ignoring members it adds.
let private open' (kind: string) (text: string) : Decoded<int * Json> =
    Json.parse text
    |> Result.mapError (fun message -> [ { Path = "$"; Message = message } ])
    |> Result.bind (fun json ->
        let version =
            Decode.member' Decode.text "$" "version" json
            |> Result.bind (fun v ->
                let m = versionPattern.Match v

                if not m.Success then Decode.problem "$.version" "must be major.minor, for example 1.0"
                elif int m.Groups[1].Value <> Major then Decode.problem "$.version" $"major version {m.Groups[1].Value} is not supported; this reader reads {Major}.x"
                else Ok(int m.Groups[2].Value))

        let contract =
            Decode.member' Decode.text "$" "contract" json
            |> Result.bind (fun c -> if c = Name then Ok() else Decode.problem "$.contract" $"must be {Name}")

        let kindOk =
            Decode.member' Decode.text "$" "kind" json
            |> Result.bind (fun k -> if k = kind then Ok() else Decode.problem "$.kind" $"must be {kind}")

        Decode.map2 (fun minor () -> minor) version (Decode.checks [ contract; kindOk ])
        |> Result.bind (fun minor ->
            Decode.closed (minor <= Minor) "$" [ "contract"; "kind"; "message"; "version" ] json
            |> Result.bind (fun () -> Decode.field "$" "message" json)
            |> Result.map (fun message -> minor, message)))

let private moneyOf (path: string) (json: Json) : Decoded<Money.Money> =
    let currency =
        Decode.member' Decode.text path "currency" json
        |> Result.bind (fun c -> if currencyPattern.IsMatch c then Ok c else Decode.problem $"{path}.currency" "must be a three-letter ISO 4217 code")

    let minor =
        match Decode.field path "minor" json with
        | Ok(Json.Integer n) when n > 0L -> Ok n
        | Ok(Json.Integer _) -> Decode.problem $"{path}.minor" "must be more than zero"
        | Ok _ -> Decode.problem $"{path}.minor" "must be a whole number of minor units"
        | Error e -> Error e

    Decode.closed true path [ "currency"; "minor" ] json
    |> Result.bind (fun () -> Decode.map2 (fun c m -> ({ Currency = c; Minor = m }: Money.Money)) currency minor)

let private commandOf (minor: int) (path: string) (json: Json) : Decoded<Command> =
    let strict = minor <= Minor
    let get decode field = Decode.member' decode path field json
    let closed names = Decode.closed strict path ("kind" :: names) json

    // A command a message's version does not define is refused at its kind.
    let since (needed: int) (decoded: unit -> Decoded<Command>) =
        if minor < needed then Decode.problem $"{path}.kind" $"needs version {Major}.{needed} or later" else decoded ()

    Decode.member' Decode.text path "kind" json
    |> Result.bind (function
        | "propose-invoice" ->
            let time =
                get (Decode.list name) "time"
                |> Result.bind (fun ids ->
                    if ids.IsEmpty then Decode.problem $"{path}.time" "must name at least one publication"
                    elif (List.distinct ids).Length <> ids.Length then Decode.problem $"{path}.time" "must not name a publication twice"
                    else Ok ids)

            let currency =
                get Decode.text "currency"
                |> Result.bind (fun c -> if currencyPattern.IsMatch c then Ok c else Decode.problem $"{path}.currency" "must be a three-letter ISO 4217 code")

            Decode.checks [ closed [ "currency"; "customerId"; "engagementId"; "proposalId"; "time" ] ]
            |> Result.bind (fun () ->
                Ok(fun p c e cur t -> ProposeInvoice(p, c, e, cur, t))
                <*> (get name "proposalId")
                <*> (get name "customerId")
                <*> (get (Decode.optional name) "engagementId")
                <*> currency
                <*> time)
        | "submit-for-review" ->
            closed [ "draftId"; "expectedVersion" ]
            |> Result.bind (fun () -> Decode.map2 (fun d v -> SubmitForReview(d, v)) (get name "draftId") (get positive "expectedVersion"))
        | "issue-invoice" ->
            closed [ "draftId"; "expectedVersion"; "issueDate" ]
            |> Result.bind (fun () ->
                Ok(fun d v date -> IssueInvoice(d, v, date))
                <*> (get name "draftId")
                <*> (get positive "expectedVersion")
                <*> (get Decode.date "issueDate"))
        | "void-invoice" ->
            closed [ "date"; "invoiceId"; "reason" ]
            |> Result.bind (fun () ->
                Ok(fun i r date -> VoidInvoice(i, r, date))
                <*> (get name "invoiceId")
                <*> (get name "reason")
                <*> (get Decode.date "date"))
        | "record-payment" ->
            since 1 (fun () ->
                let method =
                    get Decode.text "method"
                    |> Result.bind (fun text ->
                        match methods |> List.tryFind (fun (_, n) -> n = text) with
                        | Some(m, _) -> Ok m
                        | None -> Decode.problem $"{path}.method" "must be ach, check, wire, credit-card, cash or other")

                closed [ "amount"; "customerId"; "dateReceived"; "memo"; "method"; "paymentId"; "reference" ]
                |> Result.bind (fun () ->
                    Ok(fun id customer date amount m reference memo ->
                        RecordPayment
                            { Id = id
                              CustomerId = customer
                              DateReceived = date
                              Amount = amount
                              Method = m
                              Reference = reference
                              Memo = memo })
                    <*> get name "paymentId"
                    <*> get name "customerId"
                    <*> get Decode.date "dateReceived"
                    <*> get moneyOf "amount"
                    <*> method
                    <*> get Decode.text "reference"
                    <*> get (Decode.optional Decode.text) "memo"))
        | "allocate-payment" ->
            since 1 (fun () ->
                closed [ "allocationId"; "amount"; "invoiceId"; "paymentId" ]
                |> Result.bind (fun () ->
                    Ok(fun a p i amount -> AllocatePayment(a, p, i, amount))
                    <*> get name "allocationId"
                    <*> get name "paymentId"
                    <*> get name "invoiceId"
                    <*> get moneyOf "amount"))
        | other -> Decode.problem $"{path}.kind" $"'{other}' is not a command of this contract")

/// A request from untrusted text: every problem, each with its path.
let decodeRequest (text: string) : Decoded<Request> =
    open' "request" text
    |> Result.bind (fun (minor, message) ->
        Decode.closed (minor <= Minor) "$.message" [ "command"; "requestId" ] message
        |> Result.bind (fun () ->
            Decode.map2
                (fun id command -> { RequestId = id; Command = command })
                (Decode.member' name "$.message" "requestId" message)
                (Decode.member' (commandOf minor) "$.message" "command" message)))

let private outcomeOf (strict: bool) (path: string) (json: Json) : Decoded<Outcome> =
    Decode.member' Decode.text path "kind" json
    |> Result.bind (function
        | "accepted" ->
            Decode.closed strict path [ "kind"; "produced" ] json
            |> Result.bind (fun () ->
                Decode.member'
                    (fun p j ->
                        Decode.closed strict p [ "id"; "kind"; "number"; "version" ] j
                        |> Result.bind (fun () ->
                            Ok(fun k i v n -> Accepted { Kind = k; Id = i; Version = v; Number = n })
                            <*> (Decode.member' name p "kind" j)
                            <*> (Decode.member' name p "id" j)
                            <*> (Decode.member' (Decode.optional positive) p "version" j)
                            <*> (Decode.member' (Decode.optional name) p "number" j)))
                    path
                    "produced"
                    json)
        | "refused" ->
            Decode.closed strict path [ "kind"; "reasons" ] json
            |> Result.bind (fun () ->
                Decode.member'
                    (Decode.list (fun p j ->
                        Decode.closed strict p [ "code"; "message" ] j
                        |> Result.bind (fun () -> Decode.map2 (fun c m -> { Code = c; Message = m }) (Decode.member' name p "code" j) (Decode.member' name p "message" j))))
                    path
                    "reasons"
                    json
                |> Result.bind (fun reasons -> if reasons.IsEmpty then Decode.problem $"{path}.reasons" "must give at least one reason" else Ok(Refused reasons)))
        | "conflict" ->
            Decode.closed strict path [ "currentVersion"; "kind" ] json
            |> Result.bind (fun () -> Decode.member' positive path "currentVersion" json |> Result.map Conflict)
        | other -> Decode.problem $"{path}.kind" $"'{other}' is not an outcome of this contract")

/// A response from untrusted text.
let decodeResponse (text: string) : Decoded<Response> =
    open' "response" text
    |> Result.bind (fun (minor, message) ->
        let strict = minor <= Minor
        Decode.closed strict "$.message" [ "outcome"; "requestId" ] message
        |> Result.bind (fun () ->
            Decode.map2
                (fun id outcome -> { RequestId = id; Outcome = outcome })
                (Decode.member' name "$.message" "requestId" message)
                (Decode.member' (outcomeOf strict) "$.message" "outcome" message)))

// ---- Handling ---------------------------------------------------------------------------

/// What the host supplies: the accounts proposals post to, the issue request
/// for a draft (the organization's defaults), the id of a void's entry, and
/// the accounts and entry id an allocation posts to.
[<NoEquality; NoComparison>]
type Host =
    { Accounts: Billing.BillingAccounts
      IssueRequest: string -> int -> DateOnly -> IssueRequest
      VoidEntryId: string -> string
      CashAccountId: string
      ReceivableAccountId: string
      AllocationEntryId: string -> string }

let private refusal code (problem: obj) = { Code = code; Message = $"%A{problem}" }

let private ofBlockers (draftId: string) (r: Receivables) (blockers: Issuance.Blocker list) =
    match blockers |> List.tryFind (fun b -> b.Code = "stale-version"), r.Books.Drafts.TryFind draftId with
    | Some _, Some draft -> Conflict draft.Version
    | _ -> Refused(blockers |> List.map (fun b -> { Code = b.Code; Message = $"{b.Explanation} {b.Resolution}" }))

/// Runs one request as the host's authenticated `context`: the books after
/// it (unchanged unless accepted) and the response.
let handle (context: Context) (host: Host) (request: Request) (r: Receivables) : Receivables * Response =
    let respond (books, outcome) = books, { RequestId = request.RequestId; Outcome = outcome }

    match request.Command with
    | ProposeInvoice(proposalId, customerId, engagementId, currency, time) ->
        let proposal: Billing.ProposalRequest =
            { ProposalId = proposalId
              CustomerId = customerId
              EngagementId = engagementId
              Currency = currency
              Time = time
              Grouping = []
              FixedFee = false
              Milestones = []
              Expenses = []
              Manual = []
              Accounts = host.Accounts }

        match Billing.propose context proposal r with
        | Ok next -> next, Accepted { Kind = "proposal"; Id = proposalId; Version = None; Number = None }
        | Error problems -> r, Refused(problems |> List.map (refusal "proposal-refused"))
    | SubmitForReview(draftId, expected) ->
        match r.Books.Drafts.TryFind draftId with
        | Some draft when draft.Version <> expected -> r, Conflict draft.Version
        | _ ->
            match Issuance.submitForReview context (host.IssueRequest draftId expected (DateOnly.FromDateTime context.When.UtcDateTime)) r with
            | Ok next -> next, Accepted { Kind = "draft"; Id = draftId; Version = Some expected; Number = None }
            | Error blockers -> r, ofBlockers draftId r blockers
    | IssueInvoice(draftId, expected, issueDate) ->
        match Issuance.issueInvoice context (host.IssueRequest draftId expected issueDate) r with
        | Ok(next, invoice) -> next, Accepted { Kind = "invoice"; Id = invoice.InvoiceId; Version = None; Number = Some invoice.Number }
        | Error blockers -> r, ofBlockers draftId r blockers
    | VoidInvoice(invoiceId, reason, date) ->
        let voiding: Corrections.VoidRequest = { InvoiceId = invoiceId; Reason = reason; Date = date; JournalEntryId = host.VoidEntryId invoiceId }

        match Corrections.voidInvoice context voiding r with
        | Ok next -> next, Accepted { Kind = "invoice-void"; Id = invoiceId; Version = None; Number = None }
        | Error problems -> r, Refused(problems |> List.map (refusal "void-refused"))
    | RecordPayment payment ->
        match Payments.recordPayment context payment r with
        | Ok next -> next, Accepted { Kind = "payment"; Id = payment.Id; Version = None; Number = None }
        | Error problems -> r, Refused(problems |> List.map (refusal "payment-refused"))
    | AllocatePayment(allocationId, paymentId, invoiceId, amount) ->
        let allocation: AllocationRequest =
            { AllocationId = allocationId
              PaymentId = paymentId
              InvoiceId = invoiceId
              Amount = amount
              JournalEntryId = host.AllocationEntryId allocationId
              CashAccountId = host.CashAccountId
              ReceivableAccountId = host.ReceivableAccountId }

        match Payments.allocate context allocation r with
        | Ok next -> next, Accepted { Kind = "allocation"; Id = allocationId; Version = None; Number = None }
        | Error problems -> r, Refused(problems |> List.map (refusal "allocation-refused"))
    |> respond
