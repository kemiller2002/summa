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

## The routine run (SUM3-006)

DF-SUMMA-2026-0042 sets out the routine run. Schedule one run per
organization at least once a day, so the newest backup is never older than
the 24-hour recovery point. Each run does three jobs in order:

1. **Proposals.** It makes an invoice proposal for every recurring
   template occurrence that is due by today, in the organization's time
   zone. A person reviews each one. The run never makes a draft or an
   invoice. A template that cannot propose is reported with why and holds
   back no other. Running again on the same day makes nothing new.
2. **Backup.** It takes a sealed backup, restores it into an empty scratch
   folder and verifies it there. Only then does it store the backup in the
   backup location. A backup that does not restore, does not verify or
   cannot be stored is not kept, and the report says why.
3. **Retention.** It deletes kept backups that fall outside the 14-day,
   8-week and 12-month policy.

Every job runs and reports even when an earlier one failed. The report also
says whether the newest kept backup meets the recovery point. Alert when it
does not.

### Setting it up

- **Admit the run.** An administrator admits a principal of kind
  `ScheduledProcess` to the organization's roster, for example
  `summa-routine-acme`, with `ViewFinancials` and `ProposeInvoice` only.
  The run records itself as `automation`, never as a person. It cannot
  issue, and it can never manage users or the organization.
- **Give the host what it needs.** The machine that runs it needs:
  - a storage credential for the organization's data location;
  - the 32-byte backup key from the operator's key store;
  - a backup location outside the organization folder, which provides
    put, list and delete.
- **Each run supplies:** the time, a unique run id, and 12 fresh random
  bytes for the nonce. The run id becomes the run's execution and
  correlation id.
- **Backup ids:** the organization id and the UTC instant the backup was
  taken, for example `org_acme-20261220T060000Z`.

No deployment schedules the run yet. That waits on a real deployment
(WI-0036).

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
