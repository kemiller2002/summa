/// Operational safety (WI-0029): command telemetry and structured logs with
/// correlation ids (SUM3-016..018), exports that say what they are
/// (SUM3-026), reports that agree with the projection (SUM3-027),
/// deterministic results (SUM3-028) and business dates (SUM3-029).
module Summa.Storage.Tests.OperationalSafetyTests

open System
open System.Globalization
open Xunit
open Arca
open Summa.Ledger.Money
open Summa.Ledger.Ledger
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Access
open Summa.Access.Access
open Summa.Storage
open Summa.Storage.Commands
open Summa.Storage.Tests.Books
open Summa.Storage.Tests.Support

let private kevin = Actor.person "583231" "kevin" "corr-ops-1"

let private founded () =
    let store = InMemoryStore()
    let binding = bindingOf production
    let ns = Storage.organizationNamespace production binding "org_acme" |> Support.ok
    let found = Governance.found production binding RepositoryVisibility.Private None (context "found") acme { PrincipalId = "github:583231"; Kind = Human; DisplayName = "kevin" } |> Support.ok
    store.Provider.Commit found |> Async.RunSynchronously |> Support.ok |> ignore
    store, ns

let private request actor capability key transition : Request<_> =
    { Actor = actor; Capability = capability; Summary = key; IdempotencyKey = $"cmd-{key}-0001"; Transition = transition }

let private observed (store: InMemoryStore) ns (events: Collections.Generic.List<Telemetry.CommandEvent>) req =
    Telemetry.observe events.Add (fun () -> ledgerContext.When) store.Provider defaultApprovalGates ns 3 req |> Async.RunSynchronously

let private setUp (r: Receivables) = Ok { r with Books = openBooks chart |> saveCustomer ledgerContext abc }

// ---- Telemetry, structured logs and correlation (SUM3-016..018) ------------------------------

[<Fact>]
let ``every command run is one structured event, with financial problems told apart from technical ones`` () =
    let store, ns = founded ()
    let events = Collections.Generic.List<Telemetry.CommandEvent>()
    observed store ns events (request kevin ManageSettings "chart" setUp) |> Support.ok |> ignore
    observed store ns events (request kevin ManageSettings "chart" setUp) |> Support.ok |> ignore
    observed store ns events (request kevin IssueInvoice "issue-missing" (fun r -> issue ledgerContext issueRequest r.Books |> Result.map (fun (b, _) -> { r with Books = b }))) |> ignore
    let stranger = Actor.person "999" "stranger" "corr-ops-2"
    observed store ns events (request stranger ManageSettings "stranger" setUp) |> ignore

    match List.ofSeq events with
    | [ committed; again; rejected; refused ] ->
        Assert.Equal("committed", committed.Outcome)
        Assert.Equal(Telemetry.Succeeded, committed.Category)
        Assert.NotEmpty committed.Entities
        Assert.Equal("corr-ops-1", committed.CorrelationId)
        Assert.Equal("github:583231", committed.Actor)
        Assert.Equal("Human", committed.ActorKind)
        // The same command again: detected as a duplicate, nothing committed.
        Assert.True again.Duplicate
        Assert.Equal("unchanged", again.Outcome)
        Assert.Equal("rejected-transition", rejected.Outcome)
        Assert.Equal(Telemetry.Financial, rejected.Category)
        Assert.Equal("not-authorized", refused.Outcome)
        Assert.Equal(Telemetry.Access, refused.Category)
        // The log line holds identifiers and outcomes, never what was typed.
        let line = Telemetry.toJson committed
        Assert.Contains("\"correlationId\":\"corr-ops-1\"", line)
        Assert.Contains("\"category\":\"succeeded\"", line)
        Assert.DoesNotContain("ABC Corp", line)
        Assert.DoesNotContain("1 Main St", line)
    | other -> failwith $"%A{other}"

