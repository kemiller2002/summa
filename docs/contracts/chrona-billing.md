# Chrona-to-Summa billing contract

Package `EchelonFoundry.Summa.Contracts` (Echelon system `summa-contracts`).
Wire contract `summa.chrona-billing` 1.0. Owner: Summa (DF-SUMMA-2026-0001).
Shape: DF-SUMMA-2026-0003. Source: `src/Summa.Contracts`.

## Consuming it (Chrona)

1. Declare it in `conditor.json` once echelon-registry selects it:
   `{ "id": "summa-contracts", "version": "<version>", "required": true }`.
   Then run `conditor upgrade --current` against that echelon-current
   resolved set. Conditor proves the package against the Registry digest,
   writes `vendor/nuget/EchelonFoundry.Summa.Contracts.<version>.nupkg` and
   `vendor/nuget/summa-contracts.lock`, and maps the id to `echelon-vendor`
   in `NuGet.config`.
2. Pin it: `<PackageVersion Include="EchelonFoundry.Summa.Contracts" Version="<version>" />`.
   It needs FSharp.Core 10.1.401 or later, with the implicit FSharp.Core
   reference disabled, as Chrona already does.
3. Use it:

```fsharp
open Summa.Contracts.ChronaBilling.V1

// Chrona -> Summa
match Codec.tryEncodePublication (BillableTimePublished time) with
| Ok text -> (* send text *) ()
| Error problems -> (* each problem has a JSON Path and a Message *) ()

// Summa -> Chrona
match Codec.decodeFeedback text with
| Ok(InvoicedExternally invoiced) -> (* Review.recordInvoiced *) ()
| Ok(AdjustmentNeeded adjustment) -> (* AdjustmentRequired *) ()
| Error problems -> ()
```

4. Test against `Summa.Contracts.GoldenVectors`:
   - every `valid` vector decodes and re-encodes to the same bytes;
   - every `invalid` vector is refused with a problem at its `ExpectedPath`.

## Mapping from Chrona

| Contract | Chrona |
|---|---|
| `BillableTime.PublicationId`, `Source.Revision`, `BillableMinutes`, `Policy` | `Review.PublicationRecord` |
| `Source.OrganizationId`, `ActivityId`, `PerformerId`, `ExactMinutes` | `Activity.OrganizationId`, `ActivityId`, `ActorId`, `Minutes` |
| `Service` | `Activity.Occurrence` (`LocalDate`, `Zone`) and `Timing` (`Interval` or `DurationOnDate`) |
| `Classification` | `Activity.Classification` |
| `BillingReference` | `Activity.BillingReference` |
| `Approval` | the covering `Review.Approval` (`Approver`, `At`), or `ApprovalNotRequired` when `ReviewConfig.ApprovalRequired` is false |
| `Origin` | `Activity.EntryMethod` and `Activity.Source` (`ObservationSource`); `Unknown` when Chrona cannot say |
| `Lineage`, `WorkItemReference` | `Activity.Lineage`, `Activity.WorkItemRef` |
| `Supersedes` | the previous `PublicationRecord.PublicationId` when republishing after `AdjustmentRequired` |
| `PublicationWithdrawn` | an activity whose publication is `AdjustmentRequired` and that is now `Voided`, `Superseded` or `NonBillable` |
| `Invoiced` | `Review.InvoiceReport` (`PublicationId`, `ActivityId`, `Revision`, `InvoiceReference`, `At`) |
| `AdjustmentRequired` | `PublicationState.AdjustmentRequired` |

## Rules a reader enforces

- Envelope: `contract` = `summa.chrona-billing`, `version` = `1.x`, a known
  `kind`, and a `message`.
- Every required field is present; optional fields are `null`, never omitted.
- Ids, descriptions and references are not blank and never credential-shaped.
- Revisions and policy versions are 1 or more; minutes are not negative.
- Start and finish are given together or not at all, and finish is after
  start.
- Imported time names its source system. Lineage and replacement ids are
  distinct, and a publication never supersedes itself.
