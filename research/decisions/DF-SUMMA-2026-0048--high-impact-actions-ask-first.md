---
id: DF-SUMMA-2026-0048
title: High-impact actions ask first in a Forma dialog that says what will happen; routine ones do not
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
  - research/decisions/DF-SUMMA-2026-0046--forma-presents-tables-flyouts-and-dialogs.md
tags: [interface, confirmation, safety]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-13T22:11:02.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record confirmation behaviour (WI-0044, SUM4-038)"
---

# DF-SUMMA-2026-0048 — High-impact actions ask first

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §38 says that routine reversible actions should not keep asking for
confirmation, but high-impact ones should. Its candidates are issuing,
voiding, refunding, writing off, closing a period and reversing a payment.
"Confirmation should explain the consequence of the action."

## Decisions

1. **The actions that ask first are the high-impact ones the app offers.**
   Each opens a Forma dialog (DF-SUMMA-2026-0046) whose sentence comes
   from the books as they stand:

   | Action | What the dialog says |
   |---|---|
   | Issuing an invoice | to whom, for how much, that the number never changes, and that a correction is a credit memo |
   | Approving a proposal | it is issued as an invoice, with the same terms |
   | Issuing a credit memo | how much, against which invoice, that it reduces what is owed and is sent as its own document, and that it cannot be withdrawn |
   | Closing a month | nothing more can be posted until an accountant reopens it |
   | Locking a month | its numbers are final |
   | Reopening a month | reports already given for it may no longer match |
   | Replacing damaged books | covered by DF-SUMMA-2026-0046 |

   The dialog's confirming button is the only element that sends the
   action. Its other button changes nothing.
2. **Routine reversible actions do not ask.** These are saving or
   reviewing a draft, recording a reminder, dispute or promise, applying a
   payment, keeping credit, changing filters, and the like.
3. **Void, refund, write-off and payment reversal are not in the app.**
   They are domain and contract commands that an integration or agent
   sends explicitly (`summa.invoice-commands`). Nothing in the app sends
   them, so there is nothing for it to confirm. If the app later offers
   them, each must go through a dialog like the others.
4. **The rule is enforced by a test.** `ArchitectureTests` fails if
   `draftIssued`, `proposalApproved`, `creditMemoIssued`, `periodClosed`,
   `periodLocked`, `periodReopened` or `resetConfirmed` is sent from
   anywhere but inside an `ef-dialog`.

## Consequences

- SUM4-038 is tested:
  - `ConfirmationTests`: issue, credit memo, and the three month actions;
  - `InterfaceTests`: approve;
  - `ArchitectureTests`;
  - `editor.spec.js`: "Not yet" issues nothing, then confirming issues;
  - every browser flow that issues, credits, closes or reopens now
    confirms in the dialog.
- The buttons that open a dialog end in "…".
