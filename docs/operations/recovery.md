# Backups, restore and the recovery drill

The decisions behind this procedure are in DF-SUMMA-2026-0004: an RPO of
24 hours, an RTO of 4 hours, sealed backups, verified restores, and
retention of 14 days, 8 weeks and 12 months.

## Taking a backup

`Summa.Operations.Backup.take provider ns key nonce` exports one
organization folder as of a single change token. The export includes every
object with its SHA-256, and is sealed with AES-256-GCM.

- `key` is 32 bytes from the operator's key store.
- `nonce` is 12 fresh random bytes for each backup.

Store the sealed text anywhere. It is unreadable without the key, and any
change to it is detected.

## Restoring

1. Make the target location empty, for example a new data repository at the
   configured location.
2. `Backup.unseal key backup` decrypts the backup. It fails on a wrong key,
   on tampering, and on any object whose hash no longer matches.
3. Commit `Backup.restoreOperation ns metadata archive`. This is a single
   commit that recreates every object exactly as it was exported.
4. Run `Recovery.verify provider ns receivablesAccount asOf`. Treat the
   restore as done only when this returns `Ok`.

## The drill (v0.3 §44)

`RecoveryTests.the recovery drill` follows these steps on every CI run:

1. Back up the books.
2. Destroy the environment.
3. Restore from the backup.
4. Start: the folder opens through its manifests.
5. Run the integrity checks: audit, invariants, reconciliation.
6. Produce the reports: trial balance.

Before Summa is trusted with real books, repeat the drill once against a
production-like deployment (WI-0036).

## Reconciliation jobs

`Summa.Storage.Reconciliation.run receivablesAccount asOf books` checks that:

- journal debits equal journal credits;
- the receivables control account equals the open invoices;
- no payment has more allocated to it than was received;
- no invoice is overpaid.

`Verification.audit` adds two more checks: each record's history, and
every invariant. Run both on a schedule and after a restore. Any finding is
an integrity failure.
