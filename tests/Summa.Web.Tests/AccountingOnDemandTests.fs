/// Accounting on demand (WI-0044, v0.4 §21, §33, §34; SUM4-021, SUM4-033,
/// SUM4-034): ordinary workflows speak business language, the accounting
/// is one link away after something is posted, and every generated entry
/// says why it was posted.
module Summa.Web.Tests.AccountingOnDemandTests

open System
open System.Text.RegularExpressions
open Xunit
open Limen.Routing
open Summa.Web.Engine.Common
open Summa.Web.Engine.Accounting
open Summa.Web.Tests.Support

let private ctx = { Now = DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero); Actor = "local-person" }

let private run (msgs: Msg list) (model: Model) =
    msgs |> List.fold (fun (m, effects) msg -> let next, more = update ctx msg m in next, effects @ more) (model, [])

let private address hash : PageLocation =
    { Origin = "https://summa.example"; Path = "/app/"; Query = ""; Hash = hash }

let private go hash = LocationChanged(address hash)

let private value (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Value(Text t)) -> t
    | Some(_, Value(Flag f)) -> string f
    | _ -> failwith $"no scalar view value {name}"

let private texts (name: string) (model: Model) =
    match view model |> List.tryFind (fun (n, _) -> n = name) with
    | Some(_, Items rows) -> rows |> List.map (fun row -> row |> List.pick (fun (k, v) -> match k, v with "text", Text t -> Some t | _ -> None))
    | _ -> failwith $"no list view value {name}"

/// ABC Corp, and its invoice INV-0001 for 1,500.00 USD just issued.
let private issued () =
    let started =
        run [ Started(address ""); ConfigurationRead(Ok """{"environment":"local","environmentName":"test"}"""); Loaded None ] initial |> fst

    let withCustomer =
        run [ go "#/customers"; CustomerNameChanged "ABC Corp"; CustomerAddressChanged "1 Main Street"; CustomerTermsChanged "30"; CustomerAdded; go "#/invoices/new?customer=CUST-0001" ] started
        |> fst

    let key = withCustomer.Draft.Lines.Head.Key
    run [ LineDescriptionChanged(key, "Assessment"); LineHoursChanged(key, "10"); LineRateChanged(key, "150"); DraftSubmitted; DraftIssued ] withCustomer |> fst

[<Fact>]
let ``SUM4-034: issuing says so in business words, with the accounting entry one link away`` () =
    let model = issued ()
    let invoice = model.Books.Value.Books.Invoices["INV-0001"]
    Assert.Equal($"Invoice {invoice.Number} issued.", value "notice" model)
    Assert.Equal("True", value "hasNoticeEntry" model)
    Assert.Equal($"#/ledger/entries/{invoice.JournalEntryId}", value "noticeEntryHref" model)
    // The link belongs to that notice: the next one does not carry it.
    let later = run [ go "#/customers"; CustomerNameChanged "Beta"; CustomerAddressChanged "2 Main Street"; CustomerAdded ] model |> fst
    Assert.Equal("Customer Beta added.", value "notice" later)
    Assert.Equal("False", value "hasNoticeEntry" later)

[<Fact>]
let ``SUM4-034: recording a payment and issuing a credit memo offer their entries the same way`` () =
    let paid = run [ go "#/invoices/INV-0001?tab=payments"; PaymentAmountChanged "500"; PaymentRecorded ] (issued ()) |> fst
    Assert.Equal("Payment of 500.00 USD recorded.", value "notice" paid)
    let allocation = paid.Books.Value.Allocations |> List.exactlyOne
    Assert.Equal($"#/ledger/entries/{allocation.JournalEntryId}", value "noticeEntryHref" paid)

    let credited = run [ CreditAmountChanged "100"; CreditReasonChanged "Goodwill"; CreditMemoIssued ] paid |> fst
    let memo = credited.Books.Value.CreditMemos |> Map.toList |> List.exactlyOne |> snd
    Assert.Equal($"Credit memo {memo.Id} issued and applied.", value "notice" credited)
    Assert.Equal($"#/ledger/entries/{memo.JournalEntryId}", value "noticeEntryHref" credited)

