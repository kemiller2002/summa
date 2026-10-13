/// Recurring billing templates (WI-0029 slice 12, INV-REC-001 to 004).
module Summa.Tests.RecurringTests

open System
open Xunit
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Ledger.Invoicing
open Summa.Ledger.Payments
open Summa.Ledger.Recurring
open Summa.Tests.LedgerTests
open Summa.Tests.InvoicingTests

/// A monthly retainer for ABC: 10 hours at 150.00 on the 15th, Net 15,
/// sent to accounts payable, from 20 October 2026.
let private retainer =
    { Id = "RET-ABC"
      Version = 1
      CustomerId = abc.Id
      EngagementId = None
      Currency = "USD"
      Schedule = Monthly 15
      Lines = [ { consulting 10000L 15000L with Description = "Monthly retainer" } ]
      Terms = Some(Net 15)
      Recipients = Some { To = [ "ap@abc.example" ]; Cc = []; ReplyTo = None }
      StartsOn = DateOnly(2026, 10, 20)
      EndsOn = None
      Occurrences = None
      Active = true }

let private saved template = start (Recurring.saveTemplate context template books |> ok)
let private on (date: DateOnly) = { context with When = DateTimeOffset(date.ToDateTime(TimeOnly(6, 0)), TimeSpan.Zero) }
let private generate (today: DateOnly) r = generateDue (on today) today "RET-ABC" r |> ok

[<Fact>]
let ``INV-REC-001: a schedule makes proposals for a person to review, never a draft, an invoice or an entry`` () =
    let r = saved retainer
    let after, made = generate (DateOnly(2026, 11, 15)) r
    Assert.Equal<string list>([ "RET-ABC-20261115" ], made)
    let proposal = after.Books.Proposals["RET-ABC-20261115"]
    Assert.Equal(Proposed, proposal.State)
    Assert.Equal<InvoiceLine list>(retainer.Lines, proposal.Lines |> List.map _.Line)
    Assert.True((r.Books.Drafts = after.Books.Drafts))
    Assert.True((r.Books.Invoices = after.Books.Invoices))
    Assert.True((r.Books.Ledger.Entries = after.Books.Ledger.Entries))
    Assert.Contains(after.Books.Ledger.Audit, fun a -> a.What = "recurring-proposal-generated" && a.Subject = "RET-ABC-20261115")

[<Fact>]
let ``INV-REC-002: a template holds customer, schedule, lines, pricing, terms, delivery defaults, start and end, and derives its next date`` () =
    let r = saved retainer
    let kept = r.Books.Templates["RET-ABC@1"]
    Assert.Equal(abc.Id, kept.CustomerId)
    Assert.Equal(Monthly 15, kept.Schedule)
    Assert.Equal(usd 15000L, kept.Lines.Head.UnitPrice)
    Assert.Equal(Some(Net 15), kept.Terms)
    Assert.Equal<string list>([ "ap@abc.example" ], kept.Recipients.Value.To)
    // The first 15th on or after the start; then each month.
    let dates = occurrences kept |> Seq.truncate 3 |> List.ofSeq
    Assert.Equal<DateOnly list>([ DateOnly(2026, 11, 15); DateOnly(2026, 12, 15); DateOnly(2027, 1, 15) ], dates)
    Assert.Equal(Some(DateOnly(2026, 11, 15)), nextGeneration r.Books "RET-ABC")
    let after, _ = generate (DateOnly(2026, 11, 15)) r
    Assert.Equal(Some(DateOnly(2026, 12, 15)), nextGeneration after.Books "RET-ABC")
    // An end date and a count bound it; every two weeks counts from the start.
    Assert.Equal(2, occurrences { kept with EndsOn = Some(DateOnly(2026, 12, 31)) } |> Seq.length)
    Assert.Equal(4, occurrences { kept with Occurrences = Some 4 } |> Seq.length)

    Assert.Equal<DateOnly list>(
        [ DateOnly(2026, 10, 20); DateOnly(2026, 11, 3) ],
        occurrences { kept with Schedule = EveryWeeks 2 } |> Seq.truncate 2 |> List.ofSeq
    )
    // A paused template has no next date; schedules that cannot be kept are refused.
    let paused = start (Recurring.saveTemplate context { kept with Version = 2; Active = false } r.Books |> ok)
    Assert.Equal(None, nextGeneration paused.Books "RET-ABC")
    Assert.Equal<string list>([], generate (DateOnly(2027, 6, 1)) paused |> snd)
    Assert.Equal<RecurringProblem list>([ InvalidSchedule "a monthly day is from 1 to 28" ], Recurring.saveTemplate context { retainer with Schedule = Monthly 31 } books |> refused)
    Assert.Equal<RecurringProblem list>([ NoTemplateLines ], Recurring.saveTemplate context { retainer with Lines = [] } books |> refused)
    Assert.Equal<RecurringProblem list>([ UnknownCustomerForTemplate "CUST-NONE" ], Recurring.saveTemplate context { retainer with CustomerId = "CUST-NONE" } books |> refused)

