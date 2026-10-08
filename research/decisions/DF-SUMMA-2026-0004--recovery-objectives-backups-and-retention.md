---
id: DF-SUMMA-2026-0004
title: Recovery objectives, sealed backups and retention
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/operations/recovery.md
  - research/decisions/DF-SUMMA-2026-0001--summa-owns-the-chrona-contract-and-stores-through-arca.md
tags: [operations, backup, recovery, encryption]
provenance:
  contributions:
    EXE-20261008T160041321Z-fa6acf65:
      operations: [created]
      at: 2026-10-08T16:07:53.351Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Choose recovery objectives, the backup format and retention"
---

# DF-SUMMA-2026-0004 — Recovery objectives, sealed backups and retention

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0022

v0.3 §8 asks for recovery objectives that are deliberate rather than
accidental, and v0.3 §6 asks for encrypted, retained, restorable backups.
The requirements leave the numbers, the format and the policy open. These
are the defaults; a deployment may make any of them stricter.

## Decisions

1. **Objectives.** The recovery point objective (RPO) is **24 hours** and the
   recovery time objective (RTO) is **4 hours**, the targets v0.3 §8
   suggests for an internal small-business system.
   `Summa.Operations.Recovery.objectives` states them, and
   `recoveryPointMet` reports when the newest verified backup is older than
   the RPO.
2. **What a backup is.** A backup is Arca's canonical export of one
   organization folder, taken as of a single change token. It includes every
   object: records, derived indexes and manifests, byte for byte, each with
   its SHA-256. It is sealed with AES-256-GCM. The key belongs to the
   operator's key store and is never created, stored or logged by Summa.
   This meets SUM3-024's "encrypted backups" without encrypting data at rest
   in the application, which DF-SUMMA-2026-0001 defers.
3. **A backup counts only after a verified restore.** `Recovery.verify`
   checks the restored folder in this order:
   - it opens through its manifests;
   - nothing blocking appears in the audit;
   - every record validates;
   - every invariant holds;
   - the reconciliation jobs agree;
   - the audit trail exists;
   - the reports can be produced.
4. **Retention.** Keep every backup from the last 14 days, plus the newest
   backup of each of the last 8 weeks and of each of the last 12 months.
5. **Where it runs.** Backups and restores run in `Summa.Operations`, outside
   the WASM application, on whatever machine the deployment schedules. That
   schedule waits on the deployment (WI-0036).

## Consequences

- The recovery drill is a test in CI: back up, destroy, restore, verify,
  report. It runs against Arca's in-memory provider. The first drill on a
  production-like deployment is part of WI-0036.
