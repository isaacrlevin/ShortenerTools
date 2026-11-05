# Production deployment setup

The ACA workflow uses the existing Aspire AppHost and renamed server-side
`ShortenerTools.Admin`. This setup does not deploy, change DNS, copy tables, or
enable scheduled posts. Azure identity/federation and GitHub environment inputs
are configured separately by the operator.

## Safe local configuration import

If the existing data account moves resource groups, update
`ProductionDeployment:ExistingStorageResourceGroup` in AppHost user secrets and
the GitHub `EXISTING_STORAGE_RESOURCE_GROUP` variable. The production data account
`shortenertooltfkc6sa` is now in `rg-shortener-production`, not
`LevinUrlShortener`. Deploy mode verifies the account is accessible in the
configured subscription/group before invoking Aspire; publish remains offline.

Use PowerShell 7, .NET 11, and Aspire CLI 13.6.1. Azure CLI and Docker with Linux
containers are needed for actual deployment; Azure CLI's local Bicep compiler is
used by the artifact checker. From the repository root:

```powershell
# Default: validate required inputs only, without invoking Aspire or Azure.
.\src\tools\Invoke-ProductionDeployment.ps1 `
    -LocalSettingsPath .\src\ShortenerTools.Functions\local.settings.json -LoadUserSecrets

# Nonprovisioning artifact generation with the same validated inputs.
.\src\tools\Invoke-ProductionDeployment.ps1 -Mode Publish `
    -LocalSettingsPath .\src\ShortenerTools.Functions\local.settings.json -LoadUserSecrets
.\src\tools\Test-AcaArtifacts.ps1 -ArtifactPath .\aspire-output

# Explicit provisioning/update; run only when intentionally deploying.
.\src\tools\Invoke-ProductionDeployment.ps1 -Mode Deploy `
    -LocalSettingsPath .\src\ShortenerTools.Functions\local.settings.json -LoadUserSecrets
```

`local.settings.json` is ignored by Git. The helper accepts a different
`-LocalSettingsPath` (including an old Functions checkout) and reads only its
unencrypted `Values` object. `-LoadUserSecrets` explicitly reads the AppHost's
local user-secret store using its `UserSecretsId`; it does not invoke or print
`dotnet user-secrets list`. Without that switch the secret store is not opened.
The helper creates no exported secret file, logs no values, and restores imported
environment variables in `finally`, including on failure. Aspire may maintain
private local deployment state; keep that state and published output out of Git
and artifact uploads. Aspire stdout/stderr
is suppressed because tool exception messages may contain credentials. Failure
reports the operation and exit code, not tool payloads. Do not enable shell
tracing, transcription, verbose configuration dumps, or upload deployment state.

Precedence: exact process environment key, portable underscore process key,
production-specific user-secret defaults (for domain, fallback and existing
storage), AppHost `Parameters:*` user-secret key, then Functions `Values`. The helper converts
portable names such as `Parameters__admin_api_key` to the exact `Parameters__admin-api-key`
for Aspire. **All inputs below are required**, including the email
integration even while the timer is disabled. Social account credentials (including
Threads and the optional X via handle) live in App Configuration, not here. Missing inputs fail before
publish/deploy; no deployment-time prompting or silently absent integrations.
URLs must be HTTPS, without embedded credentials, localhost/loopback addresses,
or reserved `.invalid` hosts.
`CustomDomain` is the full HTTPS short-link origin, without a trailing slash,
path, query, fragment, or custom port. The helper derives `redirect-domain-name`
from its hostname; an inherited hostname cannot override that derived value.

### Retaining the redirect custom domain on redeployment

The AppHost declares the redirect ingress binding with `ConfigureCustomDomain`.
Without this declaration, deploying its generated ingress configuration removes
custom domains added manually in the Azure portal. DNS validation and initial
managed certificate issuance remain operator steps; subsequent deployments reuse
that certificate rather than requesting a new one.

Supply the **managed certificate name**, not its resource ID, using
`ProductionDeployment:RedirectCertificateName` with `-LoadUserSecrets`,
`Parameters:redirect-certificate-name`, or
`Parameters__redirect_certificate_name`. For GitHub deployments, set the
production environment variable `REDIRECT_CERTIFICATE_NAME`. The current
`www.isaacl.dev` binding uses `www.isaacl.dev-rg-short-261008231256`.
Confirm the certificate still exists in the target ACA environment before using
that name. The helper requires this input and fails before deployment if missing,
rather than silently deploying an unbound production domain.

For direct Aspire production commands, also supply `redirect-domain-name` as the
bare hostname (for example, `www.isaacl.dev`), not `https://www.isaacl.dev`.
For a first-time environment with no certificate, bootstrap through direct
Aspire deployment with an empty certificate name, complete DNS validation and
certificate issuance in Azure, then configure the certificate name before using
the production helper. Staging and local runs do not use these binding inputs.

