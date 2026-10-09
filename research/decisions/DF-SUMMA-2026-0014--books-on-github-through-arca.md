---
id: DF-SUMMA-2026-0014
title: Books on GitHub through Arca's GitHub adapter, each change a command
status: accepted
version: 1.3.0
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
      last: 2026-10-09T00:43:34.000Z
    EXE-20261009T023449249Z-e60c915b:
      operations: [modified]
      at: 2026-10-09T04:00:36.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Adopt Arca 0.4.0: namespace tokens, erasure and queued entries by account (WI-0045)"
    EXE-20261009T041729762Z-84e7aa9f:
      operations: [modified]
      at: 2026-10-09T04:23:10.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Keep queued changes' accounts and hold other sign-ins' changes (WI-0046)"
---

# DF-SUMMA-2026-0014 — Books on GitHub through Arca's GitHub adapter, each change a command

- **Date:** 2026-10-09
- **Status:** accepted
- **Work item:** WI-0037 (parts 2 and 3)

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

7. **Changes that cannot reach GitHub are kept, in order, never dropped
   (1.1.0).**
   - Every command is decided on the books as GitHub holds them, or, when
     GitHub cannot be reached, on the books as last read. It is then put in
     Arca's offline queue (`OfflinePolicy.QueueWrites`), the queue is saved,
     and it is sent through `OfflineSync`.
   - The page shows the change at once and says how many changes have not
     reached GitHub yet; "Send now" tries again.
   - The queue lives in this browser's localStorage (`LocalStorageQueue`)
     when this tab holds its Web Lock (`limen.coordination`). Otherwise it
     lives in this tab's memory, and the page says so.
   - A new page sends what an earlier one kept before it shows the books.
     An entry that was being sent is reconciled, never resent blindly.
   - When GitHub moved under a queued command of this page, the command is
     decided again on the newer books and revised in place. If it no longer
     applies, it is given up and the reason is shown.
   - A queued change from an earlier page cannot be decided again, because
     its message is gone. It waits for the person, who sees what it was and
     why it is stuck, and may give it up. Nothing is resolved silently.
8. **Edits made outside Summa are shown on request.** "Check the books"
   (Settings) runs `Verification.audit` against every financial record's
   GitHub history and lists what it finds; it changes nothing. Integrity
   failures already stop the books from opening.
9. **Start-up reads Summa's application manifest** (SUM0-007,
   `Workspace.checkApplication`). Books in a folder that is not Summa's, at
   another storage version, or needing a newer Summa are refused.
10. **Arca 0.4.0 (WI-0045): commands are held to their own namespace.**
    - Commands read `StorageProvider.NamespaceState` and condition their
      commit with `Operation.requireNamespaceToken` (ARCA-CON-005). A
      commit by another organization or application elsewhere in the
      repository no longer makes a decided change stale. A change inside
      the organization's folder still does: `StaleNamespaceToken` is
      decided again, like a conflict.
    - Unsent changes are queued with `OfflineQueue.enqueueFor`, under
      `AccountId.ofIdentity` of the account GitHub resolved. Sign-out can
      then match them by a stable id. Offering to send, keep or discard
      them at sign-out is recorded on WI-0044.
    - An erased member record (ARCA-INT-005) is no longer a member.
    - An erased financial record or manifest makes the books untrustworthy.
      Summa never erases one, and does not keep books without it.
    - The adoption came through Conditor 0.8.2 `upgrade --current` to
      echelon-current 1.17.0, which moves Praxis 3.7.2 to 3.11.0 in the
      same change. Praxis was not pinned.
11. **Unsent changes keep their account (WI-0046, Arca 0.4.1 through
    echelon-current 1.18.0).**
    - `OfflineQueue.revise` keeps the account the entry recorded. In 0.4.0
      it dropped it.
    - An entry kept without its account (before Arca 0.4.0, or revised
      under 0.4.0) is stamped only from the actor the entry itself
      recorded (`AccountId.ofActor`), never from the session and never by
      display name. One that names this person's actor id is theirs and is
      sent.
    - An entry that another account made, or that records no usable actor,
      is held. It is never stamped with the
      signed-in person's account, never sent as them, and never discarded
      on its own. The page says so and offers "Send them as me" or "Discard
      them"; the person's choice is what releases or abandons them.
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
- The browser suite also keeps a change while GitHub is unreachable and
  sends it after a reload (`github-books.spec.js`). `StoreTests` cover the
  same, plus a stuck change from an earlier page given up, and the books
  check.
- The invoice editor's unsaved edits (before "Save draft") are not kept
  across a refresh; that is WI-0044.
