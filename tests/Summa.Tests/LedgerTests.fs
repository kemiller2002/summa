/// The ledger (WI-0012): v0.1 §1 general ledger, §2 periods, §16
/// dimensions, §18 audit and §19 idempotency.
module Summa.Tests.LedgerTests

open System
open Xunit
open Summa.Ledger.Money
open Summa.Ledger.Ledger

let context = { Who = "kevin"; When = DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero); Source = "summa-web"; CorrelationId = Some "c-1"; Provenance = None }

let ok result =
    match result with
    | Ok value -> value
    | Error problems -> failwith $"{problems}"

let refused result =
    match result with
    | Ok _ -> failwith "expected a refusal"
    | Error problems -> problems

let accounts =
    [ { Id = "cash"; Code = "1000"; Name = "Operating Cash"; Type = Asset; Active = true }
      { Id = "ar"; Code = "1100"; Name = "Accounts Receivable"; Type = Asset; Active = true }
      { Id = "equity"; Code = "3000"; Name = "Owner Equity"; Type = Equity; Active = true }
      { Id = "revenue"; Code = "4000"; Name = "Consulting Revenue"; Type = Revenue; Active = true }
      { Id = "software"; Code = "6100"; Name = "Software"; Type = Expense; Active = true }
      { Id = "old"; Code = "6900"; Name = "Retired"; Type = Expense; Active = false } ]

let chart = accounts |> List.fold (fun ledger a -> addAccount context a ledger |> ok) empty

let line account side = { AccountId = account; Side = side; Memo = None; Dimensions = noDimensions }
let day = DateOnly(2026, 10, 7)

let draft lines =
    { Date = day
      Description = "Test"
      Lines = lines
      Source = "manual" }

[<Fact>]
let ``money is exact fixed decimal and refuses what it cannot represent`` () =
    Assert.Equal(Some(usd 605000L), tryParse "USD" "6050")
    Assert.Equal(Some(usd 605050L), tryParse "USD" "6050.5")
    Assert.Equal(Some(usd -1L), tryParse "USD" "-0.01")
    Assert.Equal(None, tryParse "USD" "1.005")
    Assert.Equal(None, tryParse "USD" "1e3")
    // 0.1 + 0.2 is exactly 0.3 here.
    Assert.Equal(usd 30L, add (usd 10L) (usd 20L))
    // 7.5 hours at 175.00 = 1312.50; 1.5 x 0.03 = 0.045 rounds half away to 0.05.
    Assert.Equal(usd 131250L, extend 7500L (usd 17500L))
    Assert.Equal(usd 5L, extend 1500L (usd 3L))
    Assert.Equal("6050.00 USD", string (usd 605000L))
    Assert.Throws<InvalidOperationException>(fun () -> add (usd 1L) { Currency = "EUR"; Minor = 1L } |> ignore) |> ignore

[<Fact>]
let ``a balanced entry posts; every rule a draft breaks is reported`` () =
    let ledger, entry = post context "k1" "JE-000001" (draft [ line "ar" (Debit(usd 605000L)); line "revenue" (Credit(usd 605000L)) ]) chart |> ok
    Assert.Equal(Posted, entry.State)
    Assert.Equal<string list>([ "JE-000001" ], ledger.Journal)

    Assert.Equal<Problem list>([ TooFewLines; Unbalanced(usd 100L, usd 0L) ], validate chart (draft [ line "ar" (Debit(usd 100L)) ]))
    Assert.Equal<Problem list>([ Unbalanced(usd 100L, usd 90L) ], validate chart (draft [ line "ar" (Debit(usd 100L)); line "revenue" (Credit(usd 90L)) ]))
    Assert.Equal<Problem list>([ NonPositiveAmount "ar"; NonPositiveAmount "revenue" ], validate chart (draft [ line "ar" (Debit(usd -5L)); line "revenue" (Credit(usd -5L)) ]))
    Assert.Equal<Problem list>([ UnknownAccount "nope"; InactiveAccount "old" ], validate chart (draft [ line "nope" (Debit(usd 5L)); line "old" (Credit(usd 5L)) ]))
    Assert.Equal<Problem list>([ MixedCurrencies ], validate chart (draft [ line "ar" (Debit(usd 5L)); line "revenue" (Credit { Currency = "EUR"; Minor = 5L }) ]))