### Deployment-only AppHost user secrets / process environment

| User-secret key                              | Portable process environment                  |
|----------------------------------------------|-----------------------------------------------|
| `Azure:SubscriptionId`                       | `Azure__SubscriptionId`                       |
| `Azure:Location`                             | `Azure__Location`                             |
| `Azure:ResourceGroup`                        | `Azure__ResourceGroup`                        |
| `Parameters:existing-storage-account`        | `Parameters__existing_storage_account`        |
| `Parameters:existing-storage-resource-group` | `Parameters__existing_storage_resource_group` |
| `Parameters:entra-tenant-id`                 | `Parameters__entra_tenant_id`                 |
| `Parameters:entra-client-id`                 | `Parameters__entra_client_id`                 |
| `Parameters:entra-client-secret`             | `Parameters__entra_client_secret`             |
| `Parameters:administrator-object-id`         | `Parameters__administrator_object_id`         |
| `Parameters:redirect-certificate-name`       | `Parameters__redirect_certificate_name`       |

With `-LoadUserSecrets`, Azure target settings also fall back to
`ProductionDeployment:SubscriptionId`, `ProductionDeployment:Location`, and
`ProductionDeployment:ResourceGroup`. Existing process environment and `Azure:*`
keys take precedence. `ProductionDeployment:ClientId`, `PrincipalId`, and
`TenantId` are identity metadata, not imported as application credentials;
local deployment still needs intentional Azure authentication.

For safe production-specific defaults, these user secrets override the
corresponding local `Parameters:*` values without changing the local secret store:

| Production user-secret key                          | AppHost parameter                 |
|-----------------------------------------------------|-----------------------------------|
| `ProductionDeployment:CustomDomain`                 | `custom-domain`                   |
| `ProductionDeployment:RedirectCertificateName`      | `redirect-certificate-name`       |
| `ProductionDeployment:DefaultRedirectUrl`           | `default-redirect-url`            |
| `ProductionDeployment:ExistingStorageAccount`       | `existing-storage-account`        |
| `ProductionDeployment:ExistingStorageResourceGroup` | `existing-storage-resource-group` |

In particular, a local sign-in callback URL is not a production redirect fallback.
The coordinating operator configured the production defaults separately; local
development `Parameters:*` values remain untouched.

Use a local editor/secret manager for user secrets, not commands containing real
secret literals in shell history. `StagingDeployment:*`, `Storage:UseProduction`,
and `ConnectionStrings:production-data` are intentionally not imported.
Production attaches the existing account by name/resource group with managed
identity rather than copying a local storage connection string.

### Functions `Values` mapping

Each destination also accepts the equivalent AppHost user-secret key
`Parameters:<parameter>` and environment `Parameters__<parameter_with_underscores>`.

