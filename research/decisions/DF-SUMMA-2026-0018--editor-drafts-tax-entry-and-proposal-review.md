---
id: DF-SUMMA-2026-0018
title: Editor drafts kept in the tab, tax entered on the screens, and proposals reviewed by a person
status: accepted
version: 1.0.0
created: 2026-10-09
updated: 2026-10-09
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0015--room-for-tax-without-calculating-it.md
  - research/decisions/DF-SUMMA-2026-0017--audit-provenance-and-agent-safety.md
  - research/decisions/DF-SUMMA-2026-0013--sign-in-through-fides-in-the-application.md
tags: [ui, drafts, tax, proposals, agents]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-09T03:02:32.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the rest of the v0.4 interface (WI-0044)"
---

# DF-SUMMA-2026-0018 — Editor drafts, tax entry and proposal review

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0044

## Context

Saved drafts already survived a refresh: in browser storage locally, and
through Arca's offline queue on GitHub (DF-SUMMA-2026-0014). What the person
had typed but not saved did not (v0.4 §39, INV-DRAFT-003). Tax could be
recorded on drafts and customers (DF-SUMMA-2026-0015), but not from the
screens. Proposals, an agent's among them, could be made and explained
(DF-SUMMA-2026-0017), but no screen showed them, so no person could
inspect and approve one (v0.4 §7, §42, §43).

## Decisions

1. **The editor's unsaved form is kept in the tab, not in the books.** As
   the person types, the engine asks the host pack to keep the form under
   `summa.editor` in tab storage. It is not a record: it never reaches
   GitHub, needs no capability, and is gone when the tab is. On start the
   engine reads it back and restores it to its own draft (still in the
   books) or to a new invoice (for the same customer, when the link names
   one), with a notice. Saving or issuing the draft, or "Discard unsaved
   changes", forgets it. A kept form that cannot be read is dropped, never
   guessed at. There is one slot: the form last typed in. Tab storage
   rather than local storage, so two tabs never overwrite each other's
   unsaved work, and nothing typed outlives the session it was typed in.
2. **Checkboxes inside a form send their state, not a toggle.** Limen
   fires a form's checked checkboxes again when the form is submitted, so a
   toggle would flip on every save. The wire reads the event's `checked`
   for those events (`lineTaxableChanged`, `draftTaxInclusiveChanged`).
3. **Tax is entered, never worked out.** The editor marks lines taxable
   and takes one tax: its code, amount, optional rate (as a percentage,
   kept in hundredths of a basis point), liability account (the books'
   active liability accounts, 2300 by default), jurisdiction, rate source,
   evidence, and whether it is inside the prices. Summa refuses a tax with
   no code or amount, an unreadable rate, or an account the books do not
   have; the domain's own rules (a taxable line, no tax for an exempt
   customer, inclusive tax no more than the taxable lines) are blockers as
   before. Other adjustments on a saved draft are kept when the editor saves
   it. The customer form records tax status (not recorded, subject to tax,
   exempt), the jurisdiction and, for an exemption, its evidence.
4. **Proposals are reviewed by a person at their own places.**
   `#/proposals` lists them with who prepared them; `#/proposals/{id}`
   shows the lines, `Billing.explain` and every contribution. While a
   proposal is open, a person may change a line's rate with a reason
   (`OverrideRate`), approve it (`IssueInvoice`: `markReady` and `accept` in
   one change, so it is checked again and issued as one invoice), or
   abandon it (`ProposeInvoice`). The application has no agent of its own,
   so every approval is a person's, appended to the agent's contribution.

## Consequences

- SUM4-007, SUM4-039, SUM4-042, SUM4-043 and INV-DRAFT are tested; tax is
  in the screens (INV-ADJ). The other v0.4 interface rows stay on WI-0044.
- An unsaved form for another draft is replaced by the one typed last.
