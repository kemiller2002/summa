# Requirement implementation gap analysis

Work item: WI-0011 (gap analysis); implementation items WI-0012 through WI-0014  
Baseline: `main` at `0e825a6` (2026-10-07)  
Authoritative corpus: `input-documents/summa-*.txt`

## Purpose and method

The Summa corpus mixes numbered sections (set 0, v0.1 to v0.4) with
requirement IDs (the invoice-generation document's `INV-<FAMILY>-NNN`). This
record gives every numbered section a stable identifier and assesses the
invoice-generation requirements by family:

| Identifier | Source |
|---|---|
| `SUM0-NNN` | `summa-requirement-set-0.txt` §0.NNN |
| `SUM1-NNN` to `SUM4-NNN` | `summa-v0.1` to `summa-v0.4-requirements.txt` §NNN (subsections included) |
| `INV-<FAMILY>` | every `INV-<FAMILY>-NNN` in `summa-invoice-generation-requirements.txt` |

Each row is `tested` (every normative statement implemented and tested),
`partial` (some, with a note saying which), or `missing`. Domain rules that
are pure functions but not yet stored, served or shown are at most `partial`
when the section also requires storage, UI or integration.

The **Baseline** column is `main` at `0e825a6`; the **Current** column is
updated by each change that closes or narrows a gap.

### What existed at baseline

The F# web tiers (`Summa.Web.Engine`, `Summa.Web.Application`) implement the
project-administration hub and backlog pages behind Limen with an Aegis
boundary, plus their tests. No ledger, invoice, receivable, payment or report
code exists; every financial requirement is missing.

## Highest-value gaps and the order they are closed

The v0.1 "First Vertical Slice" (create client, issue an invoice that posts
AR/revenue, record a payment that posts cash/AR, invoice shows paid, trial
balance balances, P&L and balance sheet reflect it) is the highest-value
outcome, and the ledger is its source of truth:

1. **WI-0012** `Summa.Ledger`: fixed-decimal money, the chart of accounts,
   balanced journal entries, posting, reversal and accounting periods.
2. **WI-0013** Customers, invoice numbering, issuing with the AR/revenue
   entry as one idempotent operation, receivable obligations and terms.
3. **WI-0014** Payments, allocation with its posting, derived invoice
   status, AR aging and the four core reports.

GitHub storage (set 0), v0.2 edge cases, v0.3 operational safety, the v0.4
UI and the invoice document requirements remain open (`later`).

## Summary

| Corpus | Rows | Baseline tested | Baseline partial | Baseline missing |
|---|---:|---:|---:|---:|
| Summa requirements | 223 | 0 | 1 | 222 |

## Rows

| Requirement | Baseline | Current | Evidence or gap | Work items |
|---|---|---|---|---|
| SUM0-001 | partial | partial | **Set 0 §0.1 Mandatory Engineering Process.** Work runs under Praxis/Ordo; the existing F# tiers sit behind Limen with Aegis (`FoundationsConformanceTests`). No financial code exists to hold to it. | later |
| SUM0-002 | missing | missing | **Set 0 §0.2 GitHub Is the Authoritative Data Store.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-003 | missing | missing | **Set 0 §0.3 GitHub Authentication.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-004 | missing | missing | **Set 0 §0.4 Token Handling.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-005 | missing | missing | **Set 0 §0.5 Multi-Application Repository Requirement.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-006 | missing | missing | **Set 0 §0.6 Application Boundary.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-007 | missing | missing | **Set 0 §0.7 Application Manifest.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-008 | missing | missing | **Set 0 §0.8 Organization Isolation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-009 | missing | missing | **Set 0 §0.9 Organization Storage Location.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-010 | missing | missing | **Set 0 §0.10 Organization Identity.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-011 | missing | missing | **Set 0 §0.11 Organization Manifest.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-012 | missing | missing | **Set 0 §0.12 One Financial Object or Event Per File.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-013 | missing | missing | **Set 0 §0.13 Authoritative Data Format.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-014 | missing | missing | **Set 0 §0.14 Authoritative Versus Derived Data.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-015 | missing | missing | **Set 0 §0.15 Rebuildable Runtime Index.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-016 | missing | missing | **Set 0 §0.16 Financial Command to Git Commit Boundary.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-017 | missing | missing | **Set 0 §0.17 Git Commit Is Not a Substitute for Domain Atomicity.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-018 | missing | missing | **Set 0 §0.18 Normal Business Transactions and Branching.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-019 | missing | missing | **Set 0 §0.19 Structural Changes and Pull Requests.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-020 | missing | missing | **Set 0 §0.20 Optimistic Concurrency Through Git State.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-021 | missing | missing | **Set 0 §0.21 No Silent Rebase of Financial Meaning.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-022 | missing | missing | **Set 0 §0.22 Summa Authorization.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-023 | missing | missing | **Set 0 §0.23 Human, Agent, and Service Identity.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-024 | missing | missing | **Set 0 §0.24 Cross-Application Ownership.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-025 | missing | missing | **Set 0 §0.25 Cross-Application References.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-026 | missing | missing | **Set 0 §0.26 Shared Concepts Must Not Imply Shared Storage Ownership.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-027 | missing | missing | **Set 0 §0.27 Draft Versus Posted Mutation Rules.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-028 | missing | missing | **Set 0 §0.28 Deletion Policy.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-029 | missing | missing | **Set 0 §0.29 Repository Manual Modification.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-030 | missing | missing | **Set 0 §0.30 Repository Tamper Detection.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-031 | missing | missing | **Set 0 §0.31 Schema Versioning.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-032 | missing | missing | **Set 0 §0.32 Storage Migration Safety.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-033 | missing | missing | **Set 0 §0.33 Repository and Organization Discovery.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-034 | missing | missing | **Set 0 §0.34 Repository Scale Assumption.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-035 | missing | missing | **Set 0 §0.35 Artifact Storage.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-036 | missing | missing | **Set 0 §0.36 Artifact References.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-037 | missing | missing | **Set 0 §0.37 Generated Invoice Representation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-038 | missing | missing | **Set 0 §0.38 Environment Isolation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-039 | missing | missing | **Set 0 §0.39 Staging.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-040 | missing | missing | **Set 0 §0.40 Environment Visibility.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-041 | missing | missing | **Set 0 §0.41 Configuration Versus Code.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-042 | missing | missing | **Set 0 §0.42 Application Configuration Must Be Scoped.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-043 | missing | missing | **Set 0 §0.43 Repository Safety.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-044 | missing | missing | **Set 0 §0.44 Deterministic Storage Paths.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-045 | missing | missing | **Set 0 §0.45 Stable Record Identity.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-046 | missing | missing | **Set 0 §0.46 Git History Is Supporting Evidence, Not the Domain Model.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-047 | missing | missing | **Set 0 §0.47 Integrity Before Availability.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-048 | missing | missing | **Set 0 §0.48 Agent Repository Behavior.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-049 | missing | missing | **Set 0 §0.49 Agent Context and File Scope.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM0-050 | missing | missing | **Set 0 §0.50 Requirement Precedence.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM1-001 | missing | tested | **v0.1 §1 General Ledger.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0012:** chart of accounts with five types and normal balances, unique codes; lines are a debit or a credit by type, positive fixed-decimal amounts; posting requires two or more lines, balance and active accounts; posted entries are immutable with unique ids and corrected only by reversal (`Summa.Ledger.Ledger`, `LedgerTests`). | WI-0012 |
| SUM1-002 | missing | tested | **v0.1 §2 Accounting Periods.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0012:** monthly periods Open/Closed/Locked; posting only into open periods; closing and locking leave entries untouched; reopening needs an explicit privileged call and is audited. | WI-0012 |
| SUM1-003 | missing | tested | **v0.1 §3 Customers.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** customer records with billing name, address, email, default terms and active flag; inactive customers cannot be invoiced (`Invoicing`, `InvoicingTests`). | WI-0013 |
| SUM1-004 | missing | partial | **v0.1 §4 Invoices.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** drafts and issued invoices with number, customer, dates, currency, lines (quantity, unit price, amount, revenue account, project, work item), subtotal, adjustments and total. Paid/outstanding amounts and derived status arrive with payments (WI-0014). | WI-0013 |
| SUM1-005 | missing | tested | **v0.1 §5 Invoice Numbering.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** unique sequential `EF-YYYY-NNNN` numbers per year, unique manual overrides, drafts carry internal ids, and issued invoices cannot be edited. | WI-0013 |
| SUM1-006 | missing | tested | **v0.1 §6 Issuing an Invoice.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** issuing checks lines, active customer, positive total and revenue accounts, derives the due date, and posts the AR/revenue entry with the invoice and obligation as one all-or-nothing operation; the invariant is checkable (`issuedInvoicesHaveEntries`). | WI-0013 |
| SUM1-007 | missing | missing | **v0.1 §7 Invoice Output.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM1-008 | missing | tested | **v0.1 §8 Sending Invoices.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** issued, sent-at and sent-to are recorded; sending stays manual. | later |
| SUM1-009 | missing | tested | **v0.1 §9 Payment Terms and Due Dates.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** due on receipt, Net N and custom dates; days until due and past due are calculated, never stored. | WI-0013 |
| SUM1-010 | missing | partial | **v0.1 §10 Receivables / Obligations.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0013:** issuing creates a receivable obligation (id, source, party, original amount, due date) with a kind that admits payables. Settlement states derive from allocations in WI-0014. | WI-0013 |
| SUM1-011 | missing | missing | **v0.1 §11 Payments.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | WI-0014 |
| SUM1-012 | missing | missing | **v0.1 §12 Payment Allocation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | WI-0014 |
| SUM1-013 | missing | missing | **v0.1 §13 Accounting for Payments.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | WI-0014 |
| SUM1-014 | missing | missing | **v0.1 §14 Accounts Receivable Aging.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | WI-0014 |
| SUM1-015 | missing | missing | **v0.1 §15 Core Financial Reports.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | WI-0014 |
| SUM1-016 | missing | tested | **v0.1 §16 Dimensions.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0012:** client, project, engagement, work-item and product dimensions on journal lines, not as accounts. | later |
| SUM1-017 | missing | missing | **v0.1 §17 Time Entry Integration Boundary.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM1-018 | missing | partial | **v0.1 §18 Audit Trail.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0012:** ledger operations (account saved, entry posted and reversed, period closed/locked/reopened) append who/what/when/source/correlation records that the API cannot edit. Invoice and payment actions follow. **WI-0013:** customer saved, invoice created/changed/issued/sent are audited as well. | later |
| SUM1-019 | missing | partial | **v0.1 §19 Idempotency.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0012:** `PostJournalEntry` is idempotent by key (five retries post once; a reused key with a different entry is refused). IssueInvoice and RecordPayment follow. **WI-0012, WI-0013:** PostJournalEntry and IssueInvoice are idempotent (five retries leave one invoice, obligation and entry). RecordPayment follows. | WI-0013 |
| SUM1-020 | missing | missing | **v0.1 §20 Data Export and Escape Hatch.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-001 | missing | missing | **v0.2 §1 Partial Payments.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-002 | missing | missing | **v0.2 §2 Multiple Payments Against One Invoice.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-003 | missing | missing | **v0.2 §3 One Payment Applied to Multiple Invoices.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-004 | missing | missing | **v0.2 §4 Overpayments.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-005 | missing | missing | **v0.2 §5 Customer Deposits and Retainers.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-006 | missing | missing | **v0.2 §6 Invoice Corrections Before Issue.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-007 | missing | missing | **v0.2 §7 Invoice Corrections After Issue.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-008 | missing | missing | **v0.2 §8 Voiding Invoices.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-009 | missing | missing | **v0.2 §9 Credit Memos.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-010 | missing | missing | **v0.2 §10 Refunds.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-011 | missing | missing | **v0.2 §11 Bounced or Reversed Payments.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-012 | missing | missing | **v0.2 §12 Write-Offs.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-013 | missing | missing | **v0.2 §13 Discounts.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-014 | missing | missing | **v0.2 §14 Reimbursable Client Expenses.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-015 | missing | missing | **v0.2 §15 Hourly Billing.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-016 | missing | missing | **v0.2 §16 Fixed-Price Billing.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-017 | missing | missing | **v0.2 §17 Milestone Billing.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-018 | missing | missing | **v0.2 §18 Payment Terms per Customer and Invoice.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-019 | missing | missing | **v0.2 §19 Late Status.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-020 | missing | missing | **v0.2 §20 Receivable Aging Must Use Outstanding Amount.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-021 | missing | missing | **v0.2 §21 Opening Balances.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-022 | missing | missing | **v0.2 §22 CPA Adjustments.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-023 | missing | missing | **v0.2 §23 Year-End Closing.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-024 | missing | missing | **v0.2 §24 Cash vs Accrual Reporting.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-025 | missing | missing | **v0.2 §25 Duplicate Detection.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-026 | missing | tested | **v0.2 §26 Currency Precision.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. **WI-0012:** money is int64 minor units with an explicit currency; parsing refuses more than two decimals; quantity extension rounds half away from zero; currencies never mix (`Money`, `LedgerTests`). | later |
| SUM2-027 | missing | missing | **v0.2 §27 Date Semantics.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-028 | missing | missing | **v0.2 §28 Audit Provenance.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-029 | missing | missing | **v0.2 §29 Permissions Boundary.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-030 | missing | missing | **v0.2 §30 Agent Execution Safety.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-031 | missing | missing | **v0.2 §31 Integration Boundaries.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM2-032 | missing | missing | **v0.2 §32 Required Scenario Tests.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-001 | missing | missing | **v0.3 §1 Transaction Boundaries.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-002 | missing | missing | **v0.3 §2 Failure Recovery.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-003 | missing | missing | **v0.3 §3 Concurrency.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-004 | missing | missing | **v0.3 §4 Database Invariants.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-005 | missing | missing | **v0.3 §5 Persistence Strategy.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-006 | missing | missing | **v0.3 §6 Backup Requirements.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-007 | missing | missing | **v0.3 §7 Restore Testing.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-008 | missing | missing | **v0.3 §8 Recovery Objectives.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-009 | missing | missing | **v0.3 §9 Schema Migrations.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-010 | missing | missing | **v0.3 §10 Migration of Financial Data.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-011 | missing | missing | **v0.3 §11 API Contracts.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-012 | missing | missing | **v0.3 §12 Authentication.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-013 | missing | missing | **v0.3 §13 Authorization.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-014 | missing | missing | **v0.3 §14 Agent Identity.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-015 | missing | missing | **v0.3 §15 Human Approval Boundaries.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-016 | missing | missing | **v0.3 §16 Observability.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-017 | missing | missing | **v0.3 §17 Structured Logging.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-018 | missing | missing | **v0.3 §18 Correlation IDs.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-019 | missing | missing | **v0.3 §19 Monitoring Invariant Violations.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-020 | missing | missing | **v0.3 §20 Reconciliation Jobs.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-021 | missing | missing | **v0.3 §21 Audit Immutability.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-022 | missing | missing | **v0.3 §22 Sensitive Data.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-023 | missing | missing | **v0.3 §23 Secrets Management.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-024 | missing | missing | **v0.3 §24 Encryption.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-025 | missing | missing | **v0.3 §25 Import Safety.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-026 | missing | missing | **v0.3 §26 Export Reproducibility.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-027 | missing | missing | **v0.3 §27 Report Consistency.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-028 | missing | missing | **v0.3 §28 Deterministic Calculations.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-029 | missing | missing | **v0.3 §29 Timezone Handling.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-030 | missing | missing | **v0.3 §30 Document Versioning.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-031 | missing | missing | **v0.3 §31 External Email Failure.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-032 | missing | missing | **v0.3 §32 Integration Outage Handling.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-033 | missing | missing | **v0.3 §33 Idempotent Integrations.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-034 | missing | missing | **v0.3 §34 Deletion Policy.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-035 | missing | missing | **v0.3 §35 Data Retention.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-036 | missing | missing | **v0.3 §36 Health Checks.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-037 | missing | missing | **v0.3 §37 Safe Startup.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-038 | missing | missing | **v0.3 §38 Deployment Safety.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-039 | missing | missing | **v0.3 §39 Rollback Strategy.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-040 | missing | missing | **v0.3 §40 Production Data Separation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-041 | missing | missing | **v0.3 §41 Test Data.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-042 | missing | missing | **v0.3 §42 Property and Invariant Tests.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-043 | missing | missing | **v0.3 §43 Failure Injection Testing.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM3-044 | missing | missing | **v0.3 §44 Recovery Drill.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-001 | missing | missing | **v0.4 §1 Home Dashboard.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-002 | missing | missing | **v0.4 §2 Global Create Action.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-003 | missing | missing | **v0.4 §3 Customer Workflow.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-004 | missing | missing | **v0.4 §4 Invoice Creation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-005 | missing | missing | **v0.4 §5 Smart Invoice Defaults.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-006 | missing | missing | **v0.4 §6 Invoice Preview.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-007 | missing | missing | **v0.4 §7 Invoice Proposal Workflow.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-008 | missing | missing | **v0.4 §8 Invoice History.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-009 | missing | missing | **v0.4 §9 Receivables Workspace.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-010 | missing | missing | **v0.4 §10 Attention-First Receivables.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-011 | missing | missing | **v0.4 §11 Payment Recording.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-012 | missing | missing | **v0.4 §12 Automatic Payment Matching.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-013 | missing | missing | **v0.4 §13 Unallocated Payments Inbox.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-014 | missing | missing | **v0.4 §14 Overdue Follow-Up.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-015 | missing | missing | **v0.4 §15 Follow-Up Status.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-016 | missing | missing | **v0.4 §16 Payment Promises.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-017 | missing | missing | **v0.4 §17 Invoice Disputes.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-018 | missing | missing | **v0.4 §18 Search.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-019 | missing | missing | **v0.4 §19 Command Palette.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-020 | missing | missing | **v0.4 §20 Ledger Workspace.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-021 | missing | missing | **v0.4 §21 Explain the Accounting.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-022 | missing | missing | **v0.4 §22 Source Traceability.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-023 | missing | missing | **v0.4 §23 Financial Report Navigation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-024 | missing | missing | **v0.4 §24 Date Range Shortcuts.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-025 | missing | missing | **v0.4 §25 Period Close Workspace.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-026 | missing | missing | **v0.4 §26 Period Readiness.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-027 | missing | missing | **v0.4 §27 CPA Workspace.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-028 | missing | missing | **v0.4 §28 CPA Export Package.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-029 | missing | missing | **v0.4 §29 Saved Report Views.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-030 | missing | missing | **v0.4 §30 Project Profitability.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-031 | missing | missing | **v0.4 §31 Unified Work Queue.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-032 | missing | missing | **v0.4 §32 Notification Philosophy.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-033 | missing | missing | **v0.4 §33 Avoid Unnecessary Accounting Jargon.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-034 | missing | missing | **v0.4 §34 Accounting Detail on Demand.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-035 | missing | missing | **v0.4 §35 Keyboard Usability.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-036 | missing | missing | **v0.4 §36 Responsive UI.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-037 | missing | missing | **v0.4 §37 Accessibility.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-038 | missing | missing | **v0.4 §38 Confirmation Behavior.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-039 | missing | missing | **v0.4 §39 Draft Persistence.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-040 | missing | missing | **v0.4 §40 Error Messages.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-041 | missing | missing | **v0.4 §41 Domain-State-Driven UI.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-042 | missing | missing | **v0.4 §42 Agent Actions in the UI.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-043 | missing | missing | **v0.4 §43 Agent Explanation.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-044 | missing | missing | **v0.4 §44 Human Override.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| SUM4-045 | missing | missing | **v0.4 §45 No Autonomous Financial Surprises.** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-A11Y | missing | missing | **Invoice generation INV-A11Y-001 to INV-A11Y-008 (8 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-ACC | missing | missing | **Invoice generation INV-ACC-001 to INV-ACC-006 (6 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-ADJ | missing | missing | **Invoice generation INV-ADJ-001 to INV-ADJ-007 (7 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-AGENT | missing | missing | **Invoice generation INV-AGENT-001 to INV-AGENT-008 (8 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-ARCH | missing | missing | **Invoice generation INV-ARCH-001 to INV-ARCH-010 (10 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-AUD | missing | missing | **Invoice generation INV-AUD-001 to INV-AUD-005 (5 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-CHR | missing | missing | **Invoice generation INV-CHR-001 to INV-CHR-012 (12 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-CON | missing | missing | **Invoice generation INV-CON-001 to INV-CON-008 (8 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-CONTRACT | missing | missing | **Invoice generation INV-CONTRACT-001 to INV-CONTRACT-005 (5 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-COR | missing | missing | **Invoice generation INV-COR-001 to INV-COR-011 (11 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-DATA | missing | missing | **Invoice generation INV-DATA-001 to INV-DATA-014 (14 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-DEL | missing | missing | **Invoice generation INV-DEL-001 to INV-DEL-010 (10 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-DOC | missing | missing | **Invoice generation INV-DOC-001 to INV-DOC-015 (15 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-DRAFT | missing | missing | **Invoice generation INV-DRAFT-001 to INV-DRAFT-007 (7 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-EXP | missing | missing | **Invoice generation INV-EXP-001 to INV-EXP-003 (3 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-FAIL | missing | missing | **Invoice generation INV-FAIL-001 to INV-FAIL-005 (5 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-FX | missing | missing | **Invoice generation INV-FX-001 to INV-FX-003 (3 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-INTL | missing | missing | **Invoice generation INV-INTL-001 to INV-INTL-003 (3 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-ISS | missing | missing | **Invoice generation INV-ISS-001 to INV-ISS-013 (13 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-NUM | missing | missing | **Invoice generation INV-NUM-001 to INV-NUM-009 (9 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-OPS | missing | missing | **Invoice generation INV-OPS-001 to INV-OPS-006 (6 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-PAYINST | missing | missing | **Invoice generation INV-PAYINST-001 to INV-PAYINST-004 (4 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-PERF | missing | missing | **Invoice generation INV-PERF-001 to INV-PERF-004 (4 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-RATE | missing | missing | **Invoice generation INV-RATE-001 to INV-RATE-006 (6 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-REC | missing | missing | **Invoice generation INV-REC-001 to INV-REC-004 (4 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-REV | missing | missing | **Invoice generation INV-REV-001 to INV-REV-007 (7 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-SEC | missing | missing | **Invoice generation INV-SEC-001 to INV-SEC-006 (6 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-SET | missing | missing | **Invoice generation INV-SET-001 to INV-SET-007 (7 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-SOURCE | missing | missing | **Invoice generation INV-SOURCE-001 to INV-SOURCE-008 (8 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-STATE | missing | missing | **Invoice generation INV-STATE-001 to INV-STATE-008 (8 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-TERM | missing | missing | **Invoice generation INV-TERM-001 to INV-TERM-005 (5 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |
| INV-UI | missing | missing | **Invoice generation INV-UI-001 to INV-UI-012 (12 requirements).** Not implemented: the existing code is the project-administration hub and backlog pages, with no financial domain. | later |

## Coverage after this programme

| Corpus | Rows | Current tested | Current partial | Current missing |
|---|---:|---:|---:|---:|
| Summa requirements | 223 | 9 | 5 | 209 |