[<Fact>]
let ``retrying a post with the same key posts once; reusing a key for something else is refused`` () =
    let entry = draft [ line "ar" (Debit(usd 605000L)); line "revenue" (Credit(usd 605000L)) ]
    let ledger = [ 1..5 ] |> List.fold (fun l _ -> post context "issue:INV-001" "JE-000001" entry l |> ok |> fst) chart
    Assert.Equal(1, ledger.Entries.Count)
    Assert.Equal(1, ledger.Audit |> List.filter (fun a -> a.What = "journal-entry-posted") |> List.length)
    Assert.Equal<Problem list>([ IdempotencyKeyReused "issue:INV-001" ], post context "issue:INV-001" "JE-000002" { entry with Description = "x"; Lines = List.rev entry.Lines } ledger |> refused)

[<Fact>]
let ``posted entries are corrected by reversal, never edited or deleted`` () =
    let posted, _ = post context "k1" "JE-000001" (draft [ line "software" (Debit(usd 4900L)); line "cash" (Credit(usd 4900L)) ]) chart |> ok
    let reversed, reversal = reverse context "JE-000001" "JE-000002" day posted |> ok
    Assert.Equal(Reversed "JE-000002", reversed.Entries["JE-000001"].State)
    Assert.Equal(Some "JE-000001", reversal.Reverses)
    Assert.Equal(Credit(usd 4900L), reversal.Lines.Head.Side)
    Assert.Equal(2, reversed.Entries.Count)
    Assert.Equal(zero "USD", balance reversed "USD" day "software")
    Assert.Equal<Problem list>([ AlreadyReversed "JE-000001" ], reverse context "JE-000001" "JE-000003" day reversed |> refused)

[<Fact>]
let ``only open periods accept entries; closing changes nothing and reopening is privileged`` () =
    let posted, _ = post context "k1" "JE-000001" (draft [ line "cash" (Debit(usd 100000L)); line "equity" (Credit(usd 100000L)) ]) chart |> ok
    let closed = closePeriod context (2026, 10) posted
    Assert.True((posted.Entries = closed.Entries))
    Assert.Equal<Problem list>([ PeriodNotOpen(2026, 10) ], post context "k2" "JE-000002" (draft [ line "cash" (Debit(usd 1L)); line "equity" (Credit(usd 1L)) ]) closed |> refused)
    Assert.True(isBalanced closed (draft [ line "cash" (Debit(usd 1L)); line "equity" (Credit(usd 1L)) ]))
    Assert.Equal<Problem list>([ ReopenRequiresPrivilege ], reopenPeriod context false (2026, 10) closed |> refused)
    let reopened = reopenPeriod context true (2026, 10) closed |> ok
    Assert.Equal(Open, periodState reopened day)
    Assert.Equal<string list>([ "period-closed"; "period-reopened" ], reopened.Audit |> List.map _.What |> List.filter (fun w -> w.StartsWith "period"))

[<Fact>]
let ``balances follow each account type's normal side, and dimensions stay on lines`` () =
    let tagged = { line "revenue" (Credit(usd 605000L)) with Dimensions = { noDimensions with Client = Some "ABC"; Engagement = Some "Architecture Assessment" } }
    let ledger, _ = post context "k1" "JE-000001" (draft [ line "ar" (Debit(usd 605000L)); tagged ]) chart |> ok
    Assert.Equal(usd 605000L, balance ledger "USD" day "ar")
    Assert.Equal(usd 605000L, balance ledger "USD" day "revenue")
    Assert.Equal(Some "ABC", ledger.Entries["JE-000001"].Lines[1].Dimensions.Client)
    // One revenue account, not one per client.
    Assert.Equal(1, ledger.Accounts |> Map.filter (fun _ a -> a.Type = Revenue) |> Map.count)

[<Fact>]
let ``account codes are unique and every operation is audited with who, what, when, source and correlation`` () =
    Assert.Equal<Problem list>([ DuplicateAccountCode "1000" ], addAccount context { accounts.Head with Id = "cash2" } chart |> refused)
    let posted, _ = post context "k1" "JE-000001" (draft [ line "cash" (Debit(usd 1L)); line "equity" (Credit(usd 1L)) ]) chart |> ok
    let record = List.last posted.Audit
    Assert.Equal(("kevin", "journal-entry-posted", "summa-web", Some "c-1", "JE-000001"), (record.Who, record.What, record.Source, record.CorrelationId, record.Subject))
