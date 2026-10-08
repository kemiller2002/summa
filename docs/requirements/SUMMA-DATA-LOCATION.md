---
id: SUM-DATALOC
title: Summa data location, application-owned namespace and permission separation
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [requirements, storage, data-location, arca]
provenance:
  contributions:
    EXE-20261008T074602101Z-3aa35119:
      operations: [created]
      at: 2026-10-08T07:47:02.752Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record user decisions of 2026-10-08 and the per-application data-location requirement"
---

# SUM-DATALOC — data location, namespace and permission separation

These requirements come from user decisions of 2026-10-08. They apply to
Summa and to every Echelon application that stores data through Arca
(`kemiller2002/arca`, requirements `ARCA-LOC-001..010`, decision
`DF-ARCA-2026-0002`).

**SUM-DATALOC-001 Configurable data location.** The data repository (owner,
repository, branch, base path) MUST be configured per deployment. Summa
MUST NOT hard-code a repository, owner, branch or root path.

**SUM-DATALOC-002 Application-owned namespace.** Summa MUST create and own its
own folder structure (namespace) in the configured repository and keep every
read and write inside it.

**SUM-DATALOC-003 Shared repositories.** Summa MUST NOT assume it is the only
application using the repository, or that it owns the repository root.

**SUM-DATALOC-004 Separable permissions.** Summa's data MUST be separable from
other applications' data under different permissions (Summa and Chrona, for example,
have different permissions). GitHub permissions apply per repository, not
per folder, so separation is achieved by pointing Summa at **its own
repository** through SUM-DATALOC-001. A shared repository is allowed only when the
applications in it may share permissions.

**SUM-DATALOC-005 No at-rest encryption for now.** Summa MUST NOT add
application-level at-rest encryption. Per-application encryption is a
deferred, possible future Arca item, not a requirement.

Related Summa requirements: SUM0-005 (multi-application repository), SUM0-006 (application boundary), SUM0-009 (organization storage location), SUM0-043 (repository safety), SUM3-040 (production data separation).
