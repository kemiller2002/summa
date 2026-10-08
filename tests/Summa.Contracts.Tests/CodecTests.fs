module Summa.Contracts.Tests.CodecTests

open System
open Xunit
open Summa.Contracts
open Summa.Contracts.ChronaBilling.V1
open Summa.Contracts.Tests.Support

let private roundTrip (message: Publication) =
    Codec.tryEncodePublication message |> Result.bind Codec.decodePublication

/// A deterministic spread of valid publications: every origin, approval,
/// interval and optional-field combination the types allow.
let private variants =
    let origins =
        [ Unknown
          Known(Manual, None, None)
          Known(Timer, None, Some "EXE-summa.run-1")
          Known(Imported "praxis", Some { SourceSystem = "praxis"; ObservationId = "o-1"; ExternalUrl = None }, None)
          Known(
              Imported "github",
              Some
                  { SourceSystem = "github"
                    ObservationId = "o-2"
                    ExternalUrl = Some "https://example.com/x?y=1" },
              Some "EXE-1"
          ) ]

    let approvals = [ ApprovalNotRequired; ApprovedBy("github:9", at "2026-10-07T23:59:59.9999999-07:00") ]

    let intervals =
        [ None; Some(at "2026-10-06T00:00:00Z", at "2026-10-06T00:01:00Z") ]

    [ for origin in origins do
          for approval in approvals do
              for interval in intervals do
                  for supersedes in [ None; Some "pub-0" ] do
                      BillableTimePublished
                          { billableTime with
                              Origin = origin
                              Approval = approval
                              Service = { billableTime.Service with Interval = interval }
                              Supersedes = supersedes
                              Lineage = (if supersedes.IsSome then [ "a"; "b" ] else [])
                              WorkItemReference = supersedes |> Option.map (fun _ -> "WI-1") }
      for reason in [ Voided; NoLongerBillable; Replaced [ "act-2" ]; Replaced [ "act-2"; "act-3" ] ] do
          PublicationWithdrawn
              { PublicationId = "pub-1"
                Source = source 3
                Reason = reason
                WithdrawnAt = at "2026-10-08T00:00:00+05:30" } ]

[<Fact>]
let ``every publication round-trips through canonical JSON unchanged`` () =
    Assert.True(List.length variants >= 40)

    for message in variants do
        Assert.Equal(Ok message, roundTrip message)

[<Fact>]
let ``feedback round-trips and matches what Chrona records as an invoice report`` () =
    let invoiced =
        InvoicedExternally
            { OrganizationId = "org-1"
              PublicationId = "pub-1"
              ActivityId = "act-1"
              Revision = 2
              InvoiceReference = "INV-1"
              At = at "2026-10-09T00:00:00Z" }

    let text = Codec.tryEncodeFeedback invoiced |> Result.defaultWith (failwithf "%A")
    Assert.Equal(Ok invoiced, Codec.decodeFeedback text)
    // Chrona.Domain.Review.InvoiceReport is { PublicationId; ActivityId;
    // Revision; InvoiceReference; At }: every one of its fields travels.
    for name in [ "publicationId"; "activityId"; "revision"; "invoiceReference"; "at" ] do
        Assert.Contains($"\"{name}\":", text)

[<Fact>]
let ``equal messages have equal digests and any change changes the digest`` () =
    let digest m = Codec.publicationJson m |> Codec.digest
    let first = BillableTimePublished billableTime
    Assert.Equal(digest first, digest (BillableTimePublished { billableTime with Lineage = [] }))
    Assert.NotEqual<string>(digest first, digest (BillableTimePublished { billableTime with Source = source 3 }))
    Assert.Equal(64, (digest first).Length)

