# Operations

Day-two tasks for a deployed environment: checking it works, watching it, switching timers, rotating secrets, backing up,
and fixing common problems.

- [Handy variables](#handy-variables)
- [Post-deployment verification](#post-deployment-verification)
- [Monitoring](#monitoring)
- [Timers](#timers)
- [Rotating secrets](#rotating-secrets)
- [Backing up SocialPoster data](#backing-up-socialposter-data)
- [Restarting an app](#restarting-an-app)
- [Troubleshooting](#troubleshooting)
- [Costs and teardown](#costs-and-teardown)

## Handy variables

```powershell
$rg = 'rg-shortener-production'
function Fqdn($app) { az containerapp show -n $app -g $rg --query properties.configuration.ingress.fqdn -o tsv }
$redirect = "https://$(Fqdn shortenertools-functions)"
$api      = "https://$(Fqdn management-api)"
$admin    = "https://$(Fqdn admin)"
```

## Post-deployment verification

All of these are **read-only or harmless**. Do not use the Coffee endpoints, `Publish` in the admin, or enable timers as a
"smoke test" — those post for real.

| # | Check                                         | How                                                                                                         | Expect                                         |
|---|-----------------------------------------------|-------------------------------------------------------------------------------------------------------------|------------------------------------------------|
| 1 | All apps running, one replica each            | `az containerapp list -g $rg --query "[].{n:name,state:properties.runningStatus}" -o table`                   | `Running`                                      |
| 2 | Unknown code falls back                       | `curl -sI "$redirect/definitely-not-a-code"`                                                                 | `302`, `Location: <DefaultRedirectUrl>`       |
| 3 | Known code redirects and counts               | Open `https://<short domain>/<existing code>`, then check Statistics in the admin                            | Destination loads; click appears              |
| 4 | Bots blocked, preview fetchers let through    | `curl -s -A "testbot" "$redirect/<code>"`, then `curl -sI -A "Twitterbot/1.0" "$redirect/<code>"`            | `200` robots text; then `302` to destination  |
| 5 | Redirect app has no management routes         | `curl -s -o NUL -w "%{http_code}" "$redirect/api/UrlList"`                                                   | `404` (route not loaded), never JSON          |
| 6 | Management API requires the key               | `curl -s -o NUL -w "%{http_code}" "$api/api/UrlList"`                                                        | `401`                                         |
| 7 | Management API works with the key             | `curl -s -H "X-Admin-Api-Key: $env:ADMIN_API_KEY" "$api/api/UrlList?take=1"`                                 | JSON with `UrlList`                           |
| 8 | Scheduler is internal                         | `az containerapp show -n shortenertools-scheduled-posts -g $rg --query properties.configuration.ingress.external` | `false`                                   |
| 9 | Admin sign-in                                 | Open `$admin`, sign in with the administrator account                                                       | SocialPoster loads                             |
| 10 | Other accounts rejected                      | Sign in with any other account (private window)                                                             | `/unauthorized`, no session                    |
| 11 | Sign-out                                     | Sign out from the menu                                                                                      | Returns to sign-in                             |
| 12 | Link management                              | Create a test link with *Stage for timer* **off**, edit it, archive it                                      | Works; archived link falls back               |
| 13 | AI drafting                                  | Compose → enter a URL → *Generate*                                                                          | Draft text within ~a minute                    |
| 14 | Custom domain + certificate                  | `curl -sI https://<short domain>/x`                                                                         | Valid TLS, `302`                              |
| 15 | Timer state is what you intended             | See [Timers](#timers)                                                                                       | `true`/`false` as planned                     |
| 16 | Telemetry arriving                           | App Insights → Logs: `requests \| summarize count() by cloud_RoleName` (after a few minutes)                | Rows for each app you exercised               |

## Monitoring

### Live console logs

```powershell
az containerapp logs show -n management-api -g $rg --follow --tail 100            # app output
az containerapp logs show -n shortenertools-scheduled-posts -g $rg --type system   # revisions, restarts, mounts
```

The Aspire dashboard deployed into the environment also shows resources and logs; find its URL in the ACA environment
blade (*Aspire Dashboard*).

### Application Insights

Open the resource group → the Application Insights component tagged `aspire-resource-name=app-insights` → **Failures**,
**Performance**, **Application map** or **Logs**. Each app reports as its own `cloud_RoleName`:
`admin`, `management-api`, `shortenertools-functions`, `shortenertools-scheduled-posts`.

Requests and failures per app (last hour):

```kusto
requests
| where timestamp > ago(1h)
| summarize Requests = count(), Failures = countif(success == false) by cloud_RoleName
```

One operation end to end:

```kusto
union requests, dependencies, traces, exceptions
| where timestamp > ago(1h)
| where operation_Id == "<operation id>"
| order by timestamp asc
```

Did the timers fire and post?

```kusto
union requests, traces, exceptions
| where timestamp > ago(7d)
| where cloud_RoleName == "shortenertools-scheduled-posts"
| where operation_Name in ("ShortPostTimer", "COSSTeaserTimer", "COSSAnnouncementTimer", "COSSArchiveTimer")
     or message has "Error when posting"
| project timestamp, itemType, operation_Name, message, success
| order by timestamp desc
```

Posting errors from any app:

```kusto
traces
| where timestamp > ago(7d)
| where message startswith "Error when posting"
| project timestamp, cloud_RoleName, message
```

In the Log Analytics workspace scope the tables are `AppRequests`, `AppDependencies`, `AppTraces`, `AppExceptions`
(columns `TimeGenerated`, `AppRoleName`, `OperationId`).

Notes:

- Sampling is on, so request counts are not a click ledger; use the admin **Statistics** (table data) for clicks.
- Prompts, page content and AI responses are never exported; HTTP header values are redacted; redirect logs omit
  destinations, referrers and user agents. Paths and dependency targets can still appear — treat log access accordingly.
- Ingestion is billable. Consider a daily cap and a retention policy on the workspace.

Every posting failure is also emailed to `EmailTo` with the exception details.

## Timers

Check:

```powershell
az containerapp show -n shortenertools-scheduled-posts -g $rg `
  --query "properties.template.containers[0].env[?starts_with(name,'AzureWebJobs.')].{name:name,value:value}" -o table
```

Change immediately (creates a new revision; survives until the next deploy):

```powershell
# Disable everything now
az containerapp update -n shortenertools-scheduled-posts -g $rg --set-env-vars `
  AzureWebJobs.ShortPostTimer.Disabled=true AzureWebJobs.COSSTeaserTimer.Disabled=true `
  AzureWebJobs.COSSAnnouncementTimer.Disabled=true AzureWebJobs.COSSArchiveTimer.Disabled=true
```

The next deployment resets them: the GitHub workflow enables all four; the helper sets them from its switches. To make a
change stick, deploy the way you intend to keep deploying. See [cutover and rollback](deployment.md#scheduler-cutover-and-rollback).

Schedules (UTC): shortener weekdays 13:00, 16:00, 19:00, 23:00; Coffee teaser Mondays 17:00, announcement daily 17:00,
archive Mondays 16:00. The shortener timer posts **one** link per run (oldest unposted), so a backlog drains at up to 20
links per week.

## Rotating secrets

Container App secrets are rewritten from the deployment parameters on every deploy, so always update the **source**
(GitHub secret/variable or user secret) and then redeploy. For an urgent change you can also set the secret directly and
restart, but update the source as well or the next deploy will revert it.

| Secret                               | Where it's used                        | Rotate by                                                                                                       |
|--------------------------------------|----------------------------------------|-----------------------------------------------------------------------------------------------------------------|
| Entra client secret                  | admin                                  | New secret in the app registration → `ENTRA_CLIENT_SECRET` → redeploy → delete the old secret. Track the expiry date. |
| Admin API key                        | admin, management-api, your automation | New random value → `ADMIN_API_KEY` → redeploy (both apps update together) → update automation callers.        |
| App Configuration keys               | admin (rw), Functions (ro)             | `az appconfig credential regenerate` for the key you're not using, update both `SOCIAL_ACCOUNTS_CONFIG_*` secrets, redeploy, then regenerate the other. |
| Azure OpenAI key                     | management-api                         | Regenerate key 2, update `AZURE_OPENAI_CONNECTION_STRING`, redeploy, regenerate key 1.                        |
| ACS connection string                | management-api, scheduler              | Same two-key pattern; `COMMUNICATION_SERVICES_CONNECTION_STRING`.                                              |
| X / Bluesky / LinkedIn / Mastodon / Threads | via App Configuration           | Update in SocialPoster → Settings. Synced within seconds; no deploy needed. Threads long-lived tokens expire, so reconnect before expiry.                                    |

Direct secret update (emergency):

```powershell
az containerapp secret list -n admin -g $rg -o table            # see secret names
az containerapp secret set  -n admin -g $rg --secrets "entra--clientsecret=<new value>"
az containerapp revision restart -n admin -g $rg --revision (az containerapp revision list -n admin -g $rg --query "[?properties.active].name | [0]" -o tsv)
```

Note: the production workflow enables all timers on every run; if you've rotated a secret before cutover, redeploy with the
helper instead.

## Backing up SocialPoster data

The `socialposter-data` Azure Files share holds the SocialPoster SQLite database, uploaded media and the Data Protection
keys needed to decrypt stored account tokens. Back up the whole share, including `keys/`, together.

```powershell
$acct = az storage account list -g $rg --query "[?starts_with(name,'socialposter')].name | [0]" -o tsv
$key  = az storage account keys list -n $acct -g $rg --query "[0].value" -o tsv

# Point-in-time snapshot (stays in Azure; restore from the portal)
az storage share snapshot --name socialposter-data --account-name $acct --account-key $key

# Download a copy
az storage file download-batch --source socialposter-data --destination .\socialposter-backup `
    --account-name $acct --account-key $key
```

The admin writes to SQLite continuously; a snapshot is crash-consistent, which SQLite tolerates. For a fully quiescent
copy, take it while nothing is being scheduled. Enabling Azure Backup for Azure Files on that account gives you scheduled
snapshots.

To restore into a new environment, upload the files into the new share before (or while) the admin is stopped, keeping
`keys/` with the database.

## Restarting an app

```powershell
$app = 'admin'
$rev = az containerapp revision list -n $app -g $rg --query "[?properties.active].name | [0]" -o tsv
az containerapp revision restart -n $app -g $rg --revision $rev
```

Each app has a single replica, so a restart means a short outage for that app (redirects for `shortenertools-functions`).

## Troubleshooting

| Symptom                                                                 | Where to look / fix                                                                                                                       |
|-------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|
| Every short link goes to the fallback URL                               | Link archived? `tables__tableServiceUri` points at the right account? Redirect identity has *Storage Table Data Contributor* on the data account? Check redirect logs for storage `403`s. |
| TLS error or 404 on the short-link domain                               | Custom domain binding lost — `redirect-certificate-name` empty/wrong on the last deploy, or DNS changed. See [Custom domain](deployment.md#custom-domain-and-certificate). |
| Admin: `AADSTS50011` redirect URI mismatch                              | Add `https://<admin fqdn>/signin-oidc` (exact host) to the app registration.                                                            |
| Admin: always `/unauthorized`                                           | Signed-in account's `oid`/`tid` don't match `administrator-object-id`/`entra-tenant-id`. Guests have a different object ID in your tenant. |
| Admin: "SocialPoster is starting. Refresh in a few seconds."            | SQLite store still opening (up to ~15 s). If persistent, check system logs for the Azure Files mount.                                   |
| SocialPoster accounts/history gone after a deploy                       | The share wasn't mounted (`SocialPoster__DataDirectory` should be `/mnt/socialposter-data`). Restore from backup.                        |
| "database is locked" errors                                             | More than one admin replica, or another process using the share. Admin must be exactly one replica.                                      |
| "The short link could not be created. Nothing was published or scheduled." | The message after it explains why: `409` (code taken), validation error, or the management API is down/restarting.                 |
| AI *Generate* times out                                                 | AI runs in `management-api`, not admin. Check its logs and `DeploymentName`. A `TimeoutRejectedException` in admin logs while a direct `/api/CreateDescription` call works means the HTTP budget (120 s), not credentials. |
| `CreateDescription` returns `503`                                       | AI not registered: Staging, or `EnableDescriptionGeneration=false`.                                                                    |
| Timers never post                                                       | Timers disabled? (see [Timers](#timers)). Nothing unposted (`Posted=false`) in `UrlsDetails`? `ExternalPostingAllowed` false (Staging)? |
| Posts to some networks fail                                             | Scheduler logs / error emails; credentials in App Configuration (`Shortener:*` keys) present and current?                               |
| X posts arrive as emails instead                                        | At least one of the four X API credentials is missing in App Configuration — by design, an intent link is emailed.                      |
| Threads posts fail with "Connect Threads under Accounts first" | `Shortener:ThreadsToken` or `Shortener:ThreadsUserId` is empty in App Configuration. Connect (or reconnect) Threads in SocialPoster → Settings; the admin syncs both. |
| Threads post has no link card                                           | Check `SocialPoster.Posting` is 0.6.6 or later in `src/Directory.Packages.props` (older versions don't send `link_attachment`). |
| Link card shows no image or title (any network)                         | The card is built by fetching the short link, and the redirect app only lets the fetchers in `UrlRedirect.LinkPreviewAgents` through. If a network's fetcher uses another user agent containing `bot`, add it there. Also open the destination directly to confirm it has Open Graph tags. See [Link previews](architecture.md#link-previews). |
| A new post's statistics start with a few clicks nobody made              | Link-preview fetches are recorded as clicks, flagged `IsCrawler=true` in `ClickStats`. Expected; see [Link previews](architecture.md#link-previews). |
| Credentials changed in SocialPoster but Functions still use old ones    | Admin needs the **read/write** App Configuration string; Functions need the read-only one. Check admin logs for "App Configuration sync is disabled" or the empty-store guard. |
| Coffee endpoint returns `403`                                           | `PostSocials`/external posting not allowed in this environment (expected outside Production).                                          |
| No telemetry                                                            | Wait a few minutes; confirm `APPLICATIONINSIGHTS_CONNECTION_STRING` is set on the app; check sampling and any daily cap.               |
| Management API answers `401` to the admin                               | Admin and API got different `AdminApiKey` values (manual secret edit on one app). Redeploy.                                            |

## Costs and teardown

See [Azure resources → Cost overview](azure-resources.md#cost-overview) for what is billed, and
[Deployment → Tearing down](deployment.md#tearing-down) to remove an environment safely.
