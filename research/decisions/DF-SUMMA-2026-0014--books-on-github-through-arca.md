---
id: DF-SUMMA-2026-0014
title: Books on GitHub through Arca's GitHub adapter, each change a command
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
  - research/decisions/DF-SUMMA-2026-0009--the-accounting-application-and-its-local-books.md
  - research/decisions/DF-SUMMA-2026-0012--organization-manifest-schema-2-and-its-migration.md
  - research/decisions/DF-SUMMA-2026-0013--sign-in-through-fides-in-the-application.md
tags: [storage, github, arca, commands, concurrency]
provenance:
  contributions:
    EXE-20261008T221837840Z-607ce088:
      operations: [created]
      at: 2026-10-09T00:09:17.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide how the accounting application keeps its books on GitHub (WI-0037 part 2)"
---

# DF-SUMMA-2026-0014 — Books on GitHub through Arca's GitHub adapter, each change a command

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0037 (part 2)

## Context

With sign-in in place (DF-SUMMA-2026-0013), a deployment with a `location`
keeps its books in a GitHub data repository. `Summa.Storage` already has
the provider-neutral pieces:
- `Commands.execute`: one command, one conditioned commit, decided again
  on a conflict;
- `Governance`: who sets an organization up;
- `Compatibility` and `Migrations`.

Arca 0.2.1's GitHub adapter is a `StorageProvider` whose HTTP, waits and
tokens come from a host. The engine, though, was written for books in the
browser: each change computes the next books and saves a snapshot.

## Decisions

1. **Opening is one provider-neutral function**
   (`Summa.Storage.Workspace.openBooks`). It reads the organization's Arca
   manifest, organization manifest, roster and records. It answers:
   - opened, with the person's capabilities from the roster and read-only
     reasons;
   - not set up (with whether this person may set it up);
   - held for a listed administrator;
   - needs migration;
   - not a member;
   - unusable (integrity problems, unreadable data).
   Setting up (`foundingOperations`) initializes Summa's namespace when the
   repository has none, then the organization, with the founder as its first
   administrator. It refuses production data in a public repository.
   `confirmation` lets a listed administrator confirm themself.
2. **Every change to books on GitHub is a command, and the command is the
   message.** In GitHub mode the engine's `update`:
   - checks the capability the message needs (`bookCommand`);
   - shows the change at once;
   - emits `CommitBooks` with the message, instead of saving a snapshot.
   The wire hands the store a transition, `replay ctx msg basis`, which runs
   the same message on the model it was made on, with the books as they
   stand on GitHub. `Commands.execute` runs it. When someone else changed
   the books first, the conflict re-reads and decides again with the same
   inputs, so numbering and validation follow the newer books (two pages
   adding customers get CUST-0001 and CUST-0002). The engine has no second
   copy of its rules for the store. Company details commit through
   `Storage.updateOrganization` under the revision read.
3. **The store is a port at the edge** (`Summa.Web.Application.Store`).
   - It runs one job at a time.
   - It answers with engine messages (`BooksOpened`, `BooksNotOpened`,
     `BooksCommitted`, `StoreRefused` with the books as they now stand).
   - It reaches GitHub through Arca's adapter: requests are Limen Http
     requests through the bridge, waits are `limen.schedule` timeouts, and
     the token comes from Fides' token provider.
4. **Nothing in the store path uses the thread pool.** `Commands.readAll`
   reads folder by folder in order (no `Async.Sequential`). In the
   single-threaded browser, a thread-pool hop would finish after the reply,
   and nothing would deliver its result.
5. **One organization per deployment for now:** the first configured one. A
   chooser comes with the second organization.
6. **Readopt from the router.** When access changes (signed in, books
   opened), the engine adopts its router's current location again, not the
   page's first address, so a resumed deep link stays where it is.

## Consequences

- `StoreTests` run the engine, wire, store port and Arca against Arca's
  in-memory provider (made synchronous, as the browser is):
  - setting up;
  - working;
  - someone else's change in between;
  - non-members;
  - missing capabilities.
- `WorkspaceTests` cover opening, setting up, confirming, migrating and
  refusing.
- `github-books.spec.js` drives the WASM engine against a fake of GitHub's
  REST API (from Chrona's suite), with the Fides fakes:
  - set up;
  - add a customer and find its commit on the branch;
  - reload and read it back.
- Still to do in WI-0037:
  - the offline queue (Arca's `OfflineQueue` on localStorage), so changes
    survive a lost connection and a reload;
  - showing conflicts and outside edits for the person to resolve;
  - draining the queue before shutdown;
  - reading the application manifest at start-up (SUM0-007).
