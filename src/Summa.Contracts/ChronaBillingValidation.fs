/// The domain rules every version-1 message obeys, on typed values. The
/// codec applies them when decoding, so nothing Summa or Chrona accepts at
/// the boundary breaks them; a producer calls `Validate.publication` or
/// `Validate.feedback` (or `Codec.tryEncode...`) before sending.
///
/// Problem paths are the JSON paths of the encoded message.
module Summa.Contracts.ChronaBilling.V1.Validate

open System
open Summa.Contracts

let private at path message = [ { Path = path; Message = message } ]

let private blank = String.IsNullOrWhiteSpace

/// Text that looks like a credential: access tokens, private keys and
/// authorization headers never travel in this contract (INV-PROV-008,
/// SUM0-004).
let looksLikeCredential (value: string) =
    let trimmed = value.TrimStart()

    [ "ghp_"; "gho_"; "ghu_"; "ghs_"; "ghr_"; "github_pat_"; "glpat-"; "xoxb-"; "xoxp-"; "AKIA" ]
    |> List.exists (fun prefix -> trimmed.StartsWith(prefix, StringComparison.Ordinal))
    || value.Contains("-----BEGIN", StringComparison.Ordinal)
    || (trimmed.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)
        && not (trimmed.Substring(7).Trim() |> Seq.exists Char.IsWhiteSpace))
    || value.Contains("authorization:", StringComparison.OrdinalIgnoreCase)

/// Any text: never a credential.
let private safe path (value: string) =
    if looksLikeCredential value then at path "looks like a credential; credentials never travel in this contract" else []

/// Required text: present, not blank, not a credential.
let private required path (value: string) =
    if blank value then at path "is required and must not be blank" else safe path value

let private optionalText path (value: string option) =
    value |> Option.map (required path) |> Option.defaultValue []

let private positive path (value: int) =
    if value < 1 then at path "must be 1 or more" else []

let private notNegative path (value: int) =
    if value < 0 then at path "must not be negative" else []

let private distinctIds path (ids: string list) =
    let each = ids |> List.mapi (fun i id -> required $"{path}[{i}]" id) |> List.concat

    if List.length (List.distinct ids) <> List.length ids then
        each @ at path "must not name the same activity twice"
    else
        each

let private webAddress path (value: string) =
    match Uri.TryCreate(value, UriKind.Absolute) with
    | true, NonNull uri when uri.Scheme = Uri.UriSchemeHttps || uri.Scheme = Uri.UriSchemeHttp -> safe path value
    | _ -> at path "must be an absolute http or https address"

let source path (s: SourceReference) =
    required $"{path}.organizationId" s.OrganizationId
    @ required $"{path}.activityId" s.ActivityId
    @ positive $"{path}.revision" s.Revision

let private service path (s: ServicePeriod) =
    required $"{path}.zone" s.Zone
    @ match s.Interval with
      | Some(start, finish) when finish <= start -> at $"{path}.finish" "must be after start"
      | _ -> []

let private classification path (c: Classification) =
    required $"{path}.projectId" c.ProjectId
    @ optionalText $"{path}.clientId" c.ClientId
    @ optionalText $"{path}.engagementId" c.EngagementId
    @ required $"{path}.activityTypeId" c.ActivityTypeId
    @ required $"{path}.description" c.Description
    @ required $"{path}.businessPurpose" c.BusinessPurpose
    @ (c.Tags |> List.mapi (fun i tag -> required $"{path}.tags[{i}]" tag) |> List.concat)

let private origin path (o: Origin) =
    match o with
    | Unknown -> []
    | Known(method, observation, executionId) ->
        (match method with
         | Imported system -> required $"{path}.sourceSystem" system
         | Manual
         | Timer -> [])
        @ (match observation with
           | None -> []
           | Some obs ->
               required $"{path}.observation.sourceSystem" obs.SourceSystem
               @ required $"{path}.observation.observationId" obs.ObservationId
               @ (obs.ExternalUrl |> Option.map (webAddress $"{path}.observation.externalUrl") |> Option.defaultValue []))
        @ optionalText $"{path}.executionId" executionId

/// Every problem with billable time.
let billableTime path (t: BillableTime) =
    required $"{path}.publicationId" t.PublicationId
    @ source $"{path}.source" t.Source
    @ required $"{path}.performerId" t.PerformerId
    @ service $"{path}.service" t.Service
    @ classification $"{path}.classification" t.Classification
    @ notNegative $"{path}.exactMinutes" t.ExactMinutes
    @ notNegative $"{path}.billableMinutes" t.BillableMinutes
    @ required $"{path}.policy.policyId" t.Policy.PolicyId
    @ positive $"{path}.policy.version" t.Policy.Version
    @ optionalText $"{path}.billingReference.rateReference" t.BillingReference.RateReference
    @ optionalText $"{path}.billingReference.billingClass" t.BillingReference.BillingClass
    @ optionalText $"{path}.billingReference.contractReference" t.BillingReference.ContractReference
    @ (match t.Approval with
       | ApprovedBy(approver, _) -> required $"{path}.approval.approver" approver
       | ApprovalNotRequired -> [])
    @ origin $"{path}.origin" t.Origin
    @ distinctIds $"{path}.lineage" t.Lineage
    @ optionalText $"{path}.workItemReference" t.WorkItemReference
    @ (match t.Supersedes with
       | Some earlier when earlier = t.PublicationId -> at $"{path}.supersedes" "must name an earlier publication, not this one"
       | other -> optionalText $"{path}.supersedes" other)

/// Every problem with a withdrawal.
let withdrawal path (w: Withdrawal) =
    required $"{path}.publicationId" w.PublicationId
    @ source $"{path}.source" w.Source
    @ match w.Reason with
      | Replaced [] -> at $"{path}.reason.by" "must name at least one replacing activity"
      | Replaced by -> distinctIds $"{path}.reason.by" by
      | Voided
      | NoLongerBillable -> []

/// Every problem with a message Chrona sends. Empty when it is valid.
let publication (message: Publication) : Problem list =
    match message with
    | BillableTimePublished t -> billableTime "$.message" t
    | PublicationWithdrawn w -> withdrawal "$.message" w

/// Every problem with a message Summa sends. Empty when it is valid.
let feedback (message: Feedback) : Problem list =
    let common organization publication activity revision =
        required "$.message.organizationId" organization
        @ required "$.message.publicationId" publication
        @ required "$.message.activityId" activity
        @ positive "$.message.revision" revision

    match message with
    | InvoicedExternally i ->
        common i.OrganizationId i.PublicationId i.ActivityId i.Revision
        @ required "$.message.invoiceReference" i.InvoiceReference
    | AdjustmentNeeded a ->
        common a.OrganizationId a.PublicationId a.ActivityId a.Revision
        @ required "$.message.reason" a.Reason
