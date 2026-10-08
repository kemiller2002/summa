/// Consuming the released Chrona billing contract (WI-0025): Chrona's
/// publications become Summa's own snapshots, and Summa's feedback is
/// derived from its invoices, voids and reviews (INV-CHR, INV-PROV-003..005,
/// SUM0-024..026, SUM1-017).
module Summa.Tests.ChronaImportTests

open System
open Xunit
open Summa.Contracts.ChronaBilling.V1
open Summa.Ledger
open Summa.Ledger.Money
open Summa.Tests.LedgerTests
open Summa.Tests.BillingTests

let private published (id: string) (activity: string) (revision: int) : BillableTime =
    { PublicationId = id
      Source = { OrganizationId = "org-1"; ActivityId = activity; Revision = revision }
      PerformerId = "github:1"
      Service =
        { BusinessDate = DateOnly(2026, 10, 6)
          Zone = "Europe/London"
          Interval = None }
      Classification =
        { ProjectId = "PRJ-A"
          ClientId = Some "CUST-ABC"
          EngagementId = None
          ActivityTypeId = "development"
          Description = "Build the ledger"
          BusinessPurpose = "Ship v0.1"
          Tags = [] }
      ExactMinutes = 45
      BillableMinutes = 48
      Policy = { PolicyId = "six-minute-up"; Version = 1 }
      BillingReference = { RateReference = Some "senior"; BillingClass = None; ContractReference = None }
      Approval = ApprovedBy("github:2", at "2026-10-07T10:00:00Z")
      Origin = Known(Imported "toggl", Some { SourceSystem = "toggl"; ObservationId = "obs-9"; ExternalUrl = None }, Some "EXE-1")
      Lineage = [ "act-0" ]
      WorkItemReference = Some "WI-0042"
      Supersedes = None
      PublishedAt = at "2026-10-07T11:00:00Z" }

let private receiveAll (messages: Publication list) (r: Payments.Receivables) =
    messages |> List.fold (fun state m -> ChronaImport.receive context m state |> ok) r

let private priced () =
    fresh () |> withRates [ Sources.Everyone, usd 15000L ]

[<Fact>]
let ``a publication becomes Summa's snapshot: Chrona's ids, revision, minutes and origin, nothing more`` () =
    let r = priced () |> receiveAll [ BillableTimePublished(published "pub-1" "act-1" 2) ]
    let t = r.Books.Time["pub-1"]
    Assert.Equal(("org-1", "act-1", 2), (t.OrganizationId, t.ActivityId, t.Revision))
    Assert.Equal((45, 48), (t.ExactMinutes, t.BillableMinutes))
    Assert.Equal(Sources.OriginKnown("imported:toggl", Some "toggl:obs-9", Some "EXE-1"), t.Origin)
    Assert.Equal<string list>([ "act-0" ], t.Lineage)
    Assert.Equal((Some "senior", Some "WI-0042"), (t.RateReference, t.WorkItem))
    // Unknown origin stays unknown; it is never inferred from the approver.
    let unknown = priced () |> receiveAll [ BillableTimePublished { published "pub-2" "act-2" 1 with Origin = Unknown } ]
    Assert.Equal(Sources.OriginUnknown, unknown.Books.Time["pub-2"].Origin)

[<Fact>]
let ``a message that breaks the contract is refused before anything is imported`` () =
    let r = priced ()

    match ChronaImport.receive context (BillableTimePublished { published "pub-1" "act-1" 1 with BillableMinutes = -1 }) r with
    | Error(ChronaImport.InvalidMessage problems) -> Assert.NotEmpty problems
    | other -> failwith $"expected a contract refusal, got %A{other}"

    Assert.True(Result.isError (ChronaImport.receive context (PublicationWithdrawn { PublicationId = "pub-404"; Source = { OrganizationId = "org-1"; ActivityId = "act-9"; Revision = 2 }; Reason = NoLongerBillable; WithdrawnAt = at "2026-10-08T10:00:00Z" }) r))

[<Fact>]
let ``feedback reports invoiced time, voids and open reviews, and every message obeys the contract`` () =
    let r =
        priced ()
        |> receiveAll [ BillableTimePublished(published "pub-1" "act-1" 1); BillableTimePublished(published "pub-2" "act-2" 1) ]

    let accepted, invoice =
        r
        |> Billing.propose context (proposalFor [ "pub-1"; "pub-2" ])
        |> ok
        |> Billing.markReady context false "P-1"
        |> ok
        |> Billing.accept context "P-1" issuing
        |> ok

    let invoiced = ChronaImport.feedback accepted

    Assert.Equal<Feedback list>(
        [ for p in [ "pub-1"; "pub-2" ] ->
              InvoicedExternally
                  { OrganizationId = "org-1"
                    PublicationId = p
                    ActivityId = (if p = "pub-1" then "act-1" else "act-2")
                    Revision = 1
                    InvoiceReference = invoice.Number
                    At = invoice.IssuedAt } ],
        invoiced
    )

    // Chrona corrects invoiced time and withdraws other time: reviews are raised.
    let corrected =
        accepted
        |> receiveAll
            [ BillableTimePublished { published "pub-1b" "act-1" 2 with Supersedes = Some "pub-1" }
              PublicationWithdrawn { PublicationId = "pub-2"; Source = { OrganizationId = "org-1"; ActivityId = "act-2"; Revision = 2 }; Reason = Replaced [ "act-3"; "act-4" ]; WithdrawnAt = at "2026-10-08T10:00:00Z" } ]

    let reviews = ChronaImport.feedback corrected |> List.choose (function AdjustmentNeeded a -> Some(a.PublicationId, a.Reason) | _ -> None)

    Assert.Equal<(string * string) list>(
        [ "pub-1", "Chrona corrected time after it was invoiced"; "pub-2", "Chrona withdrew invoiced time: replaced by act-3, act-4" ],
        reviews
    )

    // A resolved review is no longer reported; a void asks Chrona to review the time.
    let resolved = { corrected with Books = Billing.resolveReview context "review-pub-1" "No action: within the agreed cap" corrected.Books |> ok }
    let request: Corrections.VoidRequest = { InvoiceId = invoice.InvoiceId; Reason = "Wrong customer"; Date = DateOnly(2026, 10, 9); JournalEntryId = "JE-VOID-1" }
    let voided = Corrections.voidInvoice context request resolved |> ok
    let messages = ChronaImport.feedback voided

    Assert.Equal<string list>(
        [ "pub-1: Invoice EF-2026-0001 was voided; the time is billable again"
          "pub-2: Invoice EF-2026-0001 was voided; the time is billable again"
          "pub-2: Chrona withdrew invoiced time: replaced by act-3, act-4" ],
        messages |> List.choose (function AdjustmentNeeded a -> Some $"{a.PublicationId}: {a.Reason}" | _ -> None)
    )

    for message in invoiced @ messages do
        Assert.Empty(Validate.feedback message)
        Assert.True(Result.isOk (Codec.decodeFeedback (Codec.encodeFeedback message)))
