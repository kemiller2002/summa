/// The canonical JSON form of version-1 messages.
///
/// Every message travels in an envelope:
/// `{"contract":"summa.chrona-billing","kind":"...","message":{...},"version":"1.0"}`.
/// Encoding is canonical (`Json.serialize`), so equal messages have equal
/// bytes and `digest` identifies content. Decoding treats the text as
/// untrusted: structure first, then every rule in `Validate`. A message of a
/// newer minor version of major 1 is read, ignoring members this version does
/// not define; any other major is refused, never guessed at.
module Summa.Contracts.ChronaBilling.V1.Codec

open System
open System.Security.Cryptography
open System.Text.RegularExpressions
open Summa.Contracts

// Encoding --------------------------------------------------------------

let private obj (members: (string * Json) list) = Json.Object members

let private source (s: SourceReference) =
    obj
        [ "organizationId", Encode.text s.OrganizationId
          "activityId", Encode.text s.ActivityId
          "revision", Encode.integer s.Revision ]

let private service (s: ServicePeriod) =
    obj
        [ "businessDate", Encode.date s.BusinessDate
          "zone", Encode.text s.Zone
          "start", Encode.optional Encode.instant (s.Interval |> Option.map fst)
          "finish", Encode.optional Encode.instant (s.Interval |> Option.map snd) ]

let private classification (c: Classification) =
    obj
        [ "projectId", Encode.text c.ProjectId
          "clientId", Encode.optional Encode.text c.ClientId
          "engagementId", Encode.optional Encode.text c.EngagementId
          "activityTypeId", Encode.text c.ActivityTypeId
          "description", Encode.text c.Description
          "businessPurpose", Encode.text c.BusinessPurpose
          "tags", Encode.list Encode.text c.Tags ]

let private approval =
    function
    | ApprovedBy(approver, at) ->
        obj [ "kind", Json.String "approved"; "approver", Encode.text approver; "at", Encode.instant at ]
    | ApprovalNotRequired -> obj [ "kind", Json.String "not-required" ]

let private observation (o: ObservationReference) =
    obj
        [ "sourceSystem", Encode.text o.SourceSystem
          "observationId", Encode.text o.ObservationId
          "externalUrl", Encode.optional Encode.text o.ExternalUrl ]

let private origin =
    function
    | Unknown -> obj [ "kind", Json.String "unknown" ]
    | Known(method, observed, executionId) ->
        let name, system =
            match method with
            | Manual -> "manual", None
            | Timer -> "timer", None
            | Imported s -> "imported", Some s

        obj
            [ "kind", Json.String "known"
              "method", Json.String name
              "sourceSystem", Encode.optional Encode.text system
              "observation", Encode.optional observation observed
              "executionId", Encode.optional Encode.text executionId ]

let private billableTime (t: BillableTime) =
    obj
        [ "publicationId", Encode.text t.PublicationId
          "source", source t.Source
          "performerId", Encode.text t.PerformerId
          "service", service t.Service
          "classification", classification t.Classification
          "exactMinutes", Encode.integer t.ExactMinutes
          "billableMinutes", Encode.integer t.BillableMinutes
          "policy", obj [ "policyId", Encode.text t.Policy.PolicyId; "version", Encode.integer t.Policy.Version ]
          "billingReference",
          obj
              [ "rateReference", Encode.optional Encode.text t.BillingReference.RateReference
                "billingClass", Encode.optional Encode.text t.BillingReference.BillingClass
                "contractReference", Encode.optional Encode.text t.BillingReference.ContractReference ]
          "approval", approval t.Approval
          "origin", origin t.Origin
          "lineage", Encode.list Encode.text t.Lineage
          "workItemReference", Encode.optional Encode.text t.WorkItemReference
          "supersedes", Encode.optional Encode.text t.Supersedes
          "publishedAt", Encode.instant t.PublishedAt ]

