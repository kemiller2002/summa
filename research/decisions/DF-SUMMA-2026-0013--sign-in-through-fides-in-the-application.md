---
id: DF-SUMMA-2026-0013
title: Sign-in through Fides' client in the accounting application
status: accepted
version: 1.1.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - summa
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-SUMMA-2026-0009--the-accounting-application-and-its-local-books.md
  - research/decisions/DF-SUMMA-2026-0010--deep-links-through-limen-routing.md
  - docs/deployment-configuration.md
tags: [identity, fides, github, limen, sign-in]
provenance:
  contributions:
    EXE-20261008T224159765Z-e3bcf517:
      operations: [created]
      at: 2026-10-08T22:56:40.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Decide how the accounting application signs people in with GitHub through Fides (WI-0035)"
    EXE-20261008T231225908Z-26468e34:
      operations: [modified]
      at: 2026-10-08T23:21:06.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Carry the return target across the round trip to GitHub (WI-0043)"
---

# DF-SUMMA-2026-0013 — Sign-in through Fides' client in the accounting application

- **Date:** 2026-10-08
- **Status:** accepted
- **Work items:** WI-0035, WI-0043 (1.1.0)

## Context

Books kept on GitHub (a deployment with a `location`) need to know who
acts (SUM0-003, SUM3-012) and a GitHub token for the repository, handled as
a secret (SUM0-004). Fides 0.2.0 provides the exchange and an F# client,
`EchelonFoundry.Fides.Client`, installed by Conditor like Arca. The client
is asynchronous: it asks its host for browser services (`ClientPorts`).
Summa's engine is pure, and its only way to the browser is Limen's
request/reply protocol. Chrona solved the same problem (DF-CHRONA-2026-0004).
Fides is not deployed yet, so nothing about it may be hard-coded.

## Decisions

1. **Fides' own client, unmodified.** Summa does not reimplement PKCE, the
   callback check, retention, refresh or revocation.
   `Summa.Web.Application.Identity` creates the client and implements its
   ports, as Chrona does.
2. **Every port is a Limen request, through a bridge at the edge.**
   - `Summa.Web.Application.Bridge` parks each port call's continuation
     under a correlation id. The request goes out with the engine's reply,
     and the kernel's answer resumes it.
   - The exchange is Limen's Http effect (POST, text response, no
     credentials), and device storage is its Storage effect.
   - What Limen's core lacks goes through Summa's `summa.host` pack
     (`web-kernel/host.js`): this tab's session storage, leaving for
     GitHub (https only), replacing the address within this origin, and a
     token-free broadcast to other tabs.
   - The bridge is the application's one piece of mutable machinery. It
     lives in the wire, beside the composition root, so the engine stays a
     pure function of its messages.
   - A finished client operation becomes an engine message
     (`IdentityChanged`).
3. **The engine decides.**
   - `SignInState` covers not required (local books), restoring, signed
     out with a notice, leaving for the provider, signed in, and provider
     unavailable.
   - The engine chooses retention: this page by default, this tab if the
     person ticks the box.
   - Every reason code becomes words (`signInNotice`).
   - Until someone is signed in, the viewer is `Anonymous`. Every routed
     place then goes to `/sign-in?returnTo=…` (SUM-LINK-008), and the page
     shows nothing of the books.
   - Signing out rebuilds the model from its configuration, so nothing of
     the person stays in memory.
   - If GitHub is unreachable while someone is signed in, the session stays.
4. **Identity is GitHub's.** The actor is `github:<numeric subject>`, and
   the login is only a display name. The wire acts as the signed-in actor;
   local books keep their one local person.
5. **Tokens stay in Fides.** Summa never reads one. The GitHub store
   (WI-0037) will receive `Fides.Arca.TokenBridge.ofClient`, a token
   provider, never a token.
6. **The return target survives the round trip to GitHub (1.1.0, WI-0043).**
   GitHub sends the person back to the registered redirect address, which
   has no fragment.
   - Before leaving, the engine keeps the sign-in page's `returnTo` in this
     tab's session storage (`summa.returnTo`, through `summa.host`). It
     is a relative location, never a token.
   - A page that comes back with the callback reads it. Once both the
     identity and the target are known, in either order, the engine
     resumes there through `Routes.resume`: checked again against what
     the person may see, replacing sign-in in history, and sending anything
     foreign home. Then the tab forgets it.
   - A page that is not returning reads nothing: its own address says
     where to go.
7. **A location requires an identity.** A deployment that names a GitHub
   location but no way to sign in is misconfigured. Its
   Content-Security-Policy must allow the exchange's origin.

## Consequences

- Tests run the real Fides client against fakes:
  - .NET: a fake exchange that speaks Fides' wire protocol, and a fake tab
    (`SignInTests`).
  - Browser: Playwright routes for the configuration, the exchange and
    GitHub's authorize page, with the real WASM engine (`sign-in.spec.js`).
- Signed in, the page says it cannot open books on GitHub yet. The GitHub
  store (WI-0037) opens them, with the roster's capabilities in place of
  `viewerOf`'s interim "everything".
- The real end-to-end check against a deployed Fides is WI-0036.

## Revisit when

Fides publishes a Limen-native client, Limen's core gains session storage,
external navigation or broadcast effects, or a deployment needs another
identity provider.
