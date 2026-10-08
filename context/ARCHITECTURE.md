# Summa architecture

## Current architecture

Summa is an F# application on the Echelon foundation stack
(DF-SUMMA-FND-2026-0002): Limen carries the browser protocol, Forma renders
the interactive pages, Folio renders printable documents, Aegis classifies
unexpected failures at architectural boundaries.

| Tier | Project | Responsibility |
|---|---|---|
| Domain | `src/Summa.Ledger` | Pure financial rules: fixed-decimal money, chart of accounts, balanced journal entries, posting, reversal, periods, customers, invoices, receivables, payments, aging and reports. No I/O. |
| Engine | `src/Summa.Web.Engine` | Pure page decisions for the hub and backlog pages. |
| Application | `src/Summa.Web.Application` | Limen wire protocol and the Aegis boundary around the engine. |
| Host | `src/Summa.Wasm` | The WebAssembly host the pages load. |
| Pages | `web/`, `web-kernel/` | Limen/Forma pages. |

Decisions that are costly to reverse:

- The ledger is the source of truth; invoice and payment status are derived
  from it (WI-0012..WI-0014).
- Summa owns the Chrona-to-Summa contract and publishes it as a versioned
  contracts package (DF-SUMMA-2026-0001).
- Authoritative financial data is stored in GitHub through Arca, in a
  Summa-owned namespace at a per-deployment location, in its own repository
  (DF-SUMMA-2026-0001, SUM-DATALOC-001). Summa does not use Strata.
- Sign-in is GitHub through Fides (DF-SUMMA-2026-0001).

## Architectural constraints

- Canonical records must remain independent of any model vendor or chat.
- Secrets must not enter fixtures, logs, prompts or stored records.
- Generated views must not silently replace canonical source records.
- Domain code is functional: immutable values, total functions returning
  `Result` for expected failures, no hidden state.