[<Fact>]
let ``storage failures are technical, and the correlation id reaches the audit trail`` () =
    let store, ns = founded ()
    let events = Collections.Generic.List<Telemetry.CommandEvent>()
    observed store ns events (request kevin ManageSettings "chart" setUp) |> Support.ok |> ignore
    let down = { store.Provider with ChangeToken = (fun _ -> async.Return(Error(StorageFailure.ProviderFailed("github", true, "down")))); NamespaceState = (fun _ -> async.Return(Error(StorageFailure.ProviderFailed("github", true, "down")))) }
    Telemetry.observe events.Add (fun () -> ledgerContext.When) down defaultApprovalGates ns 3 (request kevin ManageSettings "down" setUp) |> Async.RunSynchronously |> ignore
    Assert.Equal(Telemetry.Technical, events[1].Category)
    Assert.Equal("storage-failed", events[1].Outcome)
    // A command's context carries the actor's correlation id into every audit event it writes.
    let context = contextFor kevin ledgerContext.When "summa-app" None None
    Assert.Equal(Some "corr-ops-1", context.CorrelationId)
    let books = saveCustomer context abc (openBooks chart)
    Assert.Equal(Some "corr-ops-1", (List.last books.Ledger.Audit).CorrelationId)

// ---- Exports say what they are (SUM3-026) -----------------------------------------------------

[<Fact>]
let ``an export names its period, the books' data version, its filters and its schema`` () =
    let books = full ()
    let version = Exports.dataVersion books |> Support.ok
    Assert.Matches("^sha256:[0-9a-f]{64}$", version)
    // The same books, the same version; any change to a record, another.
    Assert.Equal(version, Exports.dataVersion (full ()) |> Support.ok)
    let changed = { books with Books = saveCustomer ledgerContext { abc with Email = "new@abc.example" } books.Books }
    Assert.NotEqual<string>(version, Exports.dataVersion changed |> Support.ok)

    let header =
        Exports.header
            { Kind = "journal"
              GeneratedAt = DateTimeOffset(2027, 1, 15, 9, 30, 0, TimeSpan.FromHours -5.0)
              Period = DateOnly(2026, 1, 1), DateOnly(2026, 12, 31)
              DataVersion = version
              Filters = [ "currency", "USD"; "year", "2026" ] }

    Assert.Equal<string list>(
        [ "# Export: journal"
          "# SchemaVersion: summa.export/1"
          "# GeneratedAt: 2027-01-15T14:30:00Z"
          "# AccountingPeriod: 2026-01-01..2026-12-31"
          $"# DataVersion: {version}"
          "# Filters: currency=USD; year=2026" ],
        header
    )

// ---- Reports agree with the projection (SUM3-027) ---------------------------------------------

[<Fact>]
let ``the receivable projection agrees with the books, and a stale one is found out`` () =
    let store, ns = founded ()
    let run key capability transition = execute store.Provider defaultApprovalGates ns 3 (request kevin capability key transition) |> Async.RunSynchronously |> Support.ok
    run "chart" ManageSettings setUp |> ignore
    let issued = run "issue" IssueInvoice (fun r -> saveDraft ledgerContext draft r.Books |> Result.bind (issue ledgerContext issueRequest) |> Result.map (fun (b, _) -> { r with Books = b }))
    let meta key = Commands.metadata kevin key $"op-{key}-0001" |> Support.ok
    let index, _ = Derived.rebuild store.Provider ns (meta "index") Projection.definition |> Async.RunSynchronously |> Support.ok
    Assert.Empty(Reconciliation.agreesWithIndex "ar" issued.State index)
    Assert.Empty(Reconciliation.run "ar" (DateOnly(2026, 12, 31)) issued.State)

    let paid = run "pay" RecordPayment (recordPayment ledgerContext { payment with Amount = usd 1000L })
    let allocated = run "allocate" AllocatePayment (allocate ledgerContext { allocation with Amount = usd 1000L })
    ignore paid
    let findings = Reconciliation.agreesWithIndex "ar" allocated.State index
    Assert.Contains(findings, fun f -> f.Check = "projection-outstanding")
    Assert.Contains(findings, fun f -> f.Check = "projection-receivables")

// ---- Deterministic results and business dates (SUM3-028, SUM3-029) ----------------------------

