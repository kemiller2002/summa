# Configuring a Summa deployment

A deployment configures Summa with one document, `summa.deployment.json`.
Summa hard-codes no repository, owner, branch, base path, sign-in host or
client id (SUM-DATALOC-001, SUM0-003). `Summa.Storage.Deployment.parse`
reads and validates it.

```json
{
  "environment": "production",
  "environmentName": "production",
  "location": { "owner": "acme", "repository": "summa-data", "branch": "main", "basePath": "deployments/prod" },
  "identity": {
    "exchange": "https://fides.acme.example",
    "application": "summa-production",
    "provider": "github",
    "clientId": "Iv23li...",
    "redirectUri": "https://summa.acme.example/"
  },
  "organizations": [
    { "id": "org_acme", "displayName": "Acme Consulting", "slug": "acme", "defaultCurrency": "USD",
      "timeZone": "America/New_York", "administrators": [ "583231" ] },
    { "id": "org_eu", "displayName": "Acme Europe", "slug": "acme-eu", "defaultCurrency": "EUR",
      "timeZone": "Europe/Berlin", "administrators": [ "583231" ],
      "location": { "owner": "acme-eu", "repository": "summa-eu", "branch": "main", "basePath": "" } }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `environment` | yes | One of `local`, `test`, `staging` or `production`. Production data is never initialized in a public repository unless a reason is recorded (SUM3-040). |
| `environmentName` | yes | The environment's display name. |
| `location` | no | Where Summa's data lives, through Arca. Summa owns `<basePath>/summa` and nothing else (SUM-DATALOC-002, SUM0-043). |
| `identity` | with `location` | Sign-in through Fides (SUM0-003). `exchange` is an https origin. `clientId` is the GitHub App's public client id. `redirectUri` is the exact registered page. |
| `organizations` | with `location` | The organizations the deployment serves, the default one first. For each one: `id` is immutable and names its folder (SUM0-010). `slug` is lower case. `defaultCurrency` is an ISO 4217 code. `timeZone` is an IANA zone. `administrators` are the GitHub numeric account ids of its bootstrap administrators. `location` is optional and gives the organization its own repository (SUM0-009). |

The document is closed: Summa refuses any field it does not define. It
holds no secrets, and there is no field for one.

## Layout

| Path | Content |
|---|---|
| `<basePath>/summa/arca-manifest.json` | Arca's manifest for Summa's namespace |
| `<basePath>/summa/records/summa.application/summa.json` | Summa's application manifest (SUM0-007): name, id, storage version, record schemas, where organizations live, the oldest Summa version that may use the data |
| `<basePath>/summa/datasets/<id>/arca-manifest.json` | Arca's manifest for the organization's folder |
| `<basePath>/summa/datasets/<id>/records/summa.organization/<id>.json` | The organization manifest (SUM0-011): id, slug, display name, storage version, creation time, default currency, time zone, fiscal year start, invoice numbering and terms, accounting basis and default accounts |

Summa finds organizations from this document. It never scans the
repository (SUM0-033).

A folder is opened only when its Arca manifest matches the configured
namespace. Data that sits at another location must be migrated; Summa never
silently re-points to it. Initialization happens in one commit, and it never
overwrites an existing folder. A manifest change names the revision it was
read at, so a concurrent change becomes a conflict that has to be reloaded
and decided.

**Use a repository that belongs to Summa alone.** GitHub permissions apply
per repository, so a dedicated repository is the only way to keep Summa's
data separate from Chrona's and other applications' (SUM-DATALOC-004).
