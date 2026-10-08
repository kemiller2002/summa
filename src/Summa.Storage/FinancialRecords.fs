/// Summa's books as authoritative Arca records (SUM0-002, SUM0-012..014,
/// SUM0-027, SUM0-044): one financial object or event per file, at a
/// deterministic path, in canonical JSON, inside the organization's folder.
///
/// | Record type | Path under `records/` | Mutability |
/// |---|---|---|
/// | `summa.account` | `summa.account/<id>` | mutable |
/// | `summa.period` | `summa.period/<yyyy-mm>` | mutable |
/// | `summa.entry` | `summa.entry/<yyyy>/<mm>/<id>` | immutable |
/// | `summa.customer` | `summa.customer/<id>` | mutable |
/// | `summa.draft` | `summa.draft/<id>` | mutable, deleted when issued |
/// | `summa.invoice` | `summa.invoice/<yyyy>/<id>` | immutable |
/// | `summa.delivery` | `summa.delivery/<invoice id>` | mutable |
/// | `summa.obligation` | `summa.obligation/<id>` | mutable |
/// | `summa.payment` | `summa.payment/<yyyy>/<id>` | immutable |
/// | `summa.allocation` | `summa.allocation/<id>` | immutable |
/// | `summa.audit` | `summa.audit/<yyyy>/<mm>/<id>` | immutable |
/// | `summa.invoice-void` | `summa.invoice-void/<invoice id>` | immutable |
/// | `summa.engagement` | `summa.engagement/<id>` | mutable |
/// | `summa.expense` | `summa.expense/<yyyy>/<id>` | immutable |
/// | `summa.time` | `summa.time/<yyyy>/<publication id>` | immutable |
/// | `summa.time-withdrawal` | `summa.time-withdrawal/<publication id>` | immutable |
/// | `summa.proposal` | `summa.proposal/<id>` | mutable |
/// | `summa.rate-card` | `summa.rate-card/rates` | mutable |
/// | `summa.billing-review` | `summa.billing-review/<id>` | mutable |
///
/// Posted entries, issued invoices, payments, allocations and audit events
/// are written once. A reversal is a new entry naming the one it reverses,
/// and "Reversed" is derived from it on load, so nothing posted is rewritten
/// (SUM0-027). Derived values (journal order, idempotency keys, invoice
/// status) are recomputed on load, never stored (SUM0-014).
///
/// Pure.
module Summa.Storage.FinancialRecords

open System
open System.Security.Cryptography
open System.Text
open Arca
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Sources
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Storage.Codec
open Summa.Storage.Diagnostics

let private recordType text =
    match RecordType.create text with
    | Ok found -> found
    | Error _ -> invalidOp ("internal: not a record type: " + text)

let accountType = recordType "summa.account"
let periodType = recordType "summa.period"
let entryType = recordType "summa.entry"
let customerType = recordType "summa.customer"
let draftType = recordType "summa.draft"
let invoiceType = recordType "summa.invoice"
let deliveryType = recordType "summa.delivery"
let obligationType = recordType "summa.obligation"
let paymentType = recordType "summa.payment"
let allocationType = recordType "summa.allocation"
let auditType = recordType "summa.audit"
let creditType = recordType "summa.credit"
let depositType = recordType "summa.deposit"
let creditMemoType = recordType "summa.credit-memo"
let applicationType = recordType "summa.application"
let refundType = recordType "summa.refund"
let reversalType = recordType "summa.payment-reversal"
let writeOffType = recordType "summa.write-off"
let voidType = recordType "summa.invoice-void"
let engagementType = recordType "summa.engagement"
let expenseType = recordType "summa.expense"
let timeType = recordType "summa.time"
let withdrawalType = recordType "summa.time-withdrawal"
let proposalType = recordType "summa.proposal"
let rateCardType = recordType "summa.rate-card"
let reviewType = recordType "summa.billing-review"

/// Every financial record type, with its mutability and whether a record of
/// it may ever be deleted.
let types: (RecordType * Mutability * bool) list =
    [ accountType, Mutability.Mutable, false
      periodType, Mutability.Mutable, false
      entryType, Mutability.Immutable, false
      customerType, Mutability.Mutable, false
      draftType, Mutability.Mutable, true
      invoiceType, Mutability.Immutable, false
      deliveryType, Mutability.Mutable, false
      obligationType, Mutability.Mutable, false
      paymentType, Mutability.Immutable, false
      allocationType, Mutability.Immutable, false
      auditType, Mutability.Immutable, false
      creditType, Mutability.Immutable, false
      depositType, Mutability.Immutable, false
      creditMemoType, Mutability.Immutable, false
      applicationType, Mutability.Immutable, false
      refundType, Mutability.Immutable, false
      reversalType, Mutability.Immutable, false
      writeOffType, Mutability.Immutable, false
      voidType, Mutability.Immutable, false
      engagementType, Mutability.Mutable, false
      expenseType, Mutability.Immutable, false
      timeType, Mutability.Immutable, false
      withdrawalType, Mutability.Immutable, false
      proposalType, Mutability.Mutable, false
      rateCardType, Mutability.Mutable, false
      reviewType, Mutability.Mutable, false ]

/// Schema support for every financial record type: version 1 throughout.
let schemas =
    types
    |> List.map (fun (t, _, _) ->
        { Type = t
          OldestReadable = 1
          Current = 1 })

let private mutabilityOf (t: RecordType) =
    types |> List.find (fun (found, _, _) -> found = t) |> fun (_, m, _) -> m

let deletable (t: RecordType) =
    types |> List.exists (fun (found, _, d) -> found = t && d)

let isFinancial (t: RecordType) =
    types |> List.exists (fun (found, _, _) -> found = t)

// ---- Values -----------------------------------------------------------------

let private money (m: Money) =
    Json.objectOf [ "currency", Json.String m.Currency; "minor", Json.Number(decimal m.Minor) ]

let private moneyOf (value: Json) : Decoded<Money> =
    decode {
        do! closed [ "currency"; "minor" ] value
        let! currency = text "currency" value
        let! minor = long "minor" value
        return { Currency = currency; Minor = minor }
    }

let private moneyField name value = field name value |> Result.bind moneyOf

let private dimensions (d: Dimensions) =
    Json.objectOf
        [ "client", optionalString d.Client
          "project", optionalString d.Project
          "engagement", optionalString d.Engagement
          "workItem", optionalString d.WorkItem
          "product", optionalString d.Product ]

let private dimensionsOf (value: Json) : Decoded<Dimensions> =
    decode {
        do! closed [ "client"; "engagement"; "product"; "project"; "workItem" ] value
        let! client = optionalText "client" value
        let! project = optionalText "project" value
        let! engagement = optionalText "engagement" value
        let! workItem = optionalText "workItem" value
        let! product = optionalText "product" value

        return
            { Client = client
              Project = project
              Engagement = engagement
              WorkItem = workItem
              Product = product }
    }

let private line (l: Line) =
    let side, amount =
        match l.Side with
        | Debit m -> "debit", m
        | Credit m -> "credit", m

    Json.objectOf
        [ "accountId", Json.String l.AccountId
          "side", Json.String side
          "amount", money amount
          "memo", optionalString l.Memo
          "dimensions", dimensions l.Dimensions ]

let private lineOf (value: Json) : Decoded<Line> =
    decode {
        do! closed [ "accountId"; "amount"; "dimensions"; "memo"; "side" ] value
        let! accountId = text "accountId" value
        let! amount = moneyField "amount" value

        let! side =
            text "side" value
            |> Result.bind (function
                | "debit" -> Ok(Debit amount)
                | "credit" -> Ok(Credit amount)
                | other -> Error $"'{other}' is not debit or credit")

        let! memo = optionalText "memo" value
        let! dims = field "dimensions" value |> Result.bind dimensionsOf

        return
            { AccountId = accountId
              Side = side
              Memo = memo
              Dimensions = dims }
    }

let private terms =
    function
    | DueOnReceipt -> Json.objectOf [ "kind", Json.String "due-on-receipt" ]
    | Net days -> Json.objectOf [ "kind", Json.String "net"; "days", number days ]
    | CustomDate d -> Json.objectOf [ "kind", Json.String "custom-date"; "date", Json.String(dateText d) ]

let private termsOf (value: Json) : Decoded<PaymentTerms> =
    text "kind" value
    |> Result.bind (function
        | "due-on-receipt" -> closed [ "kind" ] value |> Result.map (fun () -> DueOnReceipt)
        | "net" -> closed [ "days"; "kind" ] value |> Result.bind (fun () -> integer "days" value |> Result.map Net)
        | "custom-date" -> closed [ "date"; "kind" ] value |> Result.bind (fun () -> date "date" value |> Result.map CustomDate)
        | other -> Error $"'{other}' is not a payment term")

