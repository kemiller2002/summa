---
id: DF-SUMMA-2026-0025
title: Commands and openings read only what changed, at most six at a time, on the page's own turn
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
  - research/decisions/DF-SUMMA-2026-0024--books-founded-on-a-store-start-with-their-chart.md
tags: [performance, storage, browser, arca]
provenance:
  contributions:
    EXE-20261009T031528777Z-d8d5aa1a:
      operations: [created]
      at: 2026-10-13T10:49:21.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the read path decisions (WI-0029 slice 5, SUM0-034)"
---

# DF-SUMMA-2026-0025 — Commands and openings read only what changed

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0029 (SUM0-034)

## Context

Every command on GitHub-backed books, and every opening, re-read the whole
organization folder one request after another. Each command read it twice:
once to decide, and once afterwards for the books as they stand. In the
browser against the GitHub fake, a command took 3 to 5 seconds and a reload
about 4 seconds. That was the cause behind WI-0048's slow invoice test.
Chrona found the same pattern (its WI-0077). Two upstream items in Arca
bear on it: kemiller2002/arca#38 (batched read-cache save) and
kemiller2002/arca#39 (no ref fetch before a read pinned to a commit). They
are not worked around here.

## Decisions

1. **The last fresh read is the index commands read through.**
   - Before deciding, the store observes `NamespaceState`. When the
     namespace token equals the one the records were last read at, those
     records are the namespace's content and are used as they are.
   - Otherwise `Commands.readAllKnowing` lists every folder. A file whose
     listing shows the revision already held is that object, because a
     revision names one content. Only the others are read.
   - Openings do the same (`Workspace.openBooksKnowing`).
   - Only Arca's public contracts are used (`NamespaceState`, `List`,
     `Read`, `ListEntry.Revision`).
2. **The balances index (`Projection`) stays derived.** It never decides
   a write. A derived or cached value is never the basis of a write
   (Arca LCP-085), so commands do not read through it.
3. **At most six requests in flight.**
   - Folders are listed level by level, then the changed files are read, in
     one bounded fork-join (`ReadWidth = 6`, as in Chrona's WI-0077).
   - Each step starts in turn on the caller's thread, and results come back
     in the order given. A failure stops new steps, and the first failure in
     order is the result.
4. **A fork-join resumes on the host's turn** (`Commands.Turn`).
   - F#'s `Async.FromContinuations` hands a continuation called from
     inside a running async to the thread pool. In the browser, the thread
     pool runs only after the page has replied, so the books stayed
     "Opening…" until the next message. This was observed, then reproduced
     in .NET.
   - The page's bridge runs a deferred resumption right after the answer
     that finished the last step, outside any running async, within the
     same message.
   - Other hosts resume at once (`Turn.now`).
   - `WorkspaceTests` drives reads the way the bridge does, one answer at
     a time on one thread. It asserts that a read finishes within the
     answers, on that thread, with no more than six requests waiting.

## Consequences

Measured in the browser, against the GitHub fake (three runs each):

| Step | Before | After |
|---|---|---|
| Found | 4.3–5.9 s | 3.2–4.0 s |
| Add a customer | 3.0–3.1 s | 2.0 s |
| Save a draft | 3.0–5.2 s | 1.0–1.1 s |
| Issue | 3.0–5.1 s | 2.3 s |
| Reload | 3.8–4.4 s | 2.7–3.1 s |
| Reload, sending a kept change | 4.5–5.2 s | 3.6 s |

- The invoice test's wait drops from 30 s to 10 s.
- The offline test keeps its 5 s default, now with 1.4 s to spare.
- What remains is mostly folder listings and the ref fetch per request,
  which arca#39 addresses. Adopt it through Conditor when it is released.
