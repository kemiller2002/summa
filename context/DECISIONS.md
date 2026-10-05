# Summa decisions

Material decisions use `DF-` records under `research/decisions/`. This compact
table is a navigation view, not a replacement for those records.

| Date | Decision | Status | Rationale | Record |
|---|---|---|---|---|
| 2026-09-14 | Use ROS 2.0.1-main.78.1 as a measured greenfield pilot. | provisional | Test portability and operational value on a real beginning project. | Not yet promoted to a `DF-` record |
| 2026-10-05 | Declare Aegis, Forma and Folio not yet applicable. | superseded | Summa had no product foundation of its own. | `DF-SUMMA-FND-2026-0001` |
| 2026-10-05 | Build Summa the same way as the other Echelon applications: F# engine on WebAssembly behind Limen, Aegis at its boundary, Forma and Folio from the pinned `echelon-current` releases; all foundations required. | accepted | Owner: "we want all apps built the same way." | `DF-SUMMA-FND-2026-0002` |