[<Fact>]
let ``a producer cannot encode a message that breaks a rule`` () =
    let broken =
        BillableTimePublished
            { billableTime with
                PerformerId = ""
                ExactMinutes = -1
                Classification = { billableTime.Classification with Description = "Bearer abc.def" } }

    match Codec.tryEncodePublication broken with
    | Ok _ -> failwith "encoded a broken message"
    | Error problems ->
        Assert.Equal<string list>(
            [ "$.message.performerId"; "$.message.classification.description"; "$.message.exactMinutes" ],
            paths problems
        )

[<Fact>]
let ``decoding reports every problem, not only the first`` () =
    let text =
        Codec.encodePublication (BillableTimePublished { billableTime with PublicationId = " "; Source = source 0 })

    match Codec.decodePublication text with
    | Ok _ -> failwith "accepted"
    | Error problems -> Assert.Equal<string list>([ "$.message.publicationId"; "$.message.source.revision" ], paths problems)

[<Fact>]
let ``structural problems in different fields are all reported`` () =
    let text =
        (Codec.encodePublication (BillableTimePublished billableTime))
            .Replace("\"exactMinutes\":45", "\"exactMinutes\":\"45\"")
            .Replace("\"zone\":\"Europe/London\"", "\"zone\":7")

    match Codec.decodePublication text with
    | Ok _ -> failwith "accepted"
    | Error problems -> Assert.Equal<string list>([ "$.message.service.zone"; "$.message.exactMinutes" ], paths problems)

[<Fact>]
let ``a newer minor version is read and its new members are ignored`` () =
    let current = Codec.encodePublication (BillableTimePublished billableTime)

    let newer =
        current
            .Replace("\"version\":\"1.0\"", "\"version\":\"1.4\"")
            .Replace("\"performerId\":", "\"performerRole\":\"lead\",\"performerId\":")
            .Replace("{\"contract\":", "{\"routing\":{\"lane\":2},\"contract\":")

    Assert.Equal(Ok(BillableTimePublished billableTime), Codec.decodePublication newer)

[<Fact>]
let ``the same members are refused at this version`` () =
    let current = Codec.encodePublication (BillableTimePublished billableTime)
    let extra = current.Replace("\"performerId\":", "\"performerRole\":\"lead\",\"performerId\":")

    match Codec.decodePublication extra with
    | Ok _ -> failwith "accepted"
    | Error problems -> Assert.Equal<string list>([ "$.message.performerRole" ], paths problems)

[<Fact>]
let ``a message sent the wrong way is refused`` () =
    let publication = Codec.encodePublication (BillableTimePublished billableTime)

    match Codec.decodeFeedback publication with
    | Ok _ -> failwith "accepted"
    | Error problems -> Assert.Equal<string list>([ "$.kind" ], paths problems)

[<Fact>]
let ``origin is never inferred: unknown stays unknown`` () =
    let unknown = BillableTimePublished { billableTime with Origin = Unknown }
    let text = Codec.encodePublication unknown
    Assert.Contains("\"origin\":{\"kind\":\"unknown\"}", text)
    Assert.Equal(Ok unknown, Codec.decodePublication text)

[<Theory>]
[<InlineData("ghp_abcdef0123456789")>]
[<InlineData("github_pat_11ABC")>]
[<InlineData("-----BEGIN PRIVATE KEY-----")>]
[<InlineData("bearer eyJhbGciOi")>]
[<InlineData("Authorization: token x")>]
let ``credential-shaped text is refused anywhere`` (value: string) =
    Assert.True(Validate.looksLikeCredential value)

    let message =
        BillableTimePublished { billableTime with BillingReference = { billableTime.BillingReference with RateReference = Some value } }

    Assert.Equal<string list>([ "$.message.billingReference.rateReference" ], Validate.publication message |> paths)

[<Theory>]
[<InlineData("github:737074")>]
[<InlineData("Ghost writer")>]
[<InlineData("Bearer of good news is the client")>]
let ``ordinary text is not mistaken for a credential`` (value: string) =
    Assert.False(Validate.looksLikeCredential value)