let private withdrawalReason =
    function
    | Voided -> obj [ "kind", Json.String "voided" ]
    | Replaced by -> obj [ "kind", Json.String "replaced"; "by", Encode.list Encode.text by ]
    | NoLongerBillable -> obj [ "kind", Json.String "no-longer-billable" ]

let private withdrawal (w: Withdrawal) =
    obj
        [ "publicationId", Encode.text w.PublicationId
          "source", source w.Source
          "reason", withdrawalReason w.Reason
          "withdrawnAt", Encode.instant w.WithdrawnAt ]

let private feedbackBody organization publication activity revision (rest: (string * Json) list) =
    obj (
        [ "organizationId", Encode.text organization
          "publicationId", Encode.text publication
          "activityId", Encode.text activity
          "revision", Encode.integer revision ]
        @ rest
    )

let private envelope (kind: string) (message: Json) =
    obj
        [ "contract", Json.String Contract.Name
          "version", Json.String Contract.Version
          "kind", Json.String kind
          "message", message ]

/// The JSON value of a message Chrona sends.
let publicationJson (message: Publication) : Json =
    match message with
    | BillableTimePublished t -> envelope "billable-time-published" (billableTime t)
    | PublicationWithdrawn w -> envelope "publication-withdrawn" (withdrawal w)

/// The JSON value of a message Summa sends.
let feedbackJson (message: Feedback) : Json =
    match message with
    | InvoicedExternally i ->
        envelope
            "invoiced"
            (feedbackBody
                i.OrganizationId
                i.PublicationId
                i.ActivityId
                i.Revision
                [ "invoiceReference", Encode.text i.InvoiceReference; "at", Encode.instant i.At ])
    | AdjustmentNeeded a ->
        envelope
            "adjustment-required"
            (feedbackBody a.OrganizationId a.PublicationId a.ActivityId a.Revision [ "reason", Encode.text a.Reason; "at", Encode.instant a.At ])

/// Canonical text of a message Chrona sends. Does not validate; see
/// `tryEncodePublication`.
let encodePublication (message: Publication) : string = publicationJson message |> Json.serialize

/// Canonical text of a message Summa sends. Does not validate; see
/// `tryEncodeFeedback`.
let encodeFeedback (message: Feedback) : string = feedbackJson message |> Json.serialize

/// Canonical text, or every rule the message breaks.
let tryEncodePublication (message: Publication) : Decoded<string> =
    match Validate.publication message with
    | [] -> Ok(encodePublication message)
    | problems -> Error problems

/// Canonical text, or every rule the message breaks.
let tryEncodeFeedback (message: Feedback) : Decoded<string> =
    match Validate.feedback message with
    | [] -> Ok(encodeFeedback message)
    | problems -> Error problems

/// The SHA-256 of a JSON value's canonical bytes, lowercase hex: the content
/// identity of a message (for example, to tell a retry of a publication id
/// from a conflicting reuse of it).
let digest (json: Json) : string =
    SHA256.HashData(Json.toUtf8 json) |> Convert.ToHexString |> _.ToLowerInvariant()

// Decoding --------------------------------------------------------------

let private (<!>) f a = Result.map f a
let private (<*>) f a = Decode.apply f a

