# EchelonFoundry.Summa.Contracts

The Chrona-to-Summa billing contract. Summa owns it because Summa is the
application that accepts the data (DF-SUMMA-2026-0001).

- `Summa.Contracts.ChronaBilling.V1`: the types.
  - Chrona sends a `Publication`: `BillableTimePublished` or
    `PublicationWithdrawn`.
  - Summa sends `Feedback`: `InvoicedExternally` or `AdjustmentNeeded`.
- `Codec`: canonical JSON, with `tryEncodePublication`/`tryEncodeFeedback`
  (validate, then encode), `decodePublication`/`decodeFeedback` (structure,
  then every rule), and `digest`, which hashes the canonical bytes.
- `Validate`: the domain rules on typed values.
- `GoldenVectors`: canonical valid messages, and invalid ones paired with the
  path of the problem each must report. Run your encoder and decoder against
  them.

The wire contract is `summa.chrona-billing` version `1.0`. A reader accepts
any `1.x` message and ignores members a newer minor adds. It refuses every
other major version. A new major version gets its own namespace (`V2`), so
two majors can be read side by side.

No I/O and no dependencies beyond FSharp.Core.
