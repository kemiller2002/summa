# Chrona-to-Summa billing contract

Package `EchelonFoundry.Summa.Contracts` (Echelon system `summa-contracts`).
Wire contract `summa.chrona-billing` 1.0. Owner: Summa (DF-SUMMA-2026-0001).
Shape: DF-SUMMA-2026-0003. Source: `src/Summa.Contracts`.

## Released

| Version | Tag | Commit | Registry |
|---|---|---|---|
| 0.1.0 | [`contracts-v0.1.0`](https://github.com/kemiller2002/summa/releases/tag/contracts-v0.1.0) | `e888cfb` | echelon-registry `4aee68a` (#50), echelon-current **1.8.0**, optional project binding `summa-contracts` `>=0.1.0 <1.0.0` |

Package `EchelonFoundry.Summa.Contracts.0.1.0.nupkg`, SHA-256
`8ef0e595f9749edac7fc3c46c1d400ba1a4d44f10090c3ed27291855845d15f1`, with a
SLSA v1 build-provenance attestation from `.github/workflows/contracts-release.yml`.
echelon-current 1.8.0 resolved sets at registry `4aee68a`:

| Platform | SHA-256 |
|---|---|
| linux-x64 | `50623c2ba4ec58324fe261d64ebc3eb3cc5c87b1a5d781810f47c9d50b1ddcdd` |
| linux-arm64 | `b2992fdface9dad9c635570a763ca71a47ff6b3aa976a5c86e5783eab5533cb9` |
| osx-x64 | `7261646e78ec15328e18f833b6b4e2575c32898df5e2d0d848255addb9485971` |
| osx-arm64 | `f537bfd274b96fa4b6ca98c053e8a990263d0fcb2c4c5222feeeecf2d9c9ba45` |
| win-x64 | `5359a0ffe0259a31bc92ba2455b2eb97ed824fa419fbd39dff0ad3aea7517374` |

## Consuming it (Chrona)

1. Declare it in `conditor.json`:
   `{ "id": "summa-contracts", "version": "0.1.0", "required": true }`.
   Then plan and apply `conditor upgrade --current --resolved-set
   <echelon-registry>/channels/echelon-current/linux-x64.json
   --resolved-set-sha256 50623c2b...` (`--check`, then `--authorize` with
   the plan digest). The plan lists `summa-contracts: opt in at 0.1.0`. Conditor proves the package against the Registry digest,
   writes `vendor/nuget/EchelonFoundry.Summa.Contracts.<version>.nupkg` and
   `vendor/nuget/summa-contracts.lock`, and maps the id to `echelon-vendor`
   in `NuGet.config`.
2. Pin it: `<PackageVersion Include="EchelonFoundry.Summa.Contracts" Version="0.1.0" />`.
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
