# Management API

The `management-api` Container App exposes the HTTP functions the admin site uses. You can call them yourself — from a
script, Postman, or a CI job — with the admin API key.

- [Base URL and authentication](#base-url-and-authentication)
- [Endpoint summary](#endpoint-summary)
- [Links](#links)
- [Statistics](#statistics)
- [AI descriptions](#ai-descriptions)
- [Post versions](#post-versions)
- [Coffee & Open Source](#coffee--open-source)
- [Diagnostic endpoints](#diagnostic-endpoints)
- [The redirect endpoint](#the-redirect-endpoint)
- [Examples](#examples)

## Base URL and authentication

| Environment | Base URL                                                                                                   |
|-------------|------------------------------------------------------------------------------------------------------------|
| Production  | `https://<management-api FQDN>` — `az containerapp show -n management-api -g <rg> --query properties.configuration.ingress.fqdn -o tsv` |
| Staging     | Internal only (`management-api.internal.<env domain>`); reachable from inside the ACA environment.         |
| Local       | The `management-api` endpoint shown in the Aspire dashboard.                                               |

Every request must include:

```http
X-Admin-Api-Key: <admin-api-key>
```

Missing or wrong key → `401 Authentication is required.` The key is compared in constant time. It is the `admin-api-key`
deployment parameter, **not** an Azure Functions host key (`AuthorizationLevel` is `Anonymous` on every function; the
middleware enforces the key instead).

The Functions route prefix is empty, so paths are used exactly as listed below (management routes include `/api`,
Coffee routes don't).

JSON property names are case-insensitive on input. Responses use the .NET default (PascalCase) names.

## Endpoint summary

| Function               | Method      | Path                                 | Purpose                                                    |
|------------------------|-------------|--------------------------------------|------------------------------------------------------------|
| `UrlList`              | GET         | `/api/UrlList`                       | Page/sort/filter active links                              |
| `UrlCreate`            | POST (GET)  | `/api/UrlCreate`                     | Create a short link                                        |
| `UrlCheck`             | GET         | `/api/UrlCheck?code=`                | Is a custom shortcode free to claim?                       |
| `UrlUpdate`            | POST        | `/api/UrlUpdate`                     | Change destination/title/message/posted flag               |
| `UrlArchive`           | POST        | `/api/UrlArchive`                    | Archive a link (stops redirecting)                         |
| `UrlStats`             | POST (GET)  | `/api/UrlStats`                      | Raw click records (all links or one)                       |
| `UrlClickStatsByDay`   | POST        | `/api/UrlClickStatsByDay`            | Daily click counts in a time zone                          |
| `UrlPostVersions`      | GET         | `/api/UrlPostVersions/{vanity}`      | Per-account post text history for a link                   |
| `CreateDescription`    | POST        | `/api/CreateDescription`             | AI-generated social post text for a URL                    |
| `ShortPostHttp`        | GET         | `/api/SchedulePost`                  | Run the shortener post routine (local/debug only)          |
| `TestShortUrl`         | GET         | `/test/{shortUrl}`                   | Post one link (debugger only)                              |
| `COSSPublishHttp`      | POST        | `/COSSPublishHttp`                   | Post a specific Coffee & Open Source episode               |
| `COSSTeaserHttp`       | GET         | `/COSSTeaserHttp`                    | Run the teaser routine now                                 |
| `COSSAnnouncementHttp` | GET         | `/COSSAnnouncementHttp`              | Run the announcement routine now                           |
| `COSSArchiveHttp`      | GET         | `/COSSArchiveHttp`                   | Run the archive routine now                                |

## Links

### `POST /api/UrlCreate`

```jsonc
{
  "url": "https://learn.microsoft.com/azure/container-apps/overview", // required, absolute http(s)
  "vanity": "aca",            // optional custom code; omit/blank to generate one
  "title": "Azure Container Apps overview",   // optional
  "message": "Serverless containers, explained.", // optional; used by the shortener timer
  "postToSocial": true,       // default true → Posted=false, i.e. queued for ShortPostTimer
  "creationId": "draft-123",  // optional idempotency token (≤128 chars)
  "posts": [                  // optional per-account text, stored in PostVersions
    { "account": "bluesky", "text": "New post about ACA {shorturl}" }
  ]
}
```

| Status | When                                                                                        |
|--------|---------------------------------------------------------------------------------------------|
| `200`  | `{ "ShortUrl": "https://short.example.com/aca", "LongUrl": "...", "Title": "...", "Message": "..." }` |
| `400`  | Empty/invalid URL (must be absolute `http`/`https`), invalid vanity, `creationId` > 128 chars |
| `409`  | The custom code already exists (including archived links)                                   |

- **Codes:** custom codes follow the [shortcode rules](architecture.md#data-model) (≤128 of `A–Z a–z 0–9 - _`,
  case-sensitive, `KEY`/`api`/`health`/`alive` reserved). Generated codes are base-36 encodings of an incrementing
  counter; collisions are retried up to 10 times.
- **Idempotency:** with no vanity and a `creationId` matching an existing link with the same URL, the existing link is
  returned instead of creating a new one. Per-account `posts` already stored with identical text are skipped.
- **Timer queue:** `postToSocial: true` (the default) stores `Posted=false`, so `ShortPostTimer` will post it on the next
  run in Production (if enabled). Send `false` if you only want a link.

### `GET /api/UrlCheck?code=<shortcode>`

Reports whether a custom code can still be claimed, using the same rules and the same lookup as `UrlCreate`. The admin
site calls it while you type a vanity in **Generate short link**. It is advisory: `UrlCreate` still returns `409` if
someone claims the code in between.

| Status | When                                                                                          |
|--------|-----------------------------------------------------------------------------------------------|
| `200`  | `{ "Available": true, "Message": "" }`, or `{ "Available": false, "Message": "<reason>" }` when the code is in use or belongs to an archived link |
| `400`  | `code` missing, or it breaks the shortcode rules (`{ "Message": "..." }`)                      |
| `500`  | Storage could not be read                                                                      |

### `GET /api/UrlList`

Returns `{ "UrlList": [ShortUrlEntity…], "TotalCount": n }` for **non-archived** links. Each entity has `Url`, `Title`,
`Message`, `ShortUrl` (built from `CustomDomain`), `Posted`, `IsArchived`, `PartitionKey`, `RowKey` (the code),
`Timestamp`, `CreationId`.

Two ways to query:

- Simple: `?skip=0&take=100`.
- Full: `?query=<URL-encoded JSON UrlListQuery>`:

```json
{
  "Skip": 0,
  "Take": 50,
  "Sorts":   [ { "Column": "Timestamp", "Descending": true } ],
  "Filters": [ { "Column": "Url", "Operator": "contains", "Value": "github.com" } ]
}
```

| Field        | Rules                                                                                                                   |
|--------------|-------------------------------------------------------------------------------------------------------------------------|
| `Take`       | 1–100.                                                                                                                  |
| `Column`     | `Timestamp`, `RowKey`, `Title`, `Url`.                                                                                  |
| Text operators | `contains`, `not contains`, `equals`, `not equals`, `starts with`, `ends with`, `is empty`, `is not empty` (case-insensitive). |
| `Timestamp` operators | `is`, `is not`, `is after`, `is on or after`, `is before`, `is on or before`, `is empty`, `is not empty`; `Value` is a round-trip date/time; comparisons are by date. |

Default order (no sorts) is newest first. Table Storage can't sort server-side, so the function scans active links and
pages in memory; expect it to slow down as the table grows.

### `POST /api/UrlUpdate`

```json
{ "PartitionKey": "a", "RowKey": "aca", "Url": "https://new-destination.example", "Title": "New title", "Message": "", "Posted": true }
```

Updates `Url`, `Title`, `Message` and `Posted` of an existing link and returns the stored entity. `404` if the link
doesn't exist; `400` for an empty/invalid URL. `PartitionKey` is the first character of the code.

### `POST /api/UrlArchive`

```json
{ "PartitionKey": "a", "RowKey": "aca" }
```

Sets `IsArchived=true` and returns the entity. Archived links redirect to `DefaultRedirectUrl` and their codes can't be
reused.

## Statistics

### `POST /api/UrlStats`

Body `{ "Vanity": "aca" }` for one link, or `{}` / no vanity for all. Returns
`{ "ClickStatsList": [ ClickStatsEntity… ] }`. Each record includes `Datetime`, `ShortUrl`, `Page`, `Host`,
`ReferrerUrl`, `ReferrerHost`, `Agent`, `Browser`/`BrowserWithVersion`, `Platform`/`PlatformWithVersion`, `IsDesktop`,
`IsMobile`, `IsCrawler`, mobile device fields, and `Campaign`. This returns every matching row — for all links that can be
large; prefer `UrlClickStatsByDay` for charts.

### `POST /api/UrlClickStatsByDay`

```json
{ "Vanity": "aca", "StartDate": "2026-10-01", "EndDate": "2026-10-31", "TimeZoneId": "America/Los_Angeles", "ExcludeCrawlers": true }
```

All fields optional. Dates are inclusive `yyyy-MM-dd` in the given time zone (server local if omitted).
`ExcludeCrawlers` (default `false`) leaves out clicks flagged `IsCrawler` (link-preview fetchers and recognised crawlers);
rows recorded before that flag existed are always counted. Returns
`{ "Items": [ { "DateClicked": "...", "Count": 3 } ], "Url": "https://short.example.com/aca" }`, ascending by date.
`400` for an invalid date/zone; `500` with a generic message if aggregation fails.

## AI descriptions

### `POST /api/CreateDescription`

```json
{ "Url": "https://example.com/post", "Title": "Optional title" }
```

Returns a JSON **string** with suggested post text sized to leave room for the title and short URL. Uses Azure OpenAI in
Azure, Ollama locally.

| Status | When                                                                      |
|--------|---------------------------------------------------------------------------|
| `200`  | Generated text                                                            |
| `400`  | Bad URL, or generation failed (`{ "Message": "Description generation failed." }`) |
| `404`  | Missing `Url`                                                             |
| `503`  | AI not configured in this environment (always in Staging)                |

Generation can take tens of seconds; the admin allows 120 s.

## Post versions

### `GET /api/UrlPostVersions/{vanity}[?account=<account>]`

Returns an array of `{ Account, Text, Url, Status, ExternalPostId, CreatedBy, IsLegacy, CreatedAt, RowKey }` for the
link, optionally filtered to one account (e.g. `bluesky` or `bluesky:main`). The timer posts the newest non-legacy version
per platform, replacing `{shorturl}` with the short URL.

## Coffee & Open Source

These post to **real** accounts in Production (requires `PostSocials=true` and external posting allowed). They do nothing
in Local/Staging. Timers do not need to be enabled for them to work.

| Endpoint                      | Body / behaviour                                                                                         |
|-------------------------------|----------------------------------------------------------------------------------------------------------|
| `POST /COSSPublishHttp`       | Body is a **JSON string**: the guest key, e.g. `"jane-doe"`. Posts the "episode published" message for that published guest. `403` if posting isn't allowed, `400` for a bad body, `404` if no published guest has that key, `200 "Complete"` on success. |
| `GET /COSSTeaserHttp`         | Posts a teaser for a guest airing within 5 days, if any.                                                 |
| `GET /COSSAnnouncementHttp`   | Posts an announcement for a guest airing within 3 hours, if any.                                         |
| `GET /COSSArchiveHttp`        | Re-shares a random published episode.                                                                   |

Guest data: [`guests.json`](https://raw.githubusercontent.com/isaacrlevin/CoffeeAndOpenSource.com/main/data/guests.json).

## Diagnostic endpoints

- **`GET /api/SchedulePost`** — runs "post the oldest unposted link" with `force`, but only when a debugger is attached
  or in a non-Production Aspire run. In Production it does nothing. In a dry run it still marks the link `Posted=true`.
- **`GET /test/{shortUrl}`** — posts one link (no Threads), only with a debugger attached and posting allowed.

## The redirect endpoint

Served by `shortenertools-functions`, not the management API, and the only anonymous route:

| Request                         | Response                                                                       |
|---------------------------------|--------------------------------------------------------------------------------|
| `GET /{code}` (active)          | `302` to the destination; a `ClickStats` row is written                        |
| `GET /{code}` (unknown/archived)| `302` to `DefaultRedirectUrl`                                                  |
| User agent contains `bot` (except link-preview fetchers) | `200` robots text, no click recorded                  |
| Link-preview fetcher (Twitterbot, LinkedInBot, facebookexternalhit, Bluesky Cardyb, SocialPoster) | `302` to the destination; click recorded with `IsCrawler=true` |
| `GET /robots.txt`, any path with `.` | `200` robots text (allows the link-preview fetchers, disallows others)    |

## Examples

PowerShell:

```powershell
$base    = 'https://<management-api-fqdn>'
$headers = @{ 'X-Admin-Api-Key' = $env:ADMIN_API_KEY }

# Create a link that should NOT be picked up by the timer
$body = @{ url = 'https://example.com/article'; vanity = 'article'; title = 'An article'; postToSocial = $false } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/api/UrlCreate" -Headers $headers -ContentType 'application/json' -Body $body

# Ten newest links
$q = [uri]::EscapeDataString((@{ Skip = 0; Take = 10 } | ConvertTo-Json -Compress))
(Invoke-RestMethod -Uri "$base/api/UrlList?query=$q" -Headers $headers).UrlList | Select-Object RowKey, Url, Title

# Daily clicks for October in Pacific time
$body = @{ Vanity = 'article'; StartDate = '2026-10-01'; EndDate = '2026-10-31'; TimeZoneId = 'America/Los_Angeles' } | ConvertTo-Json
(Invoke-RestMethod -Method Post -Uri "$base/api/UrlClickStatsByDay" -Headers $headers -ContentType 'application/json' -Body $body).Items
```

curl:

```bash
curl --fail-with-body -X POST "$BASE/api/UrlArchive" \
  -H "X-Admin-Api-Key: $ADMIN_API_KEY" -H "Content-Type: application/json" \
  -d '{"PartitionKey":"a","RowKey":"article"}'
```

GitHub Actions (publish a Coffee episode from another repository, keeping the key in a secret):

```yaml
- name: Publish Coffee guest
  env:
    MANAGEMENT_API_URL: ${{ vars.MANAGEMENT_API_URL }}
    ADMIN_API_KEY: ${{ secrets.ADMIN_API_KEY }}
    GUEST_KEY: jane-doe
  run: |
    curl --fail-with-body --request POST "$MANAGEMENT_API_URL/COSSPublishHttp" \
      --header "X-Admin-Api-Key: $ADMIN_API_KEY" \
      --header "Content-Type: application/json" \
      --data "\"$GUEST_KEY\""
```

Never put the API key in a URL or log it.