[<Fact>]
let ``the same records give the same books, in any order and under any culture`` () =
    let books = withCredits ()
    let records = FinancialRecords.toRecords books |> Support.ok |> FinancialRecords.contents |> Support.ok
    let expected = Summa.Ledger.Reports.trialBalanceCsv (Summa.Ledger.Reports.trialBalance "USD" (DateOnly(2026, 12, 31)) books.Books.Ledger)
    let journal = Summa.Ledger.Reports.journalCsv books.Books.Ledger
    let previous = CultureInfo.CurrentCulture

    try
        for culture in [ "de-DE"; "fr-FR"; "ar-SA"; "en-US" ] do
            CultureInfo.CurrentCulture <- CultureInfo culture
            Assert.Equal(expected, Summa.Ledger.Reports.trialBalanceCsv (Summa.Ledger.Reports.trialBalance "USD" (DateOnly(2026, 12, 31)) books.Books.Ledger))
            Assert.Equal(journal, Summa.Ledger.Reports.journalCsv books.Books.Ledger)
            Assert.True((records = (FinancialRecords.toRecords books |> Support.ok |> FinancialRecords.contents |> Support.ok)))
    finally
        CultureInfo.CurrentCulture <- previous

    // Money is whole minor units: no floating point anywhere it is added up.
    Assert.Equal(usd 30L, add (usd 10L) (usd 20L))

[<Fact>]
let ``today is the organization's business date, never the UTC date of the instant`` () =
    let lateEvening = DateTimeOffset(2026, 10, 8, 2, 30, 0, TimeSpan.Zero)
    Assert.Equal(DateOnly(2026, 10, 7), Organization.businessDate { acme with TimeZone = "America/New_York" } lateEvening)
    Assert.Equal(DateOnly(2026, 10, 8), Organization.businessDate { acme with TimeZone = "Europe/Berlin" } lateEvening)
    // A zone this system does not know falls back to UTC rather than guessing.
    Assert.Equal(DateOnly(2026, 10, 8), Organization.businessDate { acme with TimeZone = "Mars/Olympus_Mons" } lateEvening)

// ---- Sensitive data and secrets (SUM3-022, SUM3-023, SUM3-024) --------------------------------

[<Fact>]
let ``full card numbers, bank account numbers and credentials are found, and references are not`` () =
    Assert.Equal<Sensitive.Finding list>([ Sensitive.CardNumber ], Sensitive.scan "card 4111 1111 1111 1111")
    Assert.Equal<Sensitive.Finding list>([ Sensitive.CardNumber ], Sensitive.scan "4111-1111-1111-1111")
    Assert.Empty(Sensitive.scan "card ending 1111, ACH-20261007-000123")
    // Not a card: fails Luhn, or sits inside an id or a hash.
    Assert.Empty(Sensitive.scan "4111111111111112")
    Assert.Empty(Sensitive.scan "sha256:a4111111111111111b and EXE-20261009T031528777Z")
    Assert.Equal<Sensitive.Finding list>([ Sensitive.BankAccountNumber ], Sensitive.scan "IBAN DE89 3704 0044 0532 0130 00")
    Assert.Empty(Sensitive.scan "DE89 3704 0044 0532 0130 01")
    // The account digits of that mistyped IBAN pass Luhn by chance. Inside the IBAN they are
    // judged by the IBAN rule only; standing alone they are still a card number.
    Assert.Equal<Sensitive.Finding list>([ Sensitive.CardNumber ], Sensitive.scan "3704 0044 0532 0130 01")
    // A card next to an IBAN is still found, as is each.
    Assert.Equal<Sensitive.Finding list>(
        [ Sensitive.CardNumber; Sensitive.BankAccountNumber ],
        Sensitive.scan "IBAN DE89 3704 0044 0532 0130 00, card 4111 1111 1111 1111")
    Assert.Equal<Sensitive.Finding list>([ Sensitive.Credential "GitHub token" ], Sensitive.scan ("token " + "ghp_" + String('a', 36)))
    Assert.Equal<Sensitive.Finding list>([ Sensitive.Credential "private key" ], Sensitive.scan "-----BEGIN RSA PRIVATE KEY-----")