let private discount =
    function
    | Percent bp -> Json.objectOf [ "kind", Json.String "percent"; "basisPoints", number bp ]
    | Fixed m -> Json.objectOf [ "kind", Json.String "fixed"; "amount", money m ]

let private discountOf (value: Json) : Decoded<Discount> =
    text "kind" value
    |> Result.bind (function
        | "percent" -> closed [ "basisPoints"; "kind" ] value |> Result.bind (fun () -> integer "basisPoints" value |> Result.map Percent)
        | "fixed" -> closed [ "amount"; "kind" ] value |> Result.bind (fun () -> moneyField "amount" value |> Result.map Fixed)
        | other -> Error $"'{other}' is not a discount")

let private invoiceDiscount (d: InvoiceDiscount) =
    Json.objectOf [ "label", Json.String d.Label; "rule", discount d.Rule ]

let private invoiceDiscountOf (value: Json) : Decoded<InvoiceDiscount> =
    decode {
        do! closed [ "label"; "rule" ] value
        let! label = text "label" value
        let! rule = field "rule" value |> Result.bind discountOf
        return { Label = label; Rule = rule }
    }

let private optionalOf (decoder: Json -> Decoded<'a>) (name: string) (value: Json) : Decoded<'a option> =
    match Json.field name value with
    | None
    | Some Json.Null -> Ok None
    | Some found -> decoder found |> Result.map Some

let private optionalJson (encoder: 'a -> Json) (value: 'a option) =
    value |> Option.map encoder |> Option.defaultValue Json.Null

let private termsSourceName =
    function
    | InvoiceTerms -> "invoice"
    | EngagementTerms -> "engagement"
    | CustomerTerms -> "customer"
    | SystemTerms -> "system"

let private termsSourceOf =
    function
    | "invoice" -> Ok InvoiceTerms
    | "engagement" -> Ok EngagementTerms
    | "customer" -> Ok CustomerTerms
    | "system" -> Ok SystemTerms
    | other -> Error $"'{other}' is not where terms come from"

let private strings (name: string) value : Decoded<string list> =
    list
        name
        (function
        | Json.String s -> Ok s
        | _ -> Error $"'{name}' holds something other than text")
        value

let private timeReference (t: TimeReference) =
    Json.objectOf
        [ "publicationId", Json.String t.PublicationId
          "activityId", Json.String t.ActivityId
          "revision", number t.Revision
          "minutes", number t.Minutes ]

let private timeReferenceOf (value: Json) : Decoded<TimeReference> =
    decode {
        do! closed [ "activityId"; "minutes"; "publicationId"; "revision" ] value
        let! publication = text "publicationId" value
        let! activity = text "activityId" value
        let! revision = integer "revision" value
        let! minutes = integer "minutes" value

        return
            { PublicationId = publication
              ActivityId = activity
              Revision = revision
              Minutes = minutes }
    }

let private lineSource =
    function
    | ManualLine -> Json.objectOf [ "kind", Json.String "manual" ]
    | TimeSource refs -> Json.objectOf [ "kind", Json.String "time"; "entries", Json.Array(List.map timeReference refs) ]
    | FixedFeeSource e -> Json.objectOf [ "kind", Json.String "fixed-fee"; "engagementId", Json.String e ]
    | MilestoneSource(e, m) -> Json.objectOf [ "kind", Json.String "milestone"; "engagementId", Json.String e; "milestoneId", Json.String m ]
    | ExpenseSource x -> Json.objectOf [ "kind", Json.String "expense"; "expenseId", Json.String x ]

let private lineSourceOf (value: Json) : Decoded<LineSource> =
    text "kind" value
    |> Result.bind (function
        | "manual" -> closed [ "kind" ] value |> Result.map (fun () -> ManualLine)
        | "time" -> closed [ "entries"; "kind" ] value |> Result.bind (fun () -> list "entries" timeReferenceOf value |> Result.map TimeSource)
        | "fixed-fee" -> closed [ "engagementId"; "kind" ] value |> Result.bind (fun () -> text "engagementId" value |> Result.map FixedFeeSource)
        | "milestone" ->
            decode {
                do! closed [ "engagementId"; "kind"; "milestoneId" ] value
                let! e = text "engagementId" value
                let! m = text "milestoneId" value
                return MilestoneSource(e, m)
            }
        | "expense" -> closed [ "expenseId"; "kind" ] value |> Result.bind (fun () -> text "expenseId" value |> Result.map ExpenseSource)
        | other -> Error $"'{other}' is not a line source")

let private rateSourceNames =
    [ InvoiceOverride, "invoice-override"
      EngagementAgreement, "engagement"
      ProjectAgreement, "project"
      CustomerDefault, "customer"
      PersonRate, "person"
      RoleRate, "role"
      SystemDefault, "system" ]

let private rateProvenance (p: RateProvenance) =
    Json.objectOf
        [ "source", Json.String(rateSourceNames |> List.find (fun (s, _) -> s = p.RateSource) |> snd)
          "reference", Json.String p.Reference
          "override",
          p.Override
          |> Option.map (fun o ->
              Json.objectOf
                  [ "previous", optionalJson money o.Previous
                    "reason", Json.String o.Reason
                    "actor", Json.String o.Actor
                    "at", Json.String(preciseTimestamp o.At) ])
          |> Option.defaultValue Json.Null ]

let private overrideOf (o: Json) : Decoded<RateOverride> =
    decode {
        do! closed [ "actor"; "at"; "previous"; "reason" ] o
        let! previous = optionalOf moneyOf "previous" o
        let! reason = text "reason" o
        let! actor = text "actor" o
        let! at = preciseInstant "at" o

        return
            { Previous = previous
              Reason = reason
              Actor = actor
              At = at }
    }

let private rateProvenanceOf (value: Json) : Decoded<RateProvenance> =
    decode {
        do! closed [ "override"; "reference"; "source" ] value
        let! name = text "source" value

        let! source =
            match rateSourceNames |> List.tryFind (fun (_, n) -> n = name) with
            | Some(s, _) -> Ok s
            | None -> Error $"'{name}' is not a rate source"

        let! reference = text "reference" value
        let! change = optionalOf overrideOf "override" value

        return
            { RateSource = source
              Reference = reference
              Override = change }
    }

let private groupByNames =
    [ ByProject, "project"
      ByEngagement, "engagement"
      ByPerson, "person"
      ByActivityType, "activity-type"
      ByServiceMonth, "service-month" ]

let private groupByOf (value: Json) : Decoded<GroupBy> =
    match value with
    | Json.String name ->
        match groupByNames |> List.tryFind (fun (_, n) -> n = name) with
        | Some(g, _) -> Ok g
        | None -> Error $"'{name}' is not a grouping"
    | _ -> Error "a grouping is text"

let private milestoneAmountJson =
    function
    | ShareOfFee bp -> Json.objectOf [ "kind", Json.String "share-of-fee"; "basisPoints", number bp ]
    | MilestoneFixed m -> Json.objectOf [ "kind", Json.String "fixed"; "amount", money m ]

let private milestoneAmountOf (value: Json) : Decoded<MilestoneAmount> =
    text "kind" value
    |> Result.bind (function
        | "share-of-fee" -> closed [ "basisPoints"; "kind" ] value |> Result.bind (fun () -> integer "basisPoints" value |> Result.map ShareOfFee)
        | "fixed" -> closed [ "amount"; "kind" ] value |> Result.bind (fun () -> moneyField "amount" value |> Result.map MilestoneFixed)
        | other -> Error $"'{other}' is not a milestone amount")

let private milestone (m: Milestone) =
    Json.objectOf
        [ "id", Json.String m.Id
          "label", Json.String m.Label
          "amount", milestoneAmountJson m.Amount
          "completedOn", optionalJson (dateText >> Json.String) m.CompletedOn ]

let private milestoneOf (value: Json) : Decoded<Milestone> =
    decode {
        do! closed [ "amount"; "completedOn"; "id"; "label" ] value
        let! id = text "id" value
        let! label = text "label" value
        let! amount = field "amount" value |> Result.bind milestoneAmountOf
        let! completed = optionalDate "completedOn" value

        return
            { Id = id
              Label = label
              Amount = amount
              CompletedOn = completed }
    }

let private origin =
    function
    | OriginUnknown -> Json.objectOf [ "kind", Json.String "unknown" ]
    | OriginKnown(method, observation, execution) ->
        Json.objectOf
            [ "kind", Json.String "known"
              "method", Json.String method
              "observation", optionalString observation
              "executionId", optionalString execution ]

let private originOf (value: Json) : Decoded<TimeOrigin> =
    text "kind" value
    |> Result.bind (function
        | "unknown" -> closed [ "kind" ] value |> Result.map (fun () -> OriginUnknown)
        | "known" ->
            decode {
                do! closed [ "executionId"; "kind"; "method"; "observation" ] value
                let! method = text "method" value
                let! observation = optionalText "observation" value
                let! execution = optionalText "executionId" value
                return OriginKnown(method, observation, execution)
            }
        | other -> Error $"'{other}' is not an origin")

let private scope =
    function
    | ForEngagement id -> Json.objectOf [ "kind", Json.String "engagement"; "id", Json.String id ]
    | ForProject id -> Json.objectOf [ "kind", Json.String "project"; "id", Json.String id ]
    | ForCustomer id -> Json.objectOf [ "kind", Json.String "customer"; "id", Json.String id ]
    | ForPerson id -> Json.objectOf [ "kind", Json.String "person"; "id", Json.String id ]
    | ForRole id -> Json.objectOf [ "kind", Json.String "role"; "id", Json.String id ]
    | Everyone -> Json.objectOf [ "kind", Json.String "everyone"; "id", Json.Null ]

let private scopeOf (value: Json) : Decoded<RateScope> =
    decode {
        do! closed [ "id"; "kind" ] value
        let! kind = text "kind" value
        let! id = optionalText "id" value

        return!
            match kind, id with
            | "engagement", Some id -> Ok(ForEngagement id)
            | "project", Some id -> Ok(ForProject id)
            | "customer", Some id -> Ok(ForCustomer id)
            | "person", Some id -> Ok(ForPerson id)
            | "role", Some id -> Ok(ForRole id)
            | "everyone", None -> Ok Everyone
            | other, _ -> Error $"'{other}' is not a rate scope with the id it needs"
    }

let private proposalState =
    function
    | Proposed -> Json.objectOf [ "kind", Json.String "proposed"; "invoiceId", Json.Null ]
    | ReadyForReview -> Json.objectOf [ "kind", Json.String "ready-for-review"; "invoiceId", Json.Null ]
    | Abandoned -> Json.objectOf [ "kind", Json.String "abandoned"; "invoiceId", Json.Null ]
    | Accepted invoice -> Json.objectOf [ "kind", Json.String "accepted"; "invoiceId", Json.String invoice ]

let private proposalStateOf (value: Json) : Decoded<ProposalState> =
    decode {
        do! closed [ "invoiceId"; "kind" ] value
        let! kind = text "kind" value
        let! invoice = optionalText "invoiceId" value

        return!
            match kind, invoice with
            | "proposed", None -> Ok Proposed
            | "ready-for-review", None -> Ok ReadyForReview
            | "abandoned", None -> Ok Abandoned
            | "accepted", Some id -> Ok(Accepted id)
            | other, _ -> Error $"'{other}' is not a proposal state with the invoice it needs"
    }

let private invoiceLine (l: InvoiceLine) =
    Json.objectOf
        [ "description", Json.String l.Description
          "quantityThousandths", Json.Number(decimal l.QuantityThousandths)
          "unitPrice", money l.UnitPrice
          "revenueAccountId", Json.String l.RevenueAccountId
          "project", optionalString l.Project
          "workItem", optionalString l.WorkItem
          "discount", optionalJson discount l.Discount
          "source", lineSource l.Source
          "rate", optionalJson rateProvenance l.Rate ]

let private invoiceLineOf (value: Json) : Decoded<InvoiceLine> =
    decode {
        do! closed [ "description"; "discount"; "project"; "quantityThousandths"; "rate"; "revenueAccountId"; "source"; "unitPrice"; "workItem" ] value
        let! description = text "description" value
        let! quantity = long "quantityThousandths" value
        let! price = moneyField "unitPrice" value
        let! revenue = text "revenueAccountId" value
        let! project = optionalText "project" value
        let! workItem = optionalText "workItem" value
        let! lineDiscount = optionalOf discountOf "discount" value
        let! source = field "source" value |> Result.bind lineSourceOf
        let! rate = optionalOf rateProvenanceOf "rate" value

        return
            { Description = description
              QuantityThousandths = quantity
              UnitPrice = price
              RevenueAccountId = revenue
              Project = project
              WorkItem = workItem
              Discount = lineDiscount
              Source = source
              Rate = rate }
    }

let private accountTypeName =
    function
    | Asset -> "asset"
    | Liability -> "liability"
    | Equity -> "equity"
    | Revenue -> "revenue"
    | Expense -> "expense"

let private accountTypeOf =
    function
    | "asset" -> Ok Asset
    | "liability" -> Ok Liability
    | "equity" -> Ok Equity
    | "revenue" -> Ok Revenue
    | "expense" -> Ok Expense
    | other -> Error $"'{other}' is not an account type"

let private periodName =
    function
    | PeriodState.Open -> "open"
    | PeriodState.Closed -> "closed"
    | PeriodState.Locked -> "locked"

let private periodOf =
    function
    | "open" -> Ok PeriodState.Open
    | "closed" -> Ok PeriodState.Closed
    | "locked" -> Ok PeriodState.Locked
    | other -> Error $"'{other}' is not a period state"

let private methodName =
    function
    | Ach -> "ach"
    | Check -> "check"
    | Wire -> "wire"
    | CreditCard -> "credit-card"
    | PaymentMethod.Cash -> "cash"
    | PaymentMethod.Other -> "other"

let private methodOf =
    function
    | "ach" -> Ok Ach
    | "check" -> Ok Check
    | "wire" -> Ok Wire
    | "credit-card" -> Ok CreditCard
    | "cash" -> Ok PaymentMethod.Cash
    | "other" -> Ok PaymentMethod.Other
    | other -> Error $"'{other}' is not a payment method"

let private source =
    function
    | FromCredit id -> Json.objectOf [ "kind", Json.String "credit"; "id", Json.String id ]
    | FromDeposit id -> Json.objectOf [ "kind", Json.String "deposit"; "id", Json.String id ]
    | FromCreditMemo id -> Json.objectOf [ "kind", Json.String "credit-memo"; "id", Json.String id ]

let private sourceOf (value: Json) : Decoded<CreditSource> =
    decode {
        do! closed [ "id"; "kind" ] value
        let! id = text "id" value

        return!
            text "kind" value
            |> Result.bind (function
                | "credit" -> Ok(FromCredit id)
                | "deposit" -> Ok(FromDeposit id)
                | "credit-memo" -> Ok(FromCreditMemo id)
                | other -> Error $"'{other}' is not a credit source")
    }

// ---- Records ----------------------------------------------------------------

/// One record to store: its key and body.
[<NoComparison>]
type FinancialRecord = { Key: RecordKey; Body: Json }

let private segment (text: string) =
    match Segment.create text with
    | Ok found -> Ok found
    | Error error -> Error(StorageOperationRefused(LocationError.describe error))

let private keyOf (t: RecordType) (partition: string list) (id: string) : Result<RecordKey, Diagnostic> =
    match RecordId.create id with
    | Error _ -> Error(StorageOperationRefused $"'{id}' cannot be stored as a {RecordType.value t} id")
    | Ok recordId ->
        List.foldBack
            (fun item state -> state |> Result.bind (fun rest -> segment item |> Result.map (fun s -> s :: rest)))
            partition
            (Ok [])
        |> Result.map (fun parts ->
            { Type = t
              Partition = parts
              Id = recordId })

let private yearMonth (d: DateOnly) = [ $"{d.Year:D4}"; $"{d.Month:D2}" ]

/// Stable ids of audit events: a hash of the event and how many identical
/// events precede it, so the same history always has the same paths.
let private auditBody (a: AuditRecord) =
    Json.objectOf
        [ "who", Json.String a.Who
          "what", Json.String a.What
          "when", Json.String(preciseTimestamp a.When)
          "source", Json.String a.Source
          "correlationId", optionalString a.CorrelationId
          "subject", Json.String a.Subject ]

let private auditIds (audit: AuditRecord list) =
    audit
    |> List.mapFold
        (fun (seen: Map<string, int>) record ->
            let canonical = Json.canonicalText (auditBody record)
            let n = seen.TryFind canonical |> Option.defaultValue 0
            let digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{canonical}#{n}")) |> Convert.ToHexString
            ("a" + digest.Substring(0, 32).ToLowerInvariant(), record), seen.Add(canonical, n + 1))
        Map.empty
    |> fst

/// The records the receivables hold: everything authoritative, nothing
/// derived. Every key is deterministic.
let toRecords (r: Receivables) : Result<FinancialRecord list, Diagnostic list> =
    let books = r.Books
    let ledger = books.Ledger
    let keyFor (entryId: string) = ledger.Keys |> Map.tryFindKey (fun _ id -> id = entryId)
    let draftFor (invoiceId: string) = books.IssuedFrom |> Map.tryFindKey (fun _ id -> id = invoiceId)

    let records =
        [ for KeyValue(_, a) in ledger.Accounts ->
              keyOf accountType [] a.Id,
              Json.objectOf
                  [ "id", Json.String a.Id
                    "code", Json.String a.Code
                    "name", Json.String a.Name
                    "type", Json.String(accountTypeName a.Type)
                    "active", Json.Bool a.Active ]
          for KeyValue((year, month), state) in ledger.Periods ->
              keyOf periodType [] $"{year:D4}-{month:D2}",
              Json.objectOf [ "year", number year; "month", number month; "state", Json.String(periodName state) ]
          for KeyValue(_, e) in ledger.Entries ->
              keyOf entryType (yearMonth e.Date) e.Id,
              Json.objectOf
                  [ "id", Json.String e.Id
                    "date", Json.String(dateText e.Date)
                    "description", Json.String e.Description
                    "lines", Json.Array(List.map line e.Lines)
                    "source", Json.String e.Source
                    "reverses", optionalString e.Reverses
                    "postedAt", Json.String(preciseTimestamp e.PostedAt)
                    "idempotencyKey", optionalString (keyFor e.Id) ]
          for KeyValue(_, c) in books.Customers ->
              keyOf customerType [] c.Id,
              Json.objectOf
                  [ "id", Json.String c.Id
                    "name", Json.String c.Name
                    "billingName", Json.String c.BillingName
                    "billingAddress", Json.String c.BillingAddress
                    "email", Json.String c.Email
                    "defaultTerms", optionalJson terms c.DefaultTerms
                    "active", Json.Bool c.Active ]
          for KeyValue(_, d) in books.Drafts ->
              keyOf draftType [] d.DraftId,
              Json.objectOf
                  [ "draftId", Json.String d.DraftId
                    "customerId", Json.String d.CustomerId
                    "currency", Json.String d.Currency
                    "lines", Json.Array(List.map invoiceLine d.Lines)
                    "adjustments", Json.Array(List.map money d.Adjustments)
                    "discounts", Json.Array(List.map invoiceDiscount d.Discounts)
                    "terms", optionalJson terms d.Terms
                    "dueDate", optionalJson (dateText >> Json.String) d.DueDate
                    "corrects", optionalString d.Corrects
                    "engagementId", optionalString d.EngagementId ]
          for KeyValue(_, i) in books.Invoices do
              yield
                  keyOf invoiceType [ $"{i.IssueDate.Year:D4}" ] i.InvoiceId,
                  Json.objectOf
                      [ "invoiceId", Json.String i.InvoiceId
                        "number", Json.String i.Number
                        "draftId", optionalString (draftFor i.InvoiceId)
                        "customerId", Json.String i.CustomerId
                        "currency", Json.String i.Currency
                        "issueDate", Json.String(dateText i.IssueDate)
                        "dueDate", Json.String(dateText i.DueDate)
                        "terms", terms i.Terms
                        "termsSource", Json.String(termsSourceName i.TermsSource)
                        "lines", Json.Array(List.map invoiceLine i.Lines)
                        "subtotal", money i.Subtotal
                        "discounts", Json.Array(List.map invoiceDiscount i.Discounts)
                        "adjustments", Json.Array(List.map money i.Adjustments)
                        "corrects", optionalString i.Corrects
                        "engagementId", optionalString i.EngagementId
                        "total", money i.Total
                        "journalEntryId", Json.String i.JournalEntryId
                        "obligationId", Json.String i.ObligationId
                        "issuedAt", Json.String(preciseTimestamp i.IssuedAt) ]

              match i.SentAt, i.SentTo with
              | Some at, Some recipient ->
                  yield
                      keyOf deliveryType [] i.InvoiceId,
                      Json.objectOf
                          [ "invoiceId", Json.String i.InvoiceId
                            "sentAt", Json.String(preciseTimestamp at)
                            "sentTo", Json.String recipient ]
              | _ -> ()
          for KeyValue(_, o) in books.Obligations ->
              keyOf obligationType [] o.Id,
              Json.objectOf
                  [ "id", Json.String o.Id
                    "kind",
                    Json.String(
                        match o.Kind with
                        | Receivable -> "receivable"
                        | Payable -> "payable"
                    )
                    "source", Json.String o.Source
                    "party", Json.String o.Party
                    "originalAmount", money o.OriginalAmount
                    "dueDate", Json.String(dateText o.DueDate)
                    "cancelled", Json.Bool o.Cancelled ]
          for KeyValue(_, p) in r.Payments ->
              keyOf paymentType [ $"{p.DateReceived.Year:D4}" ] p.Id,
              Json.objectOf
                  [ "id", Json.String p.Id
                    "customerId", Json.String p.CustomerId
                    "dateReceived", Json.String(dateText p.DateReceived)
                    "amount", money p.Amount
                    "method", Json.String(methodName p.Method)
                    "reference", Json.String p.Reference
                    "memo", optionalString p.Memo ]
          for a in r.Allocations ->
              keyOf allocationType [] a.Id,
              Json.objectOf
                  [ "id", Json.String a.Id
                    "paymentId", Json.String a.PaymentId
                    "invoiceId", Json.String a.InvoiceId
                    "amount", money a.Amount
                    "journalEntryId", Json.String a.JournalEntryId ]
          for KeyValue(_, c) in r.Credits ->
              keyOf creditType [] c.Id,
              Json.objectOf
                  [ "id", Json.String c.Id
                    "customerId", Json.String c.CustomerId
                    "sourcePaymentId", Json.String c.SourcePaymentId
                    "amount", money c.Amount
                    "date", Json.String(dateText c.Date)
                    "journalEntryId", Json.String c.JournalEntryId ]
          for KeyValue(_, d) in r.Deposits ->
              keyOf depositType [ $"{d.DateReceived.Year:D4}" ] d.Id,
              Json.objectOf
                  [ "id", Json.String d.Id
                    "customerId", Json.String d.CustomerId
                    "dateReceived", Json.String(dateText d.DateReceived)
                    "amount", money d.Amount
                    "method", Json.String(methodName d.Method)
                    "reference", Json.String d.Reference
                    "journalEntryId", Json.String d.JournalEntryId ]
          for KeyValue(_, m) in r.CreditMemos ->
              keyOf creditMemoType [ $"{m.IssueDate.Year:D4}" ] m.Id,
              Json.objectOf
                  [ "id", Json.String m.Id
                    "customerId", Json.String m.CustomerId
                    "invoiceId", optionalString m.InvoiceId
                    "amount", money m.Amount
                    "revenueAccountId", Json.String m.RevenueAccountId
                    "reason", Json.String m.Reason
                    "issueDate", Json.String(dateText m.IssueDate)
                    "journalEntryId", Json.String m.JournalEntryId ]
          for a in r.Applications ->
              keyOf applicationType [] a.Id,
              Json.objectOf
                  [ "id", Json.String a.Id
                    "source", source a.Source
                    "invoiceId", Json.String a.InvoiceId
                    "amount", money a.Amount
                    "date", Json.String(dateText a.Date)
                    "journalEntryId", Json.String a.JournalEntryId ]
          for KeyValue(_, f) in r.Refunds ->
              keyOf refundType [ $"{f.Date.Year:D4}" ] f.Id,
              Json.objectOf
                  [ "id", Json.String f.Id
                    "customerId", Json.String f.CustomerId
                    "source", source f.Source
                    "amount", money f.Amount
                    "date", Json.String(dateText f.Date)
                    "method", Json.String(methodName f.Method)
                    "reference", Json.String f.Reference
                    "journalEntryId", Json.String f.JournalEntryId ]
          for KeyValue(_, v) in r.Reversals ->
              keyOf reversalType [] v.PaymentId,
              Json.objectOf
                  [ "paymentId", Json.String v.PaymentId
                    "reason", Json.String v.Reason
                    "date", Json.String(dateText v.Date)
                    "journalEntryIds", Json.Array(v.JournalEntryIds |> List.map Json.String) ]
          for KeyValue(_, w) in r.WriteOffs ->
              keyOf writeOffType [] w.Id,
              Json.objectOf
                  [ "id", Json.String w.Id
                    "invoiceId", Json.String w.InvoiceId
                    "amount", money w.Amount
                    "reason", Json.String w.Reason
                    "date", Json.String(dateText w.Date)
                    "journalEntryId", Json.String w.JournalEntryId ]
          for KeyValue(_, v) in r.Voids ->
              keyOf voidType [] v.InvoiceId,
              Json.objectOf
                  [ "invoiceId", Json.String v.InvoiceId
                    "reason", Json.String v.Reason
                    "date", Json.String(dateText v.Date)
                    "journalEntryId", Json.String v.JournalEntryId ]
          for KeyValue(_, e) in books.Engagements ->
              keyOf engagementType [] e.Id,
              Json.objectOf
                  [ "id", Json.String e.Id
                    "customerId", Json.String e.CustomerId
                    "name", Json.String e.Name
                    "currency", Json.String e.Currency
                    "fixedFee", optionalJson money e.FixedFee
                    "milestones", Json.Array(List.map milestone e.Milestones)
                    "terms", optionalJson terms e.Terms ]
          for KeyValue(_, x) in books.Expenses ->
              keyOf expenseType [ $"{x.Date.Year:D4}" ] x.Id,
              Json.objectOf
                  [ "id", Json.String x.Id
                    "date", Json.String(dateText x.Date)
                    "description", Json.String x.Description
                    "amount", money x.Amount
                    "expenseAccountId", Json.String x.ExpenseAccountId
                    "paidFromAccountId", Json.String x.PaidFromAccountId
                    "customerId", optionalString x.CustomerId
                    "projectId", optionalString x.ProjectId
                    "engagementId", optionalString x.EngagementId
                    "billable", Json.Bool x.Billable
                    "journalEntryId", Json.String x.JournalEntryId ]
          for KeyValue(_, t) in books.Time ->
              keyOf timeType [ $"{t.BusinessDate.Year:D4}" ] t.PublicationId,
              Json.objectOf
                  [ "publicationId", Json.String t.PublicationId
                    "organizationId", Json.String t.OrganizationId
                    "activityId", Json.String t.ActivityId
                    "revision", number t.Revision
                    "performerId", Json.String t.PerformerId
                    "businessDate", Json.String(dateText t.BusinessDate)
                    "projectId", Json.String t.ProjectId
                    "clientId", optionalString t.ClientId
                    "engagementId", optionalString t.EngagementId
                    "activityTypeId", Json.String t.ActivityTypeId
                    "description", Json.String t.Description
                    "exactMinutes", number t.ExactMinutes
                    "billableMinutes", number t.BillableMinutes
                    "approved", Json.Bool t.Approved
                    "rateReference", optionalString t.RateReference
                    "origin", origin t.Origin
                    "lineage", Json.Array(t.Lineage |> List.map Json.String)
                    "workItem", optionalString t.WorkItem
                    "supersedes", optionalString t.Supersedes
                    "publishedAt", Json.String(preciseTimestamp t.PublishedAt) ]
          for KeyValue(_, w) in books.Withdrawals ->
              keyOf withdrawalType [] w.PublicationId,
              Json.objectOf
                  [ "publicationId", Json.String w.PublicationId
                    "revision", number w.Revision
                    "reason", Json.String w.Reason
                    "withdrawnAt", Json.String(preciseTimestamp w.WithdrawnAt) ]
          for KeyValue(_, p) in books.Proposals ->
              keyOf proposalType [] p.Id,
              Json.objectOf
                  [ "id", Json.String p.Id
                    "customerId", Json.String p.CustomerId
                    "engagementId", optionalString p.EngagementId
                    "currency", Json.String p.Currency
                    "lines", Json.Array(p.Lines |> List.map (fun l -> Json.objectOf [ "line", invoiceLine l.Line; "priced", Json.Bool l.Priced ]))
                    "grouping", Json.Array(p.Grouping |> List.map (fun g -> Json.String(groupByNames |> List.find (fun (x, _) -> x = g) |> snd)))
                    "state", proposalState p.State
                    "createdAt", Json.String(preciseTimestamp p.CreatedAt) ]
          if not books.Rates.Rates.IsEmpty || not books.Rates.Roles.IsEmpty then
              yield
                  keyOf rateCardType [] "rates",
                  Json.objectOf
                      [ "rates", Json.Array(books.Rates.Rates |> List.map (fun (sc, rate) -> Json.objectOf [ "scope", scope sc; "rate", money rate ]))
                        "roles",
                        Json.Array(
                            books.Rates.Roles
                            |> Map.toList
                            |> List.map (fun (person, role) -> Json.objectOf [ "personId", Json.String person; "roleId", Json.String role ])
                        ) ]
          for KeyValue(_, v) in books.Reviews ->
              keyOf reviewType [] v.Id,
              Json.objectOf
                  [ "id", Json.String v.Id
                    "publicationId", Json.String v.PublicationId
                    "invoiceId", Json.String v.InvoiceId
                    "reason", Json.String v.Reason
                    "raisedAt", Json.String(preciseTimestamp v.RaisedAt)
                    "resolution", optionalString v.Resolution ]
          for id, a in auditIds ledger.Audit -> keyOf auditType (yearMonth (DateOnly.FromDateTime a.When.UtcDateTime)) id, auditBody a ]

    match records |> List.choose (fun (k, _) -> match k with Error d -> Some d | Ok _ -> None) with
    | [] -> Ok(records |> List.choose (fun (k, body) -> k |> Result.toOption |> Option.map (fun key -> { Key = key; Body = body })))
    | problems -> Error problems

/// The canonical stored text of a record.
let encode (record: FinancialRecord) : Result<string, Diagnostic> =
    { Id = record.Key.Id
      Type = record.Key.Type
      SchemaVersion = 1
      Mutability = mutabilityOf record.Key.Type
      Body = record.Body }
    |> Record.encode Record.DefaultMaxBytes
    |> Result.mapError (fun _ -> StorageOperationRefused $"'{RecordId.value record.Key.Id}' is too large to store")

/// The records as namespace-relative path text -> (key, canonical content).
let contents (records: FinancialRecord list) : Result<Map<string, RecordKey * string>, Diagnostic list> =
    let encoded =
        records
        |> List.map (fun r ->
            Layout.recordPath r.Key
            |> Result.mapError (LocationError.describe >> InvalidDataLocation)
            |> Result.bind (fun p -> encode r |> Result.map (fun c -> RelativePath.render p, (r.Key, c))))

    match encoded |> List.choose (function Error d -> Some d | Ok _ -> None) with
    | [] -> Ok(encoded |> List.choose Result.toOption |> Map.ofList)
    | problems -> Error problems

// ---- Loading ----------------------------------------------------------------

/// The decoded parts of a folder, before they are assembled.
type private Part =
    | AccountPart of Account
    | PeriodPart of (int * int) * PeriodState
    | EntryPart of PostedEntry * key: string option
    | CustomerPart of Customer
    | DraftPart of DraftInvoice
    | InvoicePart of IssuedInvoice * draftId: string option
    | DeliveryPart of invoiceId: string * DateTimeOffset * string
    | ObligationPart of Obligation
    | PaymentPart of Payment
    | AllocationPart of Allocation
    | AuditPart of AuditRecord
    | CreditPart of CustomerCredit
    | DepositPart of Deposit
    | CreditMemoPart of CreditMemo
    | ApplicationPart of Application
    | RefundPart of Refund
    | ReversalPart of PaymentReversal
    | WriteOffPart of WriteOff
    | VoidPart of InvoiceVoid
    | EngagementPart of Engagement
    | ExpensePart of Expense
    | TimePart of SourceTime
    | WithdrawalPart of TimeWithdrawal
    | ProposalPart of Proposal
    | RateCardPart of RateCard
    | ReviewPart of BillingReview

let private partOf (t: RecordType) (b: Json) : Decoded<Part> =
    match RecordType.value t with
    | "summa.account" ->
        decode {
            do! closed [ "active"; "code"; "id"; "name"; "type" ] b
            let! id = text "id" b
            let! code = text "code" b
            let! name = text "name" b
            let! kind = text "type" b |> Result.bind accountTypeOf
            let! active = flag "active" b

            return
                AccountPart
                    { Id = id
                      Code = code
                      Name = name
                      Type = kind
                      Active = active }
        }
    | "summa.period" ->
        decode {
            do! closed [ "month"; "state"; "year" ] b
            let! year = integer "year" b
            let! month = integer "month" b
            let! state = text "state" b |> Result.bind periodOf
            return PeriodPart((year, month), state)
        }
    | "summa.entry" ->
        decode {
            do! closed [ "date"; "description"; "id"; "idempotencyKey"; "lines"; "postedAt"; "reverses"; "source" ] b
            let! id = text "id" b
            let! date = date "date" b
            let! description = text "description" b
            let! lines = list "lines" lineOf b
            let! source = text "source" b
            let! reverses = optionalText "reverses" b
            let! postedAt = preciseInstant "postedAt" b
            let! key = optionalText "idempotencyKey" b

            return
                EntryPart(
                    { Id = id
                      Date = date
                      Description = description
                      Lines = lines
                      Source = source
                      State = Posted
                      Reverses = reverses
                      PostedAt = postedAt },
                    key
                )
        }
    | "summa.customer" ->
        decode {
            do! closed [ "active"; "billingAddress"; "billingName"; "defaultTerms"; "email"; "id"; "name" ] b
            let! id = text "id" b
            let! name = text "name" b
            let! billingName = text "billingName" b
            let! address = text "billingAddress" b
            let! email = text "email" b
            let! terms = optionalOf termsOf "defaultTerms" b
            let! active = flag "active" b

            return
                CustomerPart
                    { Id = id
                      Name = name
                      BillingName = billingName
                      BillingAddress = address
                      Email = email
                      DefaultTerms = terms
                      Active = active }
        }
    | "summa.draft" ->
        decode {
            do! closed [ "adjustments"; "corrects"; "currency"; "customerId"; "discounts"; "draftId"; "dueDate"; "engagementId"; "lines"; "terms" ] b
            let! id = text "draftId" b
            let! customer = text "customerId" b
            let! currency = text "currency" b
            let! lines = list "lines" invoiceLineOf b
            let! adjustments = list "adjustments" moneyOf b
            let! discounts = list "discounts" invoiceDiscountOf b
            let! terms = optionalOf termsOf "terms" b
            let! due = optionalDate "dueDate" b
            let! corrects = optionalText "corrects" b
            let! engagement = optionalText "engagementId" b

            return
                DraftPart
                    { DraftId = id
                      CustomerId = customer
                      Currency = currency
                      Lines = lines
                      Adjustments = adjustments
                      Discounts = discounts
                      Terms = terms
                      DueDate = due
                      Corrects = corrects
                      EngagementId = engagement }
        }
    | "summa.invoice" ->
        decode {
            do!
                closed
                    [ "adjustments"; "corrects"; "currency"; "customerId"; "discounts"; "draftId"; "dueDate"; "engagementId"; "invoiceId"; "issueDate"
                      "issuedAt"; "journalEntryId"; "lines"; "number"; "obligationId"; "subtotal"; "terms"; "termsSource"; "total" ]
                    b

            let! id = text "invoiceId" b
            let! number = text "number" b
            let! draftId = optionalText "draftId" b
            let! customer = text "customerId" b
            let! currency = text "currency" b
            let! issueDate = date "issueDate" b
            let! dueDate = date "dueDate" b
            let! terms = field "terms" b |> Result.bind termsOf
            let! termsSource = text "termsSource" b |> Result.bind termsSourceOf
            let! lines = list "lines" invoiceLineOf b
            let! subtotal = moneyField "subtotal" b
            let! discounts = list "discounts" invoiceDiscountOf b
            let! adjustments = list "adjustments" moneyOf b
            let! corrects = optionalText "corrects" b
            let! engagement = optionalText "engagementId" b
            let! total = moneyField "total" b
            let! entry = text "journalEntryId" b
            let! obligation = text "obligationId" b
            let! issuedAt = preciseInstant "issuedAt" b

            return
                InvoicePart(
                    { InvoiceId = id
                      Number = number
                      CustomerId = customer
                      Currency = currency
                      IssueDate = issueDate
                      DueDate = dueDate
                      Terms = terms
                      TermsSource = termsSource
                      Lines = lines
                      Subtotal = subtotal
                      Discounts = discounts
                      Adjustments = adjustments
                      Total = total
                      Corrects = corrects
                      EngagementId = engagement
                      JournalEntryId = entry
                      ObligationId = obligation
                      IssuedAt = issuedAt
                      SentAt = None
                      SentTo = None },
                    draftId
                )
        }
    | "summa.delivery" ->
        decode {
            do! closed [ "invoiceId"; "sentAt"; "sentTo" ] b
            let! id = text "invoiceId" b
            let! at = preciseInstant "sentAt" b
            let! recipient = text "sentTo" b
            return DeliveryPart(id, at, recipient)
        }
    | "summa.obligation" ->
        decode {
            do! closed [ "cancelled"; "dueDate"; "id"; "kind"; "originalAmount"; "party"; "source" ] b
            let! id = text "id" b

            let! kind =
                text "kind" b
                |> Result.bind (function
                    | "receivable" -> Ok Receivable
                    | "payable" -> Ok Payable
                    | other -> Error $"'{other}' is not receivable or payable")

            let! source = text "source" b
            let! party = text "party" b
            let! amount = moneyField "originalAmount" b
            let! due = date "dueDate" b
            let! cancelled = flag "cancelled" b

            return
                ObligationPart
                    { Id = id
                      Kind = kind
                      Source = source
                      Party = party
                      OriginalAmount = amount
                      DueDate = due
                      Cancelled = cancelled }
        }
    | "summa.payment" ->
        decode {
            do! closed [ "amount"; "customerId"; "dateReceived"; "id"; "memo"; "method"; "reference" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! received = date "dateReceived" b
            let! amount = moneyField "amount" b
            let! method = text "method" b |> Result.bind methodOf
            let! reference = text "reference" b
            let! memo = optionalText "memo" b

            return
                PaymentPart
                    { Id = id
                      CustomerId = customer
                      DateReceived = received
                      Amount = amount
                      Method = method
                      Reference = reference
                      Memo = memo }
        }
    | "summa.allocation" ->
        decode {
            do! closed [ "amount"; "id"; "invoiceId"; "journalEntryId"; "paymentId" ] b
            let! id = text "id" b
            let! payment = text "paymentId" b
            let! invoice = text "invoiceId" b
            let! amount = moneyField "amount" b
            let! entry = text "journalEntryId" b

            return
                AllocationPart
                    { Id = id
                      PaymentId = payment
                      InvoiceId = invoice
                      Amount = amount
                      JournalEntryId = entry }
        }
    | "summa.audit" ->
        decode {
            do! closed [ "correlationId"; "source"; "subject"; "what"; "when"; "who" ] b
            let! who = text "who" b
            let! what = text "what" b
            let! at = preciseInstant "when" b
            let! source = text "source" b
            let! correlation = optionalText "correlationId" b
            let! subject = text "subject" b

            return
                AuditPart
                    { Who = who
                      What = what
                      When = at
                      Source = source
                      CorrelationId = correlation
                      Subject = subject }
        }
    | "summa.credit" ->
        decode {
            do! closed [ "amount"; "customerId"; "date"; "id"; "journalEntryId"; "sourcePaymentId" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! payment = text "sourcePaymentId" b
            let! amount = moneyField "amount" b
            let! date = date "date" b
            let! entry = text "journalEntryId" b

            return
                CreditPart
                    { Id = id
                      CustomerId = customer
                      SourcePaymentId = payment
                      Amount = amount
                      Date = date
                      JournalEntryId = entry }
        }
    | "summa.deposit" ->
        decode {
            do! closed [ "amount"; "customerId"; "dateReceived"; "id"; "journalEntryId"; "method"; "reference" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! received = date "dateReceived" b
            let! amount = moneyField "amount" b
            let! method = text "method" b |> Result.bind methodOf
            let! reference = text "reference" b
            let! entry = text "journalEntryId" b

            return
                DepositPart
                    { Id = id
                      CustomerId = customer
                      DateReceived = received
                      Amount = amount
                      Method = method
                      Reference = reference
                      JournalEntryId = entry }
        }
    | "summa.credit-memo" ->
        decode {
            do! closed [ "amount"; "customerId"; "id"; "invoiceId"; "issueDate"; "journalEntryId"; "reason"; "revenueAccountId" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! invoice = optionalText "invoiceId" b
            let! amount = moneyField "amount" b
            let! revenue = text "revenueAccountId" b
            let! reason = text "reason" b
            let! issued = date "issueDate" b
            let! entry = text "journalEntryId" b

            return
                CreditMemoPart
                    { Id = id
                      CustomerId = customer
                      InvoiceId = invoice
                      Amount = amount
                      RevenueAccountId = revenue
                      Reason = reason
                      IssueDate = issued
                      JournalEntryId = entry }
        }
    | "summa.application" ->
        decode {
            do! closed [ "amount"; "date"; "id"; "invoiceId"; "journalEntryId"; "source" ] b
            let! id = text "id" b
            let! from = field "source" b |> Result.bind sourceOf
            let! invoice = text "invoiceId" b
            let! amount = moneyField "amount" b
            let! date = date "date" b
            let! entry = text "journalEntryId" b

            return
                ApplicationPart
                    { Id = id
                      Source = from
                      InvoiceId = invoice
                      Amount = amount
                      Date = date
                      JournalEntryId = entry }
        }
    | "summa.refund" ->
        decode {
            do! closed [ "amount"; "customerId"; "date"; "id"; "journalEntryId"; "method"; "reference"; "source" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! from = field "source" b |> Result.bind sourceOf
            let! amount = moneyField "amount" b
            let! date = date "date" b
            let! method = text "method" b |> Result.bind methodOf
            let! reference = text "reference" b
            let! entry = text "journalEntryId" b

            return
                RefundPart
                    { Id = id
                      CustomerId = customer
                      Source = from
                      Amount = amount
                      Date = date
                      Method = method
                      Reference = reference
                      JournalEntryId = entry }
        }
    | "summa.payment-reversal" ->
        decode {
            do! closed [ "date"; "journalEntryIds"; "paymentId"; "reason" ] b
            let! payment = text "paymentId" b
            let! reason = text "reason" b
            let! date = date "date" b

            let! ids =
                list "journalEntryIds" (function
                    | Json.String s -> Ok s
                    | _ -> Error "'journalEntryIds' holds something other than text") b

            return
                ReversalPart
                    { PaymentId = payment
                      Reason = reason
                      Date = date
                      JournalEntryIds = ids }
        }
    | "summa.write-off" ->
        decode {
            do! closed [ "amount"; "date"; "id"; "invoiceId"; "journalEntryId"; "reason" ] b
            let! id = text "id" b
            let! invoice = text "invoiceId" b
            let! amount = moneyField "amount" b
            let! reason = text "reason" b
            let! date = date "date" b
            let! entry = text "journalEntryId" b

            return
                WriteOffPart
                    { Id = id
                      InvoiceId = invoice
                      Amount = amount
                      Reason = reason
                      Date = date
                      JournalEntryId = entry }
        }
    | "summa.invoice-void" ->
        decode {
            do! closed [ "date"; "invoiceId"; "journalEntryId"; "reason" ] b
            let! invoice = text "invoiceId" b
            let! reason = text "reason" b
            let! date = date "date" b
            let! entry = text "journalEntryId" b

            return
                VoidPart
                    { InvoiceId = invoice
                      Reason = reason
                      Date = date
                      JournalEntryId = entry }
        }
    | "summa.engagement" ->
        decode {
            do! closed [ "currency"; "customerId"; "fixedFee"; "id"; "milestones"; "name"; "terms" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! name = text "name" b
            let! currency = text "currency" b
            let! fee = optionalOf moneyOf "fixedFee" b
            let! milestones = list "milestones" milestoneOf b
            let! terms = optionalOf termsOf "terms" b

            return
                EngagementPart
                    { Id = id
                      CustomerId = customer
                      Name = name
                      Currency = currency
                      FixedFee = fee
                      Milestones = milestones
                      Terms = terms }
        }
    | "summa.expense" ->
        decode {
            do!
                closed
                    [ "amount"; "billable"; "customerId"; "date"; "description"; "engagementId"; "expenseAccountId"; "id"; "journalEntryId"
                      "paidFromAccountId"; "projectId" ]
                    b

            let! id = text "id" b
            let! date = date "date" b
            let! description = text "description" b
            let! amount = moneyField "amount" b
            let! expenseAccount = text "expenseAccountId" b
            let! paidFrom = text "paidFromAccountId" b
            let! customer = optionalText "customerId" b
            let! project = optionalText "projectId" b
            let! engagement = optionalText "engagementId" b
            let! billable = flag "billable" b
            let! entry = text "journalEntryId" b

            return
                ExpensePart
                    { Id = id
                      Date = date
                      Description = description
                      Amount = amount
                      ExpenseAccountId = expenseAccount
                      PaidFromAccountId = paidFrom
                      CustomerId = customer
                      ProjectId = project
                      EngagementId = engagement
                      Billable = billable
                      JournalEntryId = entry }
        }
    | "summa.time" ->
        decode {
            do!
                closed
                    [ "activityId"; "activityTypeId"; "approved"; "billableMinutes"; "businessDate"; "clientId"; "description"; "engagementId"
                      "exactMinutes"; "lineage"; "organizationId"; "origin"; "performerId"; "projectId"; "publicationId"; "publishedAt"
                      "rateReference"; "revision"; "supersedes"; "workItem" ]
                    b

            let! publication = text "publicationId" b
            let! organization = text "organizationId" b
            let! activity = text "activityId" b
            let! revision = integer "revision" b
            let! performer = text "performerId" b
            let! businessDate = date "businessDate" b
            let! project = text "projectId" b
            let! client = optionalText "clientId" b
            let! engagement = optionalText "engagementId" b
            let! activityType = text "activityTypeId" b
            let! description = text "description" b
            let! exact = integer "exactMinutes" b
            let! billable = integer "billableMinutes" b
            let! approved = flag "approved" b
            let! rateReference = optionalText "rateReference" b
            let! from = field "origin" b |> Result.bind originOf
            let! lineage = strings "lineage" b
            let! workItem = optionalText "workItem" b
            let! supersedes = optionalText "supersedes" b
            let! published = preciseInstant "publishedAt" b

            return
                TimePart
                    { PublicationId = publication
                      OrganizationId = organization
                      ActivityId = activity
                      Revision = revision
                      PerformerId = performer
                      BusinessDate = businessDate
                      ProjectId = project
                      ClientId = client
                      EngagementId = engagement
                      ActivityTypeId = activityType
                      Description = description
                      ExactMinutes = exact
                      BillableMinutes = billable
                      Approved = approved
                      RateReference = rateReference
                      Origin = from
                      Lineage = lineage
                      WorkItem = workItem
                      Supersedes = supersedes
                      PublishedAt = published }
        }
    | "summa.time-withdrawal" ->
        decode {
            do! closed [ "publicationId"; "reason"; "revision"; "withdrawnAt" ] b
            let! publication = text "publicationId" b
            let! revision = integer "revision" b
            let! reason = text "reason" b
            let! at = preciseInstant "withdrawnAt" b

            return
                WithdrawalPart
                    { PublicationId = publication
                      Revision = revision
                      Reason = reason
                      WithdrawnAt = at }
        }
    | "summa.proposal" ->
        decode {
            do! closed [ "createdAt"; "currency"; "customerId"; "engagementId"; "grouping"; "id"; "lines"; "state" ] b
            let! id = text "id" b
            let! customer = text "customerId" b
            let! engagement = optionalText "engagementId" b
            let! currency = text "currency" b

            let! lines =
                list
                    "lines"
                    (fun l ->
                        decode {
                            do! closed [ "line"; "priced" ] l
                            let! line = field "line" l |> Result.bind invoiceLineOf
                            let! priced = flag "priced" l
                            return { Line = line; Priced = priced }
                        })
                    b

            let! grouping = list "grouping" groupByOf b
            let! state = field "state" b |> Result.bind proposalStateOf
            let! created = preciseInstant "createdAt" b

            return
                ProposalPart
                    { Id = id
                      CustomerId = customer
                      EngagementId = engagement
                      Currency = currency
                      Lines = lines
                      Grouping = grouping
                      State = state
                      CreatedAt = created }
        }
    | "summa.rate-card" ->
        decode {
            do! closed [ "rates"; "roles" ] b

            let! rates =
                list
                    "rates"
                    (fun r ->
                        decode {
                            do! closed [ "rate"; "scope" ] r
                            let! sc = field "scope" r |> Result.bind scopeOf
                            let! rate = moneyField "rate" r
                            return sc, rate
                        })
                    b

            let! roles =
                list
                    "roles"
                    (fun r ->
                        decode {
                            do! closed [ "personId"; "roleId" ] r
                            let! person = text "personId" r
                            let! role = text "roleId" r
                            return person, role
                        })
                    b

            return RateCardPart { Rates = rates; Roles = Map.ofList roles }
        }
    | "summa.billing-review" ->
        decode {
            do! closed [ "id"; "invoiceId"; "publicationId"; "raisedAt"; "reason"; "resolution" ] b
            let! id = text "id" b
            let! publication = text "publicationId" b
            let! invoice = text "invoiceId" b
            let! reason = text "reason" b
            let! raised = preciseInstant "raisedAt" b
            let! resolution = optionalText "resolution" b

            return
                ReviewPart
                    { Id = id
                      PublicationId = publication
                      InvoiceId = invoice
                      Reason = reason
                      RaisedAt = raised
                      Resolution = resolution }
        }
    | other -> Error $"'{other}' is not a financial record type"

/// A financial object as read: path text and revision.
type Seen = { Path: string; Revision: Revision; Content: string }

/// What the organization's financial records hold, as far as Summa may trust
/// them, and the revision of every object read (for the next commit).
[<NoComparison>]
type Loaded =
    { State: Receivables
      Seen: Map<string, Seen>
      Problems: Diagnostic list }

let private assemble (parts: Part list) : Receivables =
    let entries = parts |> List.choose (function EntryPart(e, k) -> Some(e, k) | _ -> None)
    let reversedBy = entries |> List.choose (fun (e, _) -> e.Reverses |> Option.map (fun original -> original, e.Id)) |> Map.ofList

    let posted =
        entries
        |> List.map (fun (e, _) ->
            match reversedBy.TryFind e.Id with
            | Some by -> { e with State = Reversed by }
            | None -> e)

    let deliveries = parts |> List.choose (function DeliveryPart(id, at, to') -> Some(id, (at, to')) | _ -> None) |> Map.ofList
    let invoices = parts |> List.choose (function InvoicePart(i, d) -> Some(i, d) | _ -> None)

    let ledger =
        { Accounts = parts |> List.choose (function AccountPart a -> Some(a.Id, a) | _ -> None) |> Map.ofList
          Periods = parts |> List.choose (function PeriodPart(p, s) -> Some(p, s) | _ -> None) |> Map.ofList
          Entries = posted |> List.map (fun e -> e.Id, e) |> Map.ofList
          Journal = posted |> List.sortBy (fun e -> e.PostedAt, e.Id) |> List.map _.Id
          Keys = entries |> List.choose (fun (e, k) -> k |> Option.map (fun key -> key, e.Id)) |> Map.ofList
          Audit = parts |> List.choose (function AuditPart a -> Some a | _ -> None) |> List.sortBy (fun a -> a.When, a.What, a.Subject) }

    let books =
        { Ledger = ledger
          Customers = parts |> List.choose (function CustomerPart c -> Some(c.Id, c) | _ -> None) |> Map.ofList
          Drafts = parts |> List.choose (function DraftPart d -> Some(d.DraftId, d) | _ -> None) |> Map.ofList
          Invoices =
            invoices
            |> List.map (fun (i, _) ->
                match deliveries.TryFind i.InvoiceId with
                | Some(at, recipient) -> i.InvoiceId, { i with SentAt = Some at; SentTo = Some recipient }
                | None -> i.InvoiceId, i)
            |> Map.ofList
          Obligations = parts |> List.choose (function ObligationPart o -> Some(o.Id, o) | _ -> None) |> Map.ofList
          IssuedFrom = invoices |> List.choose (fun (i, d) -> d |> Option.map (fun draft -> draft, i.InvoiceId)) |> Map.ofList
          Engagements = parts |> List.choose (function EngagementPart e -> Some(e.Id, e) | _ -> None) |> Map.ofList
          Expenses = parts |> List.choose (function ExpensePart x -> Some(x.Id, x) | _ -> None) |> Map.ofList
          Time = parts |> List.choose (function TimePart t -> Some(t.PublicationId, t) | _ -> None) |> Map.ofList
          Withdrawals = parts |> List.choose (function WithdrawalPart w -> Some(w.PublicationId, w) | _ -> None) |> Map.ofList
          Proposals = parts |> List.choose (function ProposalPart p -> Some(p.Id, p) | _ -> None) |> Map.ofList
          Rates = parts |> List.tryPick (function RateCardPart c -> Some c | _ -> None) |> Option.defaultValue noRates
          Reviews = parts |> List.choose (function ReviewPart v -> Some(v.Id, v) | _ -> None) |> Map.ofList }

    { Books = books
      Payments = parts |> List.choose (function PaymentPart p -> Some(p.Id, p) | _ -> None) |> Map.ofList
      Allocations = parts |> List.choose (function AllocationPart a -> Some a | _ -> None) |> List.sortBy _.Id
      Credits = parts |> List.choose (function CreditPart c -> Some(c.Id, c) | _ -> None) |> Map.ofList
      Deposits = parts |> List.choose (function DepositPart d -> Some(d.Id, d) | _ -> None) |> Map.ofList
      CreditMemos = parts |> List.choose (function CreditMemoPart m -> Some(m.Id, m) | _ -> None) |> Map.ofList
      Applications = parts |> List.choose (function ApplicationPart a -> Some a | _ -> None) |> List.sortBy _.Id
      Refunds = parts |> List.choose (function RefundPart f -> Some(f.Id, f) | _ -> None) |> Map.ofList
      Reversals = parts |> List.choose (function ReversalPart v -> Some(v.PaymentId, v) | _ -> None) |> Map.ofList
      WriteOffs = parts |> List.choose (function WriteOffPart w -> Some(w.Id, w) | _ -> None) |> Map.ofList
      Voids = parts |> List.choose (function VoidPart v -> Some(v.InvoiceId, v) | _ -> None) |> Map.ofList }

/// Cross-record rules that make the books trustworthy (`Invariants`), plus
/// what only the stored form can show: an idempotency key used twice.
let private consistency (entries: (PostedEntry * string option) list) (r: Receivables) =
    let reusedKeys =
        entries
        |> List.choose (fun (e, k) -> k |> Option.map (fun key -> key, e.Id))
        |> List.groupBy fst
        |> List.filter (fun (_, uses) -> uses.Length > 1)
        |> List.map (fun (key, uses) -> InvariantViolated("unique-idempotency-keys", key, $"used by {uses.Length} entries"))

    reusedKeys
    @ (Invariants.check r |> List.map (fun v -> InvariantViolated(v.Rule, v.Subject, v.Detail)))

/// Loads the financial records read from an organization's folder. Each
/// object must be a canonical record of a financial type, at the path its
/// content names; then the books must be consistent. Problems are reported,
/// never ignored, and a command never runs on books with problems.
let load (objects: StoredObject list) : Loaded =
    let decoded =
        objects
        |> List.map (fun stored ->
            let where = RelativePath.render stored.Path

            match Layout.keyOf stored.Path with
            | Some key when isFinancial key.Type ->
                let schema = schemas |> List.find (fun s -> s.Type = key.Type)

                Integrity.validate key schema Record.DefaultMaxBytes stored
                |> Result.mapError (Organization.describeIntegrity >> fun detail -> InvalidStoredRecord(where, detail))
                |> Result.bind (fun valid ->
                    if valid.Record.Mutability <> mutabilityOf key.Type then
                        Error(InvalidStoredRecord(where, "the record's mutability is not its type's"))
                    else
                        partOf key.Type valid.Record.Body |> Result.mapError (fun detail -> InvalidStoredRecord(where, detail)))
                |> Result.map (fun part -> stored, part)
            | _ -> Error(InvalidStoredRecord(where, "not a financial record's path")))

    let parts = decoded |> List.choose (function Ok(_, p) -> Some p | Error _ -> None)
    let state = assemble parts

    // Each record must sit where its content says it belongs.
    let misplaced =
        match toRecords state |> Result.bind contents with
        | Error problems -> problems
        | Ok expected ->
            let actual = objects |> List.map (fun o -> RelativePath.render o.Path) |> Set.ofList

            Set.difference actual (expected |> Map.keys |> Set.ofSeq)
            |> Set.toList
            |> List.filter (fun p -> decoded |> List.exists (function Ok(s, _) -> RelativePath.render s.Path = p | _ -> false))
            |> List.map (fun p -> InvalidStoredRecord(p, "the record is not at the path its content names"))

    { State = state
      Seen =
        objects
        |> List.map (fun o ->
            let p = RelativePath.render o.Path
            p, { Path = p; Revision = o.Revision; Content = o.Content })
        |> Map.ofList
      Problems =
        (decoded |> List.choose (function Error d -> Some d | Ok _ -> None))
        @ misplaced
        @ consistency (parts |> List.choose (function EntryPart(e, k) -> Some(e, k) | _ -> None)) state }