/// Reads an object after checking its member names.
let private shape strict path names (read: Json -> Decoded<'a>) (json: Json) : Decoded<'a> =
    Decode.map2 (fun () value -> value) (Decode.closed strict path names json) (read json)

let private sourceOf strict path json =
    json
    |> shape strict path [ "organizationId"; "activityId"; "revision" ] (fun j ->
        (fun organization activity revision ->
            { OrganizationId = organization
              ActivityId = activity
              Revision = revision })
        <!> Decode.member' Decode.text path "organizationId" j
        <*> Decode.member' Decode.text path "activityId" j
        <*> Decode.member' Decode.integer path "revision" j)

let private serviceOf strict path json =
    json
    |> shape strict path [ "businessDate"; "zone"; "start"; "finish" ] (fun j ->
        let interval =
            Decode.map2
                (fun start finish -> start, finish)
                (Decode.member' (Decode.optional Decode.instant) path "start" j)
                (Decode.member' (Decode.optional Decode.instant) path "finish" j)
            |> Result.bind (function
                | Some s, Some f -> Ok(Some(s, f))
                | None, None -> Ok None
                | _ -> Decode.problem $"{path}.finish" "start and finish are given together or not at all")

        (fun date zone interval ->
            { BusinessDate = date
              Zone = zone
              Interval = interval })
        <!> Decode.member' Decode.date path "businessDate" j
        <*> Decode.member' Decode.text path "zone" j
        <*> interval)

let private optionalText = Decode.optional Decode.text

let private classificationOf strict path json =
    json
    |> shape
        strict
        path
        [ "projectId"; "clientId"; "engagementId"; "activityTypeId"; "description"; "businessPurpose"; "tags" ]
        (fun j ->
            (fun project client engagement activityType description purpose tags ->
                { ProjectId = project
                  ClientId = client
                  EngagementId = engagement
                  ActivityTypeId = activityType
                  Description = description
                  BusinessPurpose = purpose
                  Tags = tags })
            <!> Decode.member' Decode.text path "projectId" j
            <*> Decode.member' optionalText path "clientId" j
            <*> Decode.member' optionalText path "engagementId" j
            <*> Decode.member' Decode.text path "activityTypeId" j
            <*> Decode.member' Decode.text path "description" j
            <*> Decode.member' Decode.text path "businessPurpose" j
            <*> Decode.member' (Decode.list Decode.text) path "tags" j)

let private kindOf path json = Decode.member' Decode.text path "kind" json

let private approvalOf strict path json =
    kindOf path json
    |> Result.bind (function
        | "approved" ->
            json
            |> shape strict path [ "kind"; "approver"; "at" ] (fun j ->
                (fun approver at -> ApprovedBy(approver, at))
                <!> Decode.member' Decode.text path "approver" j
                <*> Decode.member' Decode.instant path "at" j)
        | "not-required" -> json |> shape strict path [ "kind" ] (fun _ -> Ok ApprovalNotRequired)
        | other -> Decode.problem $"{path}.kind" $"'{other}' is not approved or not-required")

let private observationOf strict path json =
    json
    |> shape strict path [ "sourceSystem"; "observationId"; "externalUrl" ] (fun j ->
        (fun system id url ->
            { SourceSystem = system
              ObservationId = id
              ExternalUrl = url })
        <!> Decode.member' Decode.text path "sourceSystem" j
        <*> Decode.member' Decode.text path "observationId" j
        <*> Decode.member' optionalText path "externalUrl" j)

let private originOf strict path json =
    kindOf path json
    |> Result.bind (function
        | "unknown" -> json |> shape strict path [ "kind" ] (fun _ -> Ok Unknown)
        | "known" ->
            json
            |> shape strict path [ "kind"; "method"; "sourceSystem"; "observation"; "executionId" ] (fun j ->
                let method =
                    Decode.map2
                        (fun name system -> name, system)
                        (Decode.member' Decode.text path "method" j)
                        (Decode.member' optionalText path "sourceSystem" j)
                    |> Result.bind (function
                        | "manual", None -> Ok Manual
                        | "timer", None -> Ok Timer
                        | "imported", Some system -> Ok(Imported system)
                        | "imported", None -> Decode.problem $"{path}.sourceSystem" "is required for imported time"
                        | ("manual" | "timer"), Some _ -> Decode.problem $"{path}.sourceSystem" "is given only for imported time"
                        | other, _ -> Decode.problem $"{path}.method" $"'{other}' is not manual, timer or imported")

                (fun method observed execution -> Known(method, observed, execution))
                <!> method
                <*> Decode.member' (Decode.optional (observationOf strict)) path "observation" j
                <*> Decode.member' optionalText path "executionId" j)
        | other -> Decode.problem $"{path}.kind" $"'{other}' is not known or unknown")

let private billableTimeFields =
    [ "publicationId"
      "source"
      "performerId"
      "service"
      "classification"
      "exactMinutes"
      "billableMinutes"
      "policy"
      "billingReference"
      "approval"
      "origin"
      "lineage"
      "workItemReference"
      "supersedes"
      "publishedAt" ]

let private policyOf strict path json =
    json
    |> shape strict path [ "policyId"; "version" ] (fun j ->
        (fun id version -> { PolicyId = id; Version = version })
        <!> Decode.member' Decode.text path "policyId" j
        <*> Decode.member' Decode.integer path "version" j)

let private billingReferenceOf strict path json =
    json
    |> shape strict path [ "rateReference"; "billingClass"; "contractReference" ] (fun j ->
        (fun rate billingClass contract ->
            { RateReference = rate
              BillingClass = billingClass
              ContractReference = contract })
        <!> Decode.member' optionalText path "rateReference" j
        <*> Decode.member' optionalText path "billingClass" j
        <*> Decode.member' optionalText path "contractReference" j)

let private billableTimeOf strict path json =
    json
    |> shape strict path billableTimeFields (fun j ->
        (fun publication src performer svc cls exact billable policy billing approval origin lineage workItem supersedes publishedAt ->
            { PublicationId = publication
              Source = src
              PerformerId = performer
              Service = svc
              Classification = cls
              ExactMinutes = exact
              BillableMinutes = billable
              Policy = policy
              BillingReference = billing
              Approval = approval
              Origin = origin
              Lineage = lineage
              WorkItemReference = workItem
              Supersedes = supersedes
              PublishedAt = publishedAt })
        <!> Decode.member' Decode.text path "publicationId" j
        <*> Decode.member' (sourceOf strict) path "source" j
        <*> Decode.member' Decode.text path "performerId" j
        <*> Decode.member' (serviceOf strict) path "service" j
        <*> Decode.member' (classificationOf strict) path "classification" j
        <*> Decode.member' Decode.integer path "exactMinutes" j
        <*> Decode.member' Decode.integer path "billableMinutes" j
        <*> Decode.member' (policyOf strict) path "policy" j
        <*> Decode.member' (billingReferenceOf strict) path "billingReference" j
        <*> Decode.member' (approvalOf strict) path "approval" j
        <*> Decode.member' (originOf strict) path "origin" j
        <*> Decode.member' (Decode.list Decode.text) path "lineage" j
        <*> Decode.member' optionalText path "workItemReference" j
        <*> Decode.member' optionalText path "supersedes" j
        <*> Decode.member' Decode.instant path "publishedAt" j)

let private reasonOf strict path json =
    kindOf path json
    |> Result.bind (function
        | "voided" -> json |> shape strict path [ "kind" ] (fun _ -> Ok Voided)
        | "no-longer-billable" -> json |> shape strict path [ "kind" ] (fun _ -> Ok NoLongerBillable)
        | "replaced" ->
            json
            |> shape strict path [ "kind"; "by" ] (fun j -> Replaced <!> Decode.member' (Decode.list Decode.text) path "by" j)
        | other -> Decode.problem $"{path}.kind" $"'{other}' is not voided, replaced or no-longer-billable")

let private withdrawalOf strict path json =
    json
    |> shape strict path [ "publicationId"; "source"; "reason"; "withdrawnAt" ] (fun j ->
        (fun publication src reason at ->
            { PublicationId = publication
              Source = src
              Reason = reason
              WithdrawnAt = at })
        <!> Decode.member' Decode.text path "publicationId" j
        <*> Decode.member' (sourceOf strict) path "source" j
        <*> Decode.member' (reasonOf strict) path "reason" j
        <*> Decode.member' Decode.instant path "withdrawnAt" j)

let private feedbackOf strict path (last: string) (build: string -> string -> string -> int -> string -> DateTimeOffset -> 'a) json =
    json
    |> shape strict path [ "organizationId"; "publicationId"; "activityId"; "revision"; last; "at" ] (fun j ->
        build
        <!> Decode.member' Decode.text path "organizationId" j
        <*> Decode.member' Decode.text path "publicationId" j
        <*> Decode.member' Decode.text path "activityId" j
        <*> Decode.member' Decode.integer path "revision" j
        <*> Decode.member' Decode.text path last j
        <*> Decode.member' Decode.instant path "at" j)

let private versionPattern = Regex(@"^(\d+)\.(\d+)$", RegexOptions.CultureInvariant)

/// The envelope's kind and message, and whether members are checked
/// strictly (this minor or older) or leniently (a newer minor).
let private envelopeOf (text: string) : Decoded<string * Json * bool> =
    match Json.parse text with
    | Error message -> Error [ { Path = "$"; Message = message } ]
    | Ok json ->
        let contract =
            Decode.member' Decode.text "$" "contract" json
            |> Result.bind (fun name ->
                if name = Contract.Name then Ok() else Decode.problem "$.contract" $"'{name}' is not {Contract.Name}")

        let version =
            Decode.member' Decode.text "$" "version" json
            |> Result.bind (fun v ->
                let m = versionPattern.Match v

                match m.Success, (if m.Success then int m.Groups[1].Value else -1) with
                | true, major when major = Contract.Major -> Ok(int m.Groups[2].Value <= Contract.Minor)
                | true, major -> Decode.problem "$.version" $"major version {major} is not supported by this reader (major {Contract.Major})"
                | false, _ -> Decode.problem "$.version" "must be MAJOR.MINOR")

        let parts =
            Decode.map2 (fun () strict -> strict) contract version
            |> Result.bind (fun strict ->
                Decode.map2
                    (fun () (kind, message) -> kind, message, strict)
                    (Decode.closed strict "$" [ "contract"; "version"; "kind"; "message" ] json)
                    (Decode.map2
                        (fun kind message -> kind, message)
                        (Decode.member' Decode.text "$" "kind" json)
                        (Decode.field "$" "message" json)))

        parts

let private validated (rules: 'a -> Problem list) (decoded: Decoded<'a>) =
    decoded
    |> Result.bind (fun message ->
        match rules message with
        | [] -> Ok message
        | problems -> Error problems)

/// A message Chrona sent, or every problem with it.
let decodePublication (text: string) : Decoded<Publication> =
    envelopeOf text
    |> Result.bind (fun (kind, message, strict) ->
        match kind with
        | "billable-time-published" -> BillableTimePublished <!> billableTimeOf strict "$.message" message
        | "publication-withdrawn" -> PublicationWithdrawn <!> withdrawalOf strict "$.message" message
        | other -> Decode.problem "$.kind" $"'{other}' is not a message Chrona sends")
    |> validated Validate.publication

/// A message Summa sent, or every problem with it.
let decodeFeedback (text: string) : Decoded<Feedback> =
    envelopeOf text
    |> Result.bind (fun (kind, message, strict) ->
        match kind with
        | "invoiced" ->
            message
            |> feedbackOf strict "$.message" "invoiceReference" (fun organization publication activity revision invoice at ->
                InvoicedExternally
                    { OrganizationId = organization
                      PublicationId = publication
                      ActivityId = activity
                      Revision = revision
                      InvoiceReference = invoice
                      At = at })
        | "adjustment-required" ->
            message
            |> feedbackOf strict "$.message" "reason" (fun organization publication activity revision reason at ->
                AdjustmentNeeded
                    { OrganizationId = organization
                      PublicationId = publication
                      ActivityId = activity
                      Revision = revision
                      Reason = reason
                      At = at })
        | other -> Decode.problem "$.kind" $"'{other}' is not a message Summa sends")
    |> validated Validate.feedback
