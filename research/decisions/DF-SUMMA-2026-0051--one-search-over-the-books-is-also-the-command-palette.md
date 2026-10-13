---
id: DF-SUMMA-2026-0051
title: One search over the books, opened from the header, is also the command palette
status: accepted
version: 1.0.0
created: 2026-10-13
updated: 2026-10-13
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents: []
tags: [interface, search, navigation]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-13T23:40:46.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record global search and the command palette (WI-0044, SUM4-018, SUM4-019)"
---

# DF-SUMMA-2026-0051 — One search is also the command palette

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §18 asks for a global search that finds customers, invoice numbers,
payment references, amounts, projects, journal entries and memos, "fast
enough to serve as a primary navigation method". §19 suggests a command
palette for high-frequency operations: create invoice, record payment,
open ABC Corp, find INV-2026-0038, show overdue invoices, run trial
balance.

## Decisions

1. **One domain search.** `Search.find receivables query` returns the
   matching records (at most `Search.limit`, 50) and how many matched in
   all. Each hit names its kind, title, a line about it, and the field
   that matched with its text ("memo: Wire for the October retainer").
   It searches:

   | Kind | Fields |
   |---|---|
   | Customers | name, billing name, email, id |
   | Invoices | number, customer, every reference `Lifecycle.searchTexts` holds (PO, client references, projects, sources, recipients), customer and internal notes |
   | Payments | reference, id, customer, memo |
   | Journal entries | id, description, line memos |
   | Projects | every project named on time, invoice lines, expenses or journal lines |

2. **Amounts are matched as amounts.** A query that reads as an amount
   ("1500", "1,500.00") finds invoice totals and outstanding amounts,
   payment amounts and journal line amounts equal to it. It does not find
   numbers that merely contain those digits.
3. **The order.** The closest match comes first: the whole field, then
   its start, then inside it. Then the kinds in the order customers,
   invoices, payments, journal entries, projects.
4. **Fast enough to navigate by.** The search reads the books already in
   memory; nothing is sent anywhere. A test searches 5,000 invoices and
   requires an answer within a second. In practice it is a few
   milliseconds.
5. **The palette is the same page.** A search field in the header opens
   `#/search?q=…` on the first key, then refines it in place, so the
   field keeps focus. The page lists matching commands first, then the
   records. Every command and every record is a link, so Tab, Enter, Back
   and copied links work as they do everywhere else.
   - The commands are: Create invoice, Record payment, Show overdue
     invoices, Show receivables, Run trial balance, Open the work queue.
     A command matches when its label or words contain every word typed.
   - A leading "open", "find", "show" or "go to" is not searched for,
     so "Open ABC Corp" and "Find INV-2026-0038" find the record.
6. **No global keyboard shortcut yet.** Limen events are bound to
   elements, and a document-level shortcut such as Ctrl+K is not part of
   its event model. The palette is reached by the header field, which is
   in the tab order on every page. This narrows nothing the requirement
   states: §19 asks for a palette, not a shortcut.

## Consequences

- SUM4-018 and SUM4-019 are tested:
  - `SearchTests`: every kind, memos, amounts both ways and not
    by digits, the closeness order, the empty query, and 5,000 invoices
    within a second with the count beyond the limit;
  - `SearchPageTests`: the header opens and then refines the page,
    results link and say what matched, "open …" finds the customer, the
    commands narrow as typed;
  - `deep-links.spec.js`: typing key by key keeps focus, an amount
    finds an invoice, Back returns to the search, and a command opens
    the trial balance;
  - `BindingAgreementTests` and `RoutesTests` include the page.
