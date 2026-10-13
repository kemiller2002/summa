# Summa invoice command contract, version 1.1

Wire contract `summa.invoice-commands` 1.1. Owner: Summa (INV-CONTRACT-002,
SUM3-011, DF-SUMMA-2026-0036, DF-SUMMA-2026-0037).

## What 1.1 changes

- **Two commands added:** `record-payment` and `allocate-payment`. They
  are defined only from 1.1, so a message carrying either is written at
  version `1.1`.
- **Nothing else changes.**
  - Every other message is still written at `1.0`, byte for byte as
    before, so a 1.0 reader keeps reading it.
  - Responses are written at `1.0`; their shape has not changed.
- **Refusals:**
  - A message that claims `1.0` but carries a 1.1 command is refused at
    `$.message.command.kind`.
  - A 1.0 reader refuses the new commands as unknown kinds.

The 1.0 contract is [`invoice-commands-1.0.md`](invoice-commands-1.0.md).

This document is the contract. You do not need Summa's source to use it.
For reference, the source is `src/Summa.Ledger/InvoiceCommands.fs`. The
golden vectors are in `tests/Summa.Tests/vectors/invoice-commands`:

- `valid/`: canonical requests and responses.
- `invalid/`: messages a reader must refuse. Each file is named
  `<case>@<path>.json`, where `<path>` is the JSON path the refusal must
  report.

An external caller asks Summa to carry out one invoice command. Summa
answers with one outcome. Each command is a domain operation (INV-CONTRACT-003).
None is a generic record update.

## Envelope

Every message, request or response, is one JSON object:

```json
{"contract":"summa.invoice-commands","kind":"request","message":{...},"version":"1.1"}
```

| Member | Value |
|---|---|
| `contract` | Always `summa.invoice-commands`. |
| `kind` | `request` from the caller, or `response` from Summa. |
| `version` | `major.minor`: the oldest version that defines the message, `1.0` or `1.1`. |
| `message` | The request or response below. |

Encoding is canonical:

- no insignificant whitespace;
- object members sorted by key, in UTF-16 code-unit order;
- strings escape only `"`, `\` and control characters;
- whole numbers only.

For these value types, this is RFC 8785, so equal messages have equal bytes.

Summa parses strictly. It refuses:

- comments and trailing commas;
- a member name that appears twice;
- fractional numbers;
- nesting deeper than 32 levels.

Every problem found is reported, each with its JSON path, for example
`$.message.command.issueDate`.

## Request

```json
{"command":{...},"requestId":"req-0003"}
```

| Field | Type | Rule |
|---|---|---|
| `requestId` | text | Required and not blank. It is the caller's idempotency key, and the response returns it unchanged. |
| `command` | object | One of the commands below, chosen by its `kind`. |

### Commands

Dates are calendar dates, `yyyy-MM-dd`. Ids are non-blank text.

**`propose-invoice`**: proposes an invoice from time Summa has imported
from Chrona. A person reviews the proposal before anything is issued.

| Field | Type | Rule |
|---|---|---|
| `proposalId` | text | The proposal's id, chosen by the caller. |
| `customerId` | text | The customer billed. |
| `engagementId` | text or `null` | The engagement, if any. |
| `currency` | text | A three-letter ISO 4217 code in capitals, for example `USD`. |
| `time` | list of text | Chrona publication ids. At least one, and none twice. |

**`submit-for-review`**: submits the draft version the caller looked at.

| Field | Type | Rule |
|---|---|---|
| `draftId` | text | The draft. |
| `expectedVersion` | whole number | The draft version the caller reviewed, 1 or more. |

**`issue-invoice`**: issues the reviewed draft version.

| Field | Type | Rule |
|---|---|---|
| `draftId` | text | The draft. |
| `expectedVersion` | whole number | The reviewed version, 1 or more. If the draft has changed since, the outcome is a conflict. |
| `issueDate` | date | The invoice's issue date. |

**`void-invoice`**: voids an invoice that nothing has settled.

| Field | Type | Rule |
|---|---|---|
| `invoiceId` | text | The invoice. |
| `reason` | text | Why it is voided. Required and not blank. |
| `date` | date | The void's date. |

**`record-payment`** (since 1.1): records a payment received from a
customer. It is not yet applied to any invoice.

| Field | Type | Rule |
|---|---|---|
| `paymentId` | text | The payment's id, chosen by the caller. The same id with the same content changes nothing; with other content it is refused. |
| `customerId` | text | The customer who paid. |
| `dateReceived` | date | When it was received. |
| `amount` | money | More than zero. |
| `method` | text | `ach`, `check`, `wire`, `credit-card`, `cash` or `other`. |
| `reference` | text | The bank or check reference, possibly empty. |
| `memo` | text or `null` | A note. |

**`allocate-payment`** (since 1.1): applies part of a payment to an invoice
and posts it.

| Field | Type | Rule |
|---|---|---|
| `allocationId` | text | The allocation's id. The same id again changes nothing. |
| `paymentId` | text | The payment applied. |
| `invoiceId` | text | The invoice it pays. |
| `amount` | money | More than zero. It may be no more than the payment has left, nor more than the invoice owes. |

Money is `{"currency":"USD","minor":100000}`: a three-letter ISO 4217 code
and a whole number of minor units, here 1,000.00. It is never a decimal.

## Response

```json
{"outcome":{...},"requestId":"req-0003"}
```

| Field | Type | Rule |
|---|---|---|
| `requestId` | text | The request's `requestId`. |
| `outcome` | object | One of the outcomes below, chosen by its `kind`. |

### Outcomes

**`accepted`**: the command was carried out.

| Field | Type | Rule |
|---|---|---|
| `produced.kind` | text | `proposal`, `draft`, `invoice`, `invoice-void`, `payment` or `allocation`. |
| `produced.id` | text | The id of what was produced or changed. |
| `produced.version` | whole number or `null` | The draft's version, for a draft. |
| `produced.number` | text or `null` | The customer-facing number, for an invoice. |

**`refused`**: nothing changed.

| Field | Type | Rule |
|---|---|---|
| `reasons` | list | At least one reason. Each is `{code, message}`: a stable code (for example `void-refused`, `proposal-refused`, `payment-refused` or `allocation-refused`) and a sentence. |

Refusal codes are an **open set**, in every 1.x version.

- A reader must accept a code it does not know, and act on the outcome
  (`refused`, so nothing changed) and the sentence.
- New codes, such as `not-authorized` (added with the capability check),
  appear in responses at any 1.x version, and are not a version change.
- Only the outcome kinds (`accepted`, `refused` and `conflict`) are fixed
  by the version.

**`conflict`**: the draft is not at the version the caller named, and
nothing changed. Review the current version, then send the command again
naming it.

| Field | Type | Rule |
|---|---|---|
| `currentVersion` | whole number | The draft's current version. |

Sending the same `issue-invoice` request again is safe. It returns the same
invoice and changes nothing (INV-CON-007).

## Versioning

- **Minor versions are additive.** A reader of 1.1 reads any `1.x` message.
  It ignores members a newer minor adds, wherever they appear. In a `1.0`
  or `1.1` message, a member not listed here is refused.
- **A different major version is refused.** It is never guessed at.
  Version 2 would be a separate contract version, read beside this one.
- **A new command or outcome kind needs a new minor version.** A reader
  refuses an unknown `kind`, so older readers are never silently given a
  command they do not know (SUM3-011).

## Identity

The message never says who is acting.

- The host authenticates the caller and runs the command as that caller,
  with that caller's capabilities.
- A message cannot claim another identity.
- Summa records the authenticated actor in the audit trail of whatever the
  command changes.
