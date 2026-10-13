---
id: DF-SUMMA-2026-0042
title: The routine run makes due recurring proposals, keeps verified sealed backups and prunes them
status: accepted
version: 1.1.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0004--recovery-objectives-backups-and-retention.md
  - research/decisions/DF-SUMMA-2026-0017--audit-provenance-and-agent-safety.md
  - research/decisions/DF-SUMMA-2026-0032--recurring-templates-make-proposals-once-per-occurrence.md
tags: [operations, backup, recurring, scheduling]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created, modified]
      at: 2026-10-13T16:45:27.000Z
      last: 2026-10-13T17:03:21.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the routine run (WI-0029 slice 21, SUM3-006); seal nonces drawn, never supplied (slice 22)"
---

# DF-SUMMA-2026-0042 — The routine run

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029

## Context

SUM3-006 (v0.3 §6) asks for routine backups. DF-SUMMA-2026-0004 decided what
a backup is: a sealed export of one organization folder. It also decided
that a backup counts only once its restore verifies, the retention policy
(14 days, 8 weeks, 12 months), and the objectives (RPO 24 hours, RTO 4
hours). It placed backups in `Summa.Operations`, outside the WASM
application, "on whatever machine the deployment schedules". Until now,
nothing produced a backup except a financial migration.

DF-SUMMA-2026-0032 made recurring templates propose once per occurrence. It
left open what runs `generateDue` on a schedule.

## Decisions

1. **One routine run per organization: `Summa.Operations.Routine.run`.** A
   run does three jobs in order:
   - It proposes every template's due occurrences.
   - It takes the sealed backup. This comes after proposing, so the backup
     includes the new proposals.
   - It prunes kept backups under the retention policy.

   Every job runs and reports, even when an earlier one failed. The report
   also says whether the recovery point is met.
2. **The run is a scheduled process, never a person.** It acts as a
   `ScheduledProcess` principal that a person admitted to the organization's
   roster. Its audit and proposal provenance say `automation`, and each run's
   id is its execution and correlation id.
   - It needs only `ProposeInvoice` (and `ViewFinancials`).
   - It can never be given `ManageUsers` or `ManageOrganization`.
   - It cannot issue. A process not on the roster proposes nothing, and the
     backup still runs.
3. **Proposals are isolated per template.** `Recurring.generateAllDue`
   proposes each template on its own, in id order. A template that cannot
   propose is reported with why and holds back no other. Running again on
   the same day, or after a restart, makes nothing new (INV-REC-004).
4. **The run uses the organization's date, not the server's.** "Today" is
   the date in the organization's time zone (`Routine.today`).
5. **A backup is kept only after it verifies.** The run seals the export
   and opens the sealed text with the key. It restores the result into an
   empty in-memory folder and runs `Recovery.verify` there. Only then is the
   backup handed to the deployment's backup location.
   - A backup that fails any of these steps, or cannot be stored, is not
     counted.
   - Its id is the organization and the UTC instant it was taken
     (`org_acme-20261220T060000Z`).
6. **Where backups are kept is the deployment's choice.** The location is a
   port, `BackupStore`, with `Put`, `List` and `Delete`. It is always
   outside the organization folder.
7. **The key comes from the operator.** Summa never creates, stores or logs
   the key (DF-SUMMA-2026-0004).
8. **Every seal draws its own nonce; no caller supplies one** (revised in
   1.1.0). AES-GCM with a nonce used twice under one key loses both
   confidentiality and integrity, and a scheduler or host can easily pass
   the same value twice. So:
   - `Backup.seal` and `Backup.take` take no nonce. Each draws 12 bytes from
     the operating system's secure generator (`Backup.freshNonce`, which
     uses `RandomNumberGenerator.Fill`) for every seal.
   - The routine run's inputs no longer include a nonce. A financial
     migration's backup no longer takes one either.
   - The nonce lives only inside the sealed text. Nothing logs or stores it
     anywhere else.
   - Only tests that need a fixed envelope inject a source, through
     `sealWith`, `takeWith` or `Routine.runWith`. Production code calls the
     functions without a source.

## Revision 1.1.0

Version 1.0.0 (PR #89) took the routine's nonce from the caller, and #89
is recorded as an escaped defect. The financial migration's backup had
taken a caller's nonce since WI-0022, and is fixed in the same change.
Nothing scheduled the routine, so no backup was ever sealed under a
caller's nonce outside tests. Decision 8 replaces the caller's nonce in
both places.

## Consequences

- SUM3-006 remains **partial**. No deployment schedules the run yet.
  - The run needs a host on a schedule. That host holds a storage credential
    for the organization's repository, the backup key and a backup location.
  - That waits on a real deployment (WI-0036, `blocked-on:deployment`).
- SUM3-024's "encrypted backups" now have a routine producer, but still no
  schedule.
- The app still has no screen for recurring templates.
- `RoutineTests` drives the run against Arca's in-memory provider:
  - proposals as automation;
  - a kept, sealed and verified backup;
  - no second proposal on a rerun;
  - a process not on the roster;
  - a failed store;
  - pruning a year of backups;
  - the organization's date;
  - two runs sealing under different nonces;
  - the production nonce source never being a constant, and a source that
    does not give 12 bytes sealing nothing.
- `RecurringTests` covers isolation per template.