[<Fact>]
let ``INV-REC-003: editing a template makes a new version that only later proposals use`` () =
    let november, _ = generate (DateOnly(2026, 11, 15)) (saved retainer)
    let earlier = november.Books.Proposals["RET-ABC-20261115"]
    let raised = { retainer with Version = 2; Lines = [ { retainer.Lines.Head with UnitPrice = usd 17500L } ] }
    let edited = { november with Books = Recurring.saveTemplate context raised november.Books |> ok }
    // The earlier version and the earlier proposal are unchanged.
    Assert.Equal(retainer, edited.Books.Templates["RET-ABC@1"])
    Assert.True((earlier = edited.Books.Proposals["RET-ABC-20261115"]))
    let december, _ = generate (DateOnly(2026, 12, 15)) edited
    Assert.Equal(usd 17500L, december.Books.Proposals["RET-ABC-20261215"].Lines.Head.Line.UnitPrice)
    Assert.True((earlier = december.Books.Proposals["RET-ABC-20261115"]))
    Assert.Contains(december.Books.Proposals["RET-ABC-20261215"].Contributions, fun c -> c.What = "generated from recurring template RET-ABC version 2 for 2026-12-15")
    // Versions move forward one at a time; an old version is never rewritten.
    Assert.Equal<RecurringProblem list>([ VersionNotNext 3 ], Recurring.saveTemplate context { raised with Version = 5 } december.Books |> refused)
    Assert.Equal<RecurringProblem list>([ VersionNotNext 3 ], Recurring.saveTemplate context { retainer with Terms = None } december.Books |> refused)

[<Fact>]
let ``INV-REC-004: a retry or a restarted scheduler makes no second proposal for an occurrence`` () =
    let r = saved retainer
    // Down from October to February: one proposal per missed occurrence.
    let caughtUp, made = generate (DateOnly(2027, 2, 20)) r
    Assert.Equal<string list>([ "RET-ABC-20261115"; "RET-ABC-20261215"; "RET-ABC-20270115"; "RET-ABC-20270215" ], made)
    // The same run again, and a run by another scheduler later the same day, change nothing.
    let again, none = generate (DateOnly(2027, 2, 20)) caughtUp
    Assert.Empty none
    Assert.True((caughtUp = again))
    Assert.Empty(generateDue { context with Who = "scheduler-2" } (DateOnly(2027, 2, 20)) "RET-ABC" caughtUp |> ok |> snd)
    // An abandoned occurrence stays abandoned; it is not proposed again.
    let abandoned = Billing.abandon context "RET-ABC-20261215" caughtUp |> ok
    Assert.Empty(generate (DateOnly(2027, 2, 28)) abandoned |> snd)
    Assert.Equal(4, abandoned.Books.Proposals.Count)

[<Fact>]
let ``INV-REC-002: an accepted recurring proposal's draft takes the template version's terms and delivery defaults`` () =
    let made, _ = generate (DateOnly(2026, 11, 15)) (saved retainer)
    let proposal = made.Books.Proposals["RET-ABC-20261115"]
    Assert.Equal(Some { TemplateId = "RET-ABC"; TemplateVersion = 1; Date = DateOnly(2026, 11, 15) }, proposal.Occurrence)
    let ready = Billing.markReady context false "RET-ABC-20261115" made |> ok
    let accepted, invoice = Billing.accept context "RET-ABC-20261115" { request with IssueDate = DateOnly(2026, 11, 15) } ready |> ok
    // Net 15 from the template, not the customer's Net 30.
    Assert.Equal(DateOnly(2026, 11, 30), invoice.DueDate)
    Assert.Equal(Some "RET-ABC-20261115", accepted.Books.Proposals |> Map.tryFindKey (fun _ p -> p.State = Accepted invoice.InvoiceId))
    // The draft it became carries the template's delivery defaults.
    let draft = Billing.draftOf ready.Books proposal "D-9"
    Assert.Equal(Some { To = [ "ap@abc.example" ]; Cc = []; ReplyTo = None }, draft.Recipients)
    Assert.Equal(Some(Net 15), draft.Terms)
    // A proposal no template made keeps the customer's defaults.
    Assert.Equal(None, (Billing.draftOf ready.Books { proposal with Occurrence = None } "D-9").Recipients)

[<Fact>]
let ``SUM3-006: a routine run proposes every template's due occurrences, and one that cannot propose holds back no other`` () =
    let r = saved retainer
    // A template that saveTemplate would refuse, planted to show the run isolates it.
    let broken = { retainer with Id = "AAA-BROKEN"; CustomerId = "CUST-NONE" }
    let r = { r with Books = { r.Books with Templates = r.Books.Templates.Add("AAA-BROKEN@1", broken) } }
    let after, made, refused = generateAllDue (on (DateOnly(2026, 12, 20))) (DateOnly(2026, 12, 20)) r
    Assert.Equal<string list>([ "RET-ABC-20261115"; "RET-ABC-20261215" ], made)
    match refused with
    | [ "AAA-BROKEN", [ ProposalRefused _ ] ] -> ()
    | other -> failwith $"%A{other}"
    Assert.False(after.Books.Proposals.ContainsKey "AAA-BROKEN-20261115")
    // Again the same day: nothing new, and the broken template is still the only one refused.
    let _, none, still = generateAllDue (on (DateOnly(2026, 12, 20))) (DateOnly(2026, 12, 20)) after
    Assert.Empty none
    Assert.Equal(1, still.Length)
