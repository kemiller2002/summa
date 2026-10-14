---
id: DF-SUMMA-2026-0053
title: Management report views are reusable links; engagement contribution counts only the costs Summa holds
status: accepted
version: 1.0.0
created: 2026-10-14
updated: 2026-10-14
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents: []
tags: [reports, management, profitability]
provenance:
  contributions:
    EXE-20261009T024702576Z-bcf1e384:
      operations: [created]
      at: 2026-10-14T01:00:24.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record management report views and engagement contribution (WI-0044, SUM4-029, SUM4-030)"
---

# DF-SUMMA-2026-0053 — Management report views and engagement contribution

- **Date:** 2026-10-14
- **Status:** accepted
- **Work item:** WI-0044

## Context

v0.4 §29 asks for reusable report views: revenue by client, revenue by
project, expenses by project, profitability by engagement, and
outstanding AR by customer. "A full business intelligence platform is
not required." §30 asks, "once Summa connects to Time Entry and ROS",
for engagement contribution: revenue less human labor cost, AI cost,
infrastructure and external expenses.

## Decisions

1. **One domain module, from the posted journal.** `Management`
   provides:
   - `revenueBy` and `expensesBy`, by client, project or engagement;
   - `contribution`;
   - `outstandingByCustomer`.

   Revenue is credits less debits on revenue accounts, and expenses are
   debits less credits on expense accounts, in the period and currency.
   Year-end closing entries are left out, as in the income statement.
   Amounts with no client, project or engagement are kept as
   "Unassigned" and listed last, never dropped.
2. **The engagement of invoice revenue is the invoice's.** Invoice
   revenue lines are posted by client and project. The engagement comes
   from the invoice the entry was posted for, so earlier entries count
   too, and posting does not change.
3. **Saved views are links.**
   - `#/reports/management?view=…&from=…&to=…` shows one view. The five
     views are listed on the Reports page under "Saved views", and as
     tabs on the report.
   - A view and its period are its address, so a person keeps a view by
     copying or bookmarking its link. This is the same mechanism as every
     other view (WI-0041).
   - Summa does not store named views per person: §29 says a BI platform
     is not required.
   - Outstanding by customer is as of the "to" date.
4. **Engagement contribution counts only the costs Summa holds.**
   - Revenue and external expenses (expenses posted with the engagement)
     come from the books.
   - Human labor, AI and infrastructure costs have no source in Summa:
     Chrona publishes billable time, not cost, and no ROS cost feed is
     connected. They are `None` in the domain and "Not available" on
     the report, never zero, and the report says so.
   - Contribution is revenue less external expenses.
   - **Out of scope until a cost source exists:** labor, AI and
     infrastructure cost. When one does, it fills these fields and the
     contribution subtracts them. SUM4-030 is therefore partial, not
     tested.

## Consequences

- SUM4-029 is tested:
  - `ManagementTests`: the three breakdowns, unassigned last, the
    period, and AR as of a date;
  - `ManagementReportTests`: the saved-view links, the period in the
    address, and AR as of a date;
  - `books.spec.js`.
- SUM4-030 is partial. `ManagementTests` and `ManagementReportTests`
  test contribution from revenue and external expenses, with the other
  costs not available.
