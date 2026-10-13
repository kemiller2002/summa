---
id: DF-SUMMA-2026-0033
title: Reads pinned to the observed commit (Arca 0.5.0); no read cache to batch
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
  - research/decisions/DF-SUMMA-2026-0025--commands-read-only-what-changed.md
tags: [performance, arca, storage]
provenance:
  contributions:
    EXE-20261013T125632051Z-07e0596b:
      operations: [created]
      at: 2026-10-13T13:17:48.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Record the Arca 0.5.0 adoption decisions and measurements (WI-0050)"
---

# DF-SUMMA-2026-0033 — Reads pinned to the observed commit; no read cache to batch

- **Date:** 2026-10-13
- **Status:** accepted
- **Work item:** WI-0050

## Context

DF-SUMMA-2026-0025 left two upstream items to adopt when released:

- arca#38, a batched read-cache save;
- arca#39, pinned GitHub reads that skip the ref fetch.

Arca 0.5.0 has both and is selected in echelon-current 1.19.0.

## Decisions

1. **Adopted through Conditor.** `conditor upgrade --current` to
   echelon-current 1.19.0 with a checked plan (sha256:c3e3c5f1) and an
   isolated `--home`. Only Arca moves (0.4.1 to 0.5.0). Conditor wrote the
   pins; none were edited by hand.
2. **Reads after an observed state are pinned to its commit.**
   - The store and the opening observe `NamespaceState` before reading.
   - They then read every listing and file through Arca's `PinnedReads` at
     that state's `RepositoryToken` (`GitHubStorage.connect … PinAt`).
     Each read is one request with no ref fetch.
   - Together the reads are one consistent snapshot, so the records stored
     as last read are exactly those at the token stored with them.
   - Commits, and the namespace's current state, still go to the unpinned
     provider, so conditions on writes are unchanged.
   - A host without pins (`Pins` gives None) reads as before.
3. **No batched read-cache save.**
   - Summa keeps no Arca read-cache partitions. It has no
     `ReadCacheStore`, `IndexedDbCache` or `MemoryReadCache`.
   - Its browser storage is Arca's offline queue (one entry per change)
     and its own last-read records, held in memory.
   - `SaveMany` has nothing to replace, so none is invented.

## Consequences

Measured in the browser against the GitHub fake: three runs each, the
DF-SUMMA-2026-0025 scenarios, the same harness and builds made the same
day.

| Step | Arca 0.4.1 | Arca 0.5.0, unpinned | Arca 0.5.0, pinned |
|---|---|---|---|
| Found | 4.2–4.8 s | 4.3–4.4 s | 3.2–3.4 s |
| Add a customer | 2.0–3.1 s | 2.0–2.1 s | 2.0 s |
| Save a draft | 1.1–2.1 s | 1.1 s | 1.1 s |
| Issue | 2.2–2.4 s | 2.2–2.3 s | 1.2–1.3 s |
| Reload | 3.7–4.4 s | 2.9–3.8 s | 2.5–3.1 s |
| Reload, sending a kept change | 4.6–4.8 s | 3.6–4.5 s | 2.6–2.9 s |

- The 0.4.1 figures are higher than DF-SUMMA-2026-0025's "after" column,
  because founding now writes the chart (WI-0048) and the books hold more
  records.
- The upgrade alone changes little: Summa's reads were unpinned.
- Pinning cuts each read's ref fetch. Issue and the reloads gain the most,
  because they read the most after observing the state.
- Adding a customer and saving a draft read only what changed, so there
  was little to save.