[<Fact>]
let ``a command that would store a card number is refused, and nothing is written`` () =
    let store, ns = founded ()
    execute store.Provider defaultApprovalGates ns 3 (request kevin ManageSettings "chart" setUp) |> Async.RunSynchronously |> Support.ok |> ignore
    let before = store.State.History.Length
    let withCard = { payment with Reference = "paid with 4111 1111 1111 1111" }

    match execute store.Provider defaultApprovalGates ns 3 (request kevin RecordPayment "pay-card" (recordPayment ledgerContext withCard)) |> Async.RunSynchronously with
    | Error(Unstorable [ Diagnostics.SensitiveData(path, what) ]) ->
        Assert.StartsWith("records/summa.payment/", path)
        Assert.Equal("a full payment card number", what)
    | other -> failwith $"%A{other}"

    Assert.Equal(before, store.State.History.Length)
    // The last four digits are a reference, and are kept.
    let lastFour = { payment with Reference = "card ending 1111" }
    Assert.True(execute store.Provider defaultApprovalGates ns 3 (request kevin RecordPayment "pay-ref" (recordPayment ledgerContext lastFour)) |> Async.RunSynchronously |> Result.isOk)

[<Fact>]
let ``secrets stay out of configuration, and transport is https`` () =
    let identity = """{"exchange":"https://fides.test","application":"summa-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://summa.example/app/"}"""
    let config (identity: string) = $$"""{"environment":"test","environmentName":"test","identity":{{identity}}}"""
    Assert.True(Result.isOk (Deployment.parse (config identity)))
    // A client secret has no place in the deployment document: it is refused, not ignored.
    Assert.True(Result.isError (Deployment.parse (config (identity.Replace("}", ",\"clientSecret\":\"s3cr3t\"}")))))
    // An exchange over plain http, anywhere but this machine, is refused.
    Assert.True(Result.isError (Deployment.parse (config (identity.Replace("https://fides.test", "http://fides.test")))))

// ---- Integrations, deletion and retention (SUM3-031..035) -------------------------------------

[<Fact>]
let ``integration states: a delivery failure leaves the invoice issued and may be retried; a GitHub refusal needs a person`` () =
    Assert.Equal(Integrations.Pending, Integrations.ofDelivery Queued)
    Assert.Equal(Integrations.Succeeded, Integrations.ofDelivery Delivered)
    Assert.Equal(Integrations.Retryable "transient", Integrations.ofDelivery (DeliveryFailed "transient"))
    Assert.Equal(Integrations.PermanentFailure "permanent", Integrations.ofDelivery (DeliveryFailed "permanent"))
    Assert.Equal(Integrations.PermanentFailure "mailbox does not exist", Integrations.ofDelivery (Bounced "mailbox does not exist"))
    Assert.Equal(Integrations.Pending, Integrations.ofQueued EntryState.Pending)
    Assert.Equal(Integrations.PermanentFailure "refused", Integrations.ofQueued (EntryState.Refused "refused"))

[<Fact>]
let ``financial records are never deleted; only a draft may be`` () =
    let store, ns = founded ()
    let run key capability transition = execute store.Provider defaultApprovalGates ns 3 (request kevin capability key transition) |> Async.RunSynchronously
    run "chart" ManageSettings setUp |> Support.ok |> ignore
    run "draft" CreateDraftInvoice (fun r -> saveDraft ledgerContext draft r.Books |> Result.map (fun b -> { r with Books = b })) |> Support.ok |> ignore
    // A draft may go: it has no financial effect.
    Assert.True(run "drop-draft" CreateDraftInvoice (fun r -> Ok { r with Books = { r.Books with Drafts = Map.empty } }) |> Result.isOk)
    // A customer may become inactive, never disappear.
    match run "drop-customer" ManageBilling (fun r -> Ok { r with Books = { r.Books with Customers = Map.empty } }) with
    | Error(Unstorable problems) -> Assert.Contains(problems, fun p -> (Diagnostics.describe p).Contains "never deleted")
    | other -> failwith $"%A{other}"

[<Fact>]
let ``every financial record type has a retention, and only drafts are not kept for good`` () =
    for recordType, _, deletable in FinancialRecords.types do
        match Retention.ofType recordType with
        | Retention.Permanent _ -> Assert.False(deletable, RecordType.value recordType)
        | Retention.UntilResolved _ -> Assert.True(deletable, RecordType.value recordType)