| Functions Values key                                     | AppHost parameter                                        |
|----------------------------------------------------------|----------------------------------------------------------|
| `AdminApiKey`                                            | `admin-api-key`                                          |
| `CustomDomain`                                           | `custom-domain`                                          |
| `DefaultRedirectUrl`                                     | `default-redirect-url`                                   |
| Azure App Configuration read/write connection string     | `social-accounts-connection-string`                      |
| Azure App Configuration read-only connection string      | `social-accounts-readonly-connection-string`             |
| `COMMUNICATION_SERVICES_CONNECTION_STRING`               | `communication-services-connection-string`               |
| `EmailFrom`                                              | `email-from`                                             |
| `EmailTo`                                                | `email-to`                                               |
| `DeploymentName`                                         | `azure-openai-deployment-name`                           |
| `AzureOpenAIEndpoint` + `AzureOpenAIKey`                 | Builds `ConnectionStrings:chat` using `Endpoint=…;Key=…` |

An existing chat connection takes precedence over endpoint/key construction:
`ConnectionStrings__chat` environment, then AppHost `ConnectionStrings:chat`
user secret, then Functions `Values["ConnectionStrings:chat"]`.
The Admin receives the `social-accounts-connection-string` parameter with read/write access. Functions receive only
`social-accounts-readonly-connection-string`. SocialPoster writes the four provider credentials to Azure App
Configuration and the Functions refresh those settings through its sentinel key.
The management API receives only the secret-reference `ConnectionStrings__chat`
and the explicit `DeploymentName`. Production never relies on the legacy
`gpt-4o-mini` default. Prompt, page and response content telemetry is disabled in
all environments; description failures log/return a generic message rather than
provider exception content. Staging receives no chat/social secrets and retains its
application-level external-posting and description-generation guards.

## GitHub production environment

### Diagnosing description generation

In the Aspire deployment, AI runs in `management-api`, not `admin`. The effective
settings are `ConnectionStrings__chat` (secret reference) and `DeploymentName`.
Adding legacy `AzureOpenAIEndpoint`/`AzureOpenAIKey` variables does not override
the Aspire chat connection. Check the management API's ready revision and logs
for failures, and the admin logs for HTTP/resilience timeouts. A direct successful
`/api/CreateDescription` request combined with an admin
`TimeoutRejectedException` at ten seconds indicates the HTTP client deadline,
not invalid Azure OpenAI credentials.

Description generation now uses a dedicated 120-second resilience request budget,
without automatically retrying POSTs. Other management
requests retain their normal deadlines. A timed-out generation displays an error
instead of terminating the Blazor circuit. Redeploy the admin to apply this fix;
do not enable the scheduler or change DNS for an AI timeout fix.

Create/configure `production` with deployment branch `main`; no required
reviewers/approval are requested. Store the following inputs (never paste values
into issues, logs or repository files):

