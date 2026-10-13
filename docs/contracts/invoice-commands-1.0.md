# Summa invoice command contract, version 1.0

Wire contract `summa.invoice-commands` 1.0. Owner: Summa (INV-CONTRACT-002,
DF-SUMMA-2026-0036).

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
{"contract":"summa.invoice-commands","kind":"request","message":{...},"version":"1.0"}
```

| Member | Value |
|---|---|
| `contract` | Always `summa.invoice-commands`. |
| `kind` | `request` from the caller, or `response` from Summa. |
| `version` | `major.minor`, for example `1.0`. |
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
| `produced.kind` | text | `proposal`, `draft`, `invoice` or `invoice-void`. |
| `produced.id` | text | The id of what was produced or changed. |
| `produced.version` | whole number or `null` | The draft's version, for a draft. |
| `produced.number` | text or `null` | The customer-facing number, for an invoice. |

**`refused`**: nothing changed.

| Field | Type | Rule |
|---|---|---|
| `reasons` | list | At least one reason. Each is `{code, message}`: a stable code (for example `void-refused` or `proposal-refused`) and a sentence. |

**`conflict`**: the draft is not at the version the caller named, and
nothing changed. Review the current version, then send the command again
naming it.

| Field | Type | Rule |
|---|---|---|
| `currentVersion` | whole number | The draft's current version. |

Sending the same `issue-invoice` request again is safe. It returns the same
invoice and changes nothing (INV-CON-007).

## Versioning

- **Minor versions are additive.** A reader of 1.0 reads any `1.x` message.
  It ignores members that version adds, wherever they appear. In a `1.0`
  message, a member not listed here is refused.
- **A different major version is refused.** It is never guessed at.
  Version 2 would be a separate contract version, read beside this one.
- A new command or outcome kind needs a new version. A 1.0 reader refuses
  an unknown `kind`.

## Identity

The message never says who is acting.

- The host authenticates the caller and runs the command as that caller,
  with that caller's capabilities.
- A message cannot claim another identity.
- Summa records the authenticated actor in the audit trail of whatever the
  command changes.