[<Fact>]
let ``SUM4-021: the entry says why it was posted, then each line with its reason, in the requirement's form`` () =
    let model = issued ()
    let invoice = model.Books.Value.Books.Invoices["INV-0001"]
    let sentences = run [ go $"#/ledger/entries/{invoice.JournalEntryId}" ] model |> fst |> texts "entryExplanation"
    Assert.Equal($"Invoice {invoice.Number} was issued to ABC Corp for 1,500.00 USD.", sentences.Head)
    Assert.Contains(sentences, fun s -> Regex.IsMatch(s, @"^Debit Accounts Receivable \(\d+\) 1,500\.00 USD .*because the business owns, or is owed, that much more\.$"))
    Assert.Contains(sentences, fun s -> s.StartsWith "Credit " && s.EndsWith "because that much was earned.")
    Assert.Equal("Debits and credits are equal: 1,500.00 USD each, so the books still balance.", List.last sentences)

    let paid = run [ go "#/invoices/INV-0001?tab=payments"; PaymentAmountChanged "500"; PaymentReferenceChanged "ACH 77"; PaymentRecorded ] model |> fst
    let entry = (paid.Books.Value.Allocations |> List.exactlyOne).JournalEntryId
    let why = run [ go $"#/ledger/entries/{entry}" ] paid |> fst |> texts "entryExplanation" |> List.head
    Assert.StartsWith("ABC Corp paid 500.00 USD (ACH 77), and it was applied to what they owe", why)

/// Accounting terms (v0.4 §33) belong to the ledger and audit contexts.
let private accountingContexts =
    set [ "onLedger"; "onJournalEntry"; "onTrialBalance"; "onIncomeStatement"; "onBalanceSheet"; "onPeriods"; "onPeriod"; "onReports"; "onCpa"; "onHistoryTab" ]

let private jargon = Regex(@"\b(debits?|journal|posted|posting|control accounts?)\b", RegexOptions.IgnoreCase)

/// The visible text of the page, split by whether it is inside an
/// accounting context.
let private pageText () =
    let html = Regex.Replace(readRepoFile "app/index.html", @"<!--.*?-->", "", RegexOptions.Singleline)
    let tokens = Regex.Matches(html, @"<template\b[^>]*>|</template>|<[^>]*>|[^<]+")

    tokens
    |> Seq.fold
        (fun (stack: string list, inside: string list, outside: string list) (t: System.Text.RegularExpressions.Match) ->
            let v = t.Value

            if v.StartsWith "<template" then
                let name = Regex.Match(v, "data-if=\"([^\"]+)\"").Groups[1].Value
                name :: stack, inside, outside
            elif v = "</template>" then
                List.tail stack, inside, outside
            elif v.StartsWith "<" then
                stack, inside, outside
            elif stack |> List.exists accountingContexts.Contains then
                stack, v :: inside, outside
            else
                stack, inside, v :: outside)
        ([], [], [])
    |> fun (_, inside, outside) -> String.Join(" ", inside), String.Join(" ", outside)

[<Fact>]
let ``SUM4-033: outside the ledger and audit contexts the page never says debit, journal or posting`` () =
    let inside, outside = pageText ()
    let found = jargon.Matches(outside) |> Seq.map _.Value |> List.ofSeq
    Assert.True(found.IsEmpty, $"accounting terms in ordinary workflows: %A{found}")
    // Not vacuous: the ledger and audit contexts do use them, and each context named exists.
    Assert.True(jargon.Matches(inside).Count >= 5)
    let html = readRepoFile "app/index.html"
    for context in accountingContexts do
        Assert.Contains($"data-if=\"{context}\"", html)

[<Fact>]
let ``SUM4-033: the confirmations in ordinary workflows say "recorded in the books", not posting`` () =
    let model = issued ()
    let draft = run [ go "#/invoices/new?customer=CUST-0001" ] model |> fst
    let key = draft.Draft.Lines.Head.Key
    let reviewed = run [ LineDescriptionChanged(key, "More"); LineHoursChanged(key, "1"); LineRateChanged(key, "100"); DraftSubmitted ] draft |> fst
    let issueText = value "confirmIssueText" reviewed
    Assert.Contains("recorded in the books", issueText)
    Assert.DoesNotMatch(jargon, issueText)
    let credit = run [ go "#/invoices/INV-0001?tab=payments"; CreditAmountChanged "100"; CreditReasonChanged "Goodwill" ] model |> fst
    Assert.DoesNotMatch(jargon, value "confirmCreditText" credit)