| Kind                         | Names                                                                                                                                                                                                                                                                                 |
|------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Azure OIDC secrets           | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`                                                                                                                                                                                                                         |
| Application secrets          | `ENTRA_CLIENT_SECRET`, `ADMIN_API_KEY`, `AZURE_OPENAI_CONNECTION_STRING`                                                                                                                                                                                                              |
| Social account configuration | `SOCIAL_ACCOUNTS_CONFIG_READWRITE_CONNECTION_STRING`, `SOCIAL_ACCOUNTS_CONFIG_READONLY_CONNECTION_STRING`                                                                                                                                                                             |
| Other integration secrets    | `COMMUNICATION_SERVICES_CONNECTION_STRING`, `EMAIL_FROM`, `EMAIL_TO`                                                                                                                                                                                                 |
| Variables                    | `AZURE_LOCATION`, `AZURE_RESOURCE_GROUP`, `EXISTING_STORAGE_ACCOUNT`, `EXISTING_STORAGE_RESOURCE_GROUP`, `ENTRA_CLIENT_ID`, `ADMINISTRATOR_OBJECT_ID`, `SHORT_LINK_DOMAIN`, `REDIRECT_CERTIFICATE_NAME`, `DEFAULT_REDIRECT_URL`, `AZURE_OPENAI_DEPLOYMENT_NAME` |

`AZURE_CLIENT_ID` is the deployment identity, **not** `ENTRA_CLIENT_ID` (admin
sign-in). The workflow uses `AZURE_TENANT_ID` for both tenants. Federation subject:
`repo:isaacrlevin@8878502/ShortenerTools@1446234998:environment:production` (the repository uses GitHub's immutable, ID-based subject claim, so the owner and repository IDs are part of the subject). The identity needs
resource creation and role-assignment rights, including the existing data account.
The generated `main.bicep` is subscription-scoped and declares the target resource
group. The configured deployment principal therefore also has the custom
`ShortenerTools Deployment Bootstrap` role at subscription scope. This grants only
`Microsoft.Resources/deployments/*`, subscription reads, and resource-group reads.
It does not grant subscription-wide resource creation, role assignment, or data
access. Resource-write and role-assignment rights remain scoped to the dedicated
production group and existing data account. End-to-end GitHub OIDC deployment
authorization is verified only when the workflow is actually run.

### Setup status (October 8, 2026)

The coordinating operator reports that `isaacrlevin/ShortenerTools` now has a
`production` GitHub environment with no required reviewers and configured
deployment inputs. OIDC federation uses the environment subject above; the
deployment principal has Contributor and RBAC Administrator scoped to the new
`rg-shortener-production` resource group in `westus2` and the existing data
storage account only. The resource group is empty: no production apps have been
deployed. Existing storage and local Functions credentials are unchanged.
Subscription deployment orchestration is covered by the limited bootstrap role
described above; this setup is not a claim of verified production deployment.

Secrets may be loaded by an operator with `gh secret set NAME --env production --repo isaacrlevin/ShortenerTools`
using stdin from a secret manager, rather than `--body`/command-line literals.

The workflow validates builds/tests and Production/Staging Bicep on PR/push to `main`.
Deploy is manual, serialized, scoped to environment `production`, and allowed **only on `main`**. Branch dispatches
validate but skip deployment. The current
feature branch must be integrated into `main` and pushed before production
dispatch; creating this file locally does not make it available on GitHub.

```powershell
# Intentional production deployment; enables all four ACA timers.
gh workflow run azure-container-apps.yml --repo isaacrlevin/ShortenerTools --ref main `
    -f environment=production
```

There is no automatic production deploy on push and no DNS mutation in this
workflow. Every production action passes both scheduler enable switches, then
explicitly sets all four timer disabled flags to `false` in Azure and reads them
back. The action fails if the update or verification fails. This compensates for
observed Aspire module parameter resolution deploying disabled defaults despite
the enable switches. Legacy shortener and Coffee timers must remain disabled.
Direct helper invocations retain independent, default-disabled cutover switches.

## Scheduler cutover

The AppHost parameter `scheduler-disabled` defaults to string `true` and is
passed through to the standalone Container App module as
`scheduler_disabled_value`. Production Actions also enforce and verify the
enabled settings after Aspire deploy because module parameter resolution has
been observed to retain disabled defaults despite an explicit opt-in.
Local runs and Staging always hard-disable the timer regardless of the parameter.
Public redirects and management API always hard-disable their timer.

Only **after disabling the legacy live Functions timer**, intentionally deploy
with the production action or helper `-Mode Deploy -EnableScheduler`.
The action always enables all four timers. For direct helper deployments,
repeat the switch to request enablement; omitting it disables ACA scheduling.
Manual administrator posting is
still live in Production and can send real social/email messages; do not use it
as a harmless smoke test. See
the [cutover/rollback checklist](deployment.md#scheduler-cutover-and-rollback).

Coffee has its own `coffee-scheduler-disabled` parameter, which defaults to
string `true` and is passed through to the existing scheduler module as
`coffee_scheduler_disabled_value`. The two timer gates remain independent; this
is not a separate Coffee module or Container App.
`COSSTeaserTimer` runs `0 0 17 * * MON`, `COSSAnnouncementTimer` runs
`0 0 17 * * *`, and `COSSArchiveTimer` runs `0 0 16 * * MON` (UTC).
They share the existing internal one-replica scheduled-post container and
provider credentials, not another Coffee host or new secrets. Their source is
[`guests.json`](https://raw.githubusercontent.com/isaacrlevin/CoffeeAndOpenSource.com/main/data/guests.json).
The migrated Functions assembly replaces the standalone Coffee project in the
active solution/deployment.

Disable all three timers on the old Coffee host (or stop it and its deployment
automation) **before** running the production action or helper
`-EnableCoffeeScheduler`. The action always enables Coffee timers; direct helper
deployments still require that switch. The helper overwrites both
`Parameters__coffee-scheduler-disabled` and `Parameters__coffee_scheduler_disabled`
from the switch, ignoring inherited settings and clearing deployment cache.
`-EnableScheduler` does not affect Coffee; `-EnableCoffeeScheduler` does not
affect shortener posting. Omitting both in a direct helper deployment disables
both on redeploy; the production action always supplies both.

Local/Staging force all Coffee timers disabled and `PostSocials=false`.
Production scheduler and management set `PostSocials=true`, so Coffee sends
still require `ExternalPostingAllowed` and (for timers) the Coffee cutover.
Manual Coffee endpoints are protected by `AdminApiKey` in the
`X-Admin-Api-Key` header: `COSSPublishHttp` accepts POST with a JSON string guest
key; `COSSTeaserHttp`, `COSSAnnouncementHttp`, and `COSSArchiveHttp` use GET.
Production management ingress is public, but every management route requires
this key. These endpoints can send real production posts even when timers are
disabled; public redirects still allow only `UrlRedirect`. See the
[external automation example](management-api.md#examples)
for the HTTPS origin and a GitHub Actions call.

For Coffee rollback, redeploy with Coffee opt-in omitted, verify all ACA Coffee
timers disabled and in-flight calls drained, then restore the old Coffee host.
Never run both sets of timers concurrently; host IDs/leases do not provide
cross-host deduplication. Retain the old host's deployable revision/configuration
until cutover is proven; removing its project does not shut down deployed Azure
resources.

## Application Insights telemetry

The AppHost now includes workspace-based Application Insights (`app-insights`)
in **publish/deploy mode only**. Aspire creates the Insights component and its
Log Analytics workspace in the deployment resource group. Names are derived from
that group, so Production and Staging deployed to different resource groups have
separate telemetry; don't deploy both environments into the same group.

Aspire's resource reference supplies `APPLICATIONINSIGHTS_CONNECTION_STRING`
to all four Container Apps. No new workflow credentials, GitHub secrets, helper
inputs, or manually copied Insights connection string are needed. The existing
deployment principal's resource-group Contributor rights cover creation of
Insights and its workspace; existing subscription bootstrap/RBAC prerequisites
are unchanged. Keep publishing artifacts separate from actual deployment.

`ShortenerTools.ServiceDefaults` exports logs, traces and metrics through
`Azure.Monitor.OpenTelemetry.Exporter`, gated on a nonblank connection string.
The existing ASP.NET Core, HTTP client and runtime instrumentation is retained;
there is no second distro/instrumentation pipeline. The Functions host receives
`AzureFunctionsJobHost__telemetryMode=OpenTelemetry` and exports to that same
Insights resource. Worker `UseFunctionsWorkerDefaults()` captures invocation
spans, preserves host/worker correlation and advertises direct worker telemetry
so the host doesn't forward those logs a second time. The legacy Application
Insights SDK remains exclusively for non-Aspire Functions runs.

`OTEL_SERVICE_NAME` distinguishes the four roles:

- `admin`
- `management-api`
- `shortenertools-functions` (redirect)
- `shortenertools-scheduled-posts` (timer)

Local `aspire start` retains OTLP export to the Aspire dashboard and does not
create Insights resources or require an Insights secret. OTLP and Azure Monitor
are independently gated, so both destinations can be used if explicitly
configured. This change does not add browser/client-side telemetry.

### Where to see telemetry after deployment

Open the deployment resource group in the Azure portal, select its
Application Insights component (tag `aspire-resource-name=app-insights`), then
use **Failures**, **Performance**, **Application map**, or **Logs**. Allow a few
minutes after exercising the application for ingestion. Timer telemetry requires
an intentional scheduler cutover; don't enable scheduled posting just to test
monitoring. Live Metrics streaming is disabled in the worker exporter.

In **Application Insights → Logs**:

```kusto
requests
| where timestamp > ago(1h)
| summarize Requests=count(), Failures=countif(success == false) by cloud_RoleName
```

```kusto
union requests, dependencies, traces, exceptions
| where timestamp > ago(1h)
| where operation_Id == "<operation ID from a request>"
| order by timestamp asc
```

For logs and dependency failures:

```kusto
union traces, dependencies
| where timestamp > ago(1h)
| where cloud_RoleName == "management-api"
| order by timestamp desc
```

At the **Log Analytics workspace → Logs** scope, the corresponding table/column
names are `AppRequests`, `AppDependencies`, `AppTraces`, `AppExceptions`,
`TimeGenerated`, `AppRoleName`, and `OperationId`. Worker logs honor application
logging levels; host logs honor Functions host settings. Trace sampling can
reduce retained operations, so request counts here are not an authoritative
click ledger (the existing table-based click analytics remains unchanged).

Prompts, scraped content and model responses remain excluded:
`EnableSensitiveData=false`; AI instrumentation exports metadata only. HTTP
client header values are all redacted, no header/body enrichment is enabled,
and redirect logs no longer print destination URLs, referrers or user agents.
Request paths, dependency targets and existing application log fields can still
contain operational data: treat access/retention policies accordingly and never
add credentials or private content to log messages or URLs.

Application Insights/Log Analytics ingestion and retention are additional
billable usage, outside the ACA compute estimate. Review telemetry volume,
sampling, retention, budgets and daily caps after deployment; caps may stop
ingestion. The Azure Monitor exporter retains its default trace sampling rather
than enabling full-content or unsampled telemetry.

References: [Aspire Application Insights](https://aspire.dev/integrations/cloud/azure/azure-application-insights/),
[Functions host/worker OpenTelemetry](https://learn.microsoft.com/azure/azure-functions/opentelemetry-howto),
[Azure Monitor pricing](https://azure.microsoft.com/pricing/details/monitor/).

## Offline verification

```powershell
.\src\tools\Test-ProductionDeployment.ps1
dotnet build src\ShortenerTools.sln --configuration Release
dotnet test src\ShortenerTools.Tests\ShortenerTools.Tests.csproj --configuration Release --no-build
aspire publish --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj `
    --environment Production --non-interactive -o .\aspire-output
.\src\tools\Test-AcaArtifacts.ps1 -ArtifactPath .\aspire-output
aspire publish --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj `
    --environment Staging --non-interactive -o .\aspire-output-staging
.\src\tools\Test-AcaArtifacts.ps1 -ArtifactPath .\aspire-output-staging -DeploymentEnvironment Staging
```

The direct publish above deliberately needs no real deployment credentials. The
helper tests use synthetic fixtures and a mocked Aspire executable; they never
open the actual user-secret store or call the cloud. The checker asserts the
parameter-shaped timer, disabled module default, AI deployment/secret wiring,
independent Coffee gate/defaults, Coffee timer/HTTP allowlists and Staging blocks,
ingress, scaling, isolated function allowlists, storage references, a linked
Application Insights/workspace pair, all four generated connection references,
distinct telemetry roles, and host/worker OpenTelemetry settings. Application
tests cover exporter gating, OTLP coexistence, legacy SDK exclusion, worker
duplicate-log prevention capability, and header redaction without sending data.
