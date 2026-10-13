---
id: DF-SUMMA-2026-0021
title: Sensitive data refused at storage, import preview, integration states and retention
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0020--command-telemetry-exports-and-business-dates.md
tags: [operations, security, imports, integrations, retention]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T08:35:44.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record operational safety decisions (WI-0029 slice 3)"
---

# DF-SUMMA-2026-0021 — Sensitive data, import preview, integration states and retention

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

v0.3 §22–25 and §31–35 ask Summa to:
- avoid storing card details, full bank account numbers and secrets;
- keep secrets out of source, configuration, records and logs;
- preview an import before it changes anything;
- keep internal state sound when an integration is down;
- never apply the same external event twice;
- delete only what has no financial effect, and say what is kept.

## Decisions

1. **Sensitive values are refused at storage, for every record type.**
   `Commands.changes` scans each record it would write (`Sensitive.scan`).
   A full card number (13–19 digits standing alone, passing Luhn), a full
   IBAN (mod-97) or a credential (GitHub, AWS, Slack, bearer, private key)
   refuses the command with `SUMMA.STORAGE.SENSITIVE_DATA`, and nothing is
   written. The rule is storage-wide, with no exemption by record type,
   `summa.delivery` included. A reference such as "card ending 1111" is
   kept. The digits of an IBAN-shaped value are judged by the IBAN rule
   only: a mistyped IBAN's account digits can pass Luhn by chance, and are
   not a card number.
2. **No credential in source or committed configuration.** A test scans
   `src`, `app`, `web-kernel`, `deploy`, `.github` and the root JSON
   configuration with the same scanner.
3. **Imports are previewed.** `ChronaImport.preview` runs a batch on the
   books as they stand and returns accepted, rejected, duplicate and warning
   lists, with the books as they would be. Nothing is applied until the
   caller commits the result.
4. **One integration-state vocabulary.** `Integrations.IntegrationState`
   has Pending, Succeeded, Retryable and PermanentFailure. The GitHub queue
   (`ofQueued`) and invoice delivery (`ofDelivery`) map into it. A
   conflict or an unknown outcome is retryable, after it has been reconciled.
   A refusal or a bounce needs a person.
5. **Retention by record type** (`Retention.ofType`). Everything financial
   is permanent. Only drafts are kept until resolved, and Git keeps their
   history. A test ties retention to the record types that may be deleted.
6. **Import and migration events.** `Telemetry.importEvent` and
   `Telemetry.stepEvent` follow DF-SUMMA-2026-0020. A refused import
   message is a technical (integration) failure, and refusal reasons are
   not logged.

## Consequences

- SUM3-022, 023, 033, 034 and 035 are tested.
- SUM3-024, 025, 031 and 032 are partial. Still to do: export file
  handling, a served preview-and-accept flow, the attention item for
  delivery retries, and integration state in the application.
