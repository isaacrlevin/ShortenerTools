# Local development

Run the whole system on your machine with .NET Aspire: storage emulators, a local LLM, the three Functions apps, the admin
site and the Aspire dashboard — all from one command.

- [What runs locally](#what-runs-locally)
- [Prerequisites](#prerequisites)
- [1. Create an Entra app registration for local sign-in](#1-create-an-entra-app-registration-for-local-sign-in)
- [2. Configure AppHost user secrets](#2-configure-apphost-user-secrets)
- [3. Run](#3-run)
- [Launch profiles](#launch-profiles)
- [Debugging in an IDE](#debugging-in-an-ide)
- [Working against production data](#working-against-production-data)
- [AI options](#ai-options)
- [Testing social posting safely](#testing-social-posting-safely)
- [Running tests](#running-tests)
- [Local troubleshooting](#local-troubleshooting)

## What runs locally

| Resource (dashboard name)          | What it is                                                                 |
|------------------------------------|----------------------------------------------------------------------------|
| `storage` / `tables`               | Azurite emulator holding `UrlsDetails`, `ClickStats`, `PostVersions`.      |
| `functions-host`                   | Second Azurite emulator for Functions runtime state.                       |
| `ollama` + `chat` model            | Ollama container (data volume) pulling `llama3`, plus Open WebUI.         |
| `shortenertools-functions`         | Redirect app (`UrlRedirect`).                                              |
| `management-api`                   | Management API (dry-run: `DisableExternalPosting=true`).                   |
| `shortenertools-scheduled-posts`   | Timers — all forced **disabled** locally.                                 |
| `admin`                            | Blazor admin + SocialPoster on `https://localhost:5001`.                  |

Nothing is provisioned in Azure, and no Application Insights is used: telemetry goes to the Aspire dashboard over OTLP.

## Prerequisites

- A .NET SDK that can build the `TargetFramework` set in `src/Directory.Build.props`.
- [Aspire CLI](https://aspire.dev) 13.6.1: `dotnet tool install --global Aspire.Cli --version 13.6.1`.
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local) (`func`), used by
  Aspire to host the Functions projects.
- Docker Desktop running **Linux containers** (Azurite, Ollama).
- Disk space for the `llama3` model (several GB) on the first run, unless you [use Azure OpenAI](#ai-options).

## 1. Create an Entra app registration for local sign-in

The admin always uses real Microsoft Entra sign-in (there is no mock mode). You can reuse the production registration or
create a dedicated one:

1. Entra ID → App registrations → **New registration**, single tenant, platform **Web**.
2. Redirect URI `https://localhost:5001/signin-oidc`; front-channel logout URL
   `https://localhost:5001/signout-callback-oidc`.
3. Create a client secret and copy its **value**.
4. Note the tenant ID, client ID, and your user's object ID (`az ad signed-in-user show --query id -o tsv`).

## 2. Configure AppHost user secrets

The AppHost's `UserSecretsId` is `b32025b0-98ec-4cbe-8f4e-a49302d9d300`. Required for a local run:

```powershell
$p = 'src\ShortenerTools.AppHost'
dotnet user-secrets set "Parameters:entra-tenant-id"         "<tenant-guid>"      --project $p
dotnet user-secrets set "Parameters:entra-client-id"         "<client-guid>"      --project $p
dotnet user-secrets set "Parameters:administrator-object-id" "<your-object-guid>" --project $p
dotnet user-secrets set "Parameters:custom-domain"           "https://localhost:5001" --project $p
dotnet user-secrets set "Parameters:default-redirect-url"    "https://example.com"    --project $p
```

Set the two secrets — `Parameters:entra-client-secret` and `Parameters:admin-api-key` — through your IDE's *Manage User
Secrets* or by editing `secrets.json`, so their values don't land in shell history. Any random string works for the API
key locally.

If you skip a required parameter, the Aspire dashboard shows an "unresolved parameters" prompt where you can enter it.

`custom-domain` only affects how short URLs are *displayed*/posted locally; it isn't used for routing. The admin will
refuse to start if any `Entra:*` value is not a GUID or the client secret / API key is empty.

Optional extras are listed in the [Configuration reference](configuration.md#example-user-secrets-file), for example the
App Configuration connection strings to test credential sync across processes.

## 3. Run

```powershell
aspire start --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj --launch-profile https
aspire wait admin --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj
```

1. Open the dashboard URL printed by the CLI.
2. Click the **admin** HTTPS endpoint and sign in with the account whose object ID you configured.
3. Create a link in **Links**, then open the redirect app's endpoint + `/<code>` to try a redirect and see the click in
   **Statistics**.

Stop everything with:

```powershell
aspire stop --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj
```

The first start downloads `llama3`; the management API waits for the model (`WaitFor(chat)`), but the redirect and admin
start immediately.

## Launch profiles

Defined in `src/ShortenerTools.AppHost/Properties/launchSettings.json`. The dashboard is on `https://localhost:17120`.

| Profile                  | Transport | Data                                                       |
|--------------------------|-----------|------------------------------------------------------------|
| `https`                  | HTTPS     | Azurite                                                    |
| `http`                   | HTTP (`ASPIRE_ALLOW_UNSECURED_TRANSPORT`) | Azurite                    |
| `https-production-data`  | HTTPS dashboard, unsecured transport allowed | **Live production tables** (`Storage__UseProduction=true`) |

## Debugging in an IDE

- **Visual Studio:** open `src/shortenerTools.sln`, set `ShortenerTools.AppHost` as the startup project, choose the
  `https` profile, press F5. Breakpoints work in the Functions and admin processes (this is Blazor Server, not WASM).
- **Rider:** use an **Aspire Host** run configuration for `ShortenerTools.AppHost` (not the admin's own profile). If the
  dashboard comes up but `admin` never logs anything / has PID 0, start with the CLI instead and use
  *Run → Attach to Process* on `ShortenerTools.Admin`. More detail in the
  [admin README](../src/ShortenerTools.Admin/README.md#rider).
- **CLI:** `aspire start` runs the processes but doesn't attach a debugger.

The Functions projects copy `host.json` to output so the HTTP route prefix stays empty. If you see routes like
`/api/api/UrlList` in the startup logs, rebuild and restart the Functions resource.

## Working against production data

Use this to look at real statistics or fix a link without deploying anything.

```powershell
dotnet user-secrets set "ConnectionStrings:production-data" "<URL data account connection string>" --project src\ShortenerTools.AppHost
aspire start --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj --launch-profile https-production-data
```

Get the connection string from the **URL data** account's *Access keys* blade (not the Functions host account). Only the
redirect app and the management API switch to live data; the scheduler and host state stay on Azurite, and the scheduler
remains disabled.

> [!WARNING]
> This is live data with write access. Creating, editing or archiving links changes production, and following a short
> link through the local redirect app records a real click. If the secret is missing, the `production-data` resource is
> marked missing and the Functions apps won't start.

## AI options

| Want                       | Configure                                                                                                                   |
|----------------------------|-----------------------------------------------------------------------------------------------------------------------------|
| Local Ollama (default)     | Nothing. CPU by default; `Ollama:UseGpu=true` adds `--gpus=all` if your Docker supports NVIDIA GPUs.                       |
| Azure OpenAI instead       | `Ollama:UseLocalModel=false`, `ConnectionStrings:chat=Endpoint=https://…;Key=…`, optionally `Parameters:azure-openai-deployment-name` (defaults to `gpt-4o-mini` locally). |

AI is used by `POST /api/CreateDescription`. Prompt and response content are never written to telemetry.

## Testing social posting safely

Locally the management API runs with `DisableExternalPosting=true`, and all timers are disabled, so nothing reaches real
accounts. A few things to know:

- **`GET /api/SchedulePost`** (the `ShortPostHttp` function) runs the shortener's "post the oldest unposted link" routine
  on demand in a local Aspire run. Sending is skipped (dry run), **but the link is still marked `Posted=true`**. Against
  `https-production-data` that removes the link from the production timer queue.
- **Coffee endpoints** do nothing locally because `PostSocials=false` (`COSSPublishHttp` returns `403`).
- **SocialPoster** (in the admin) has its own posting path that the shortener's dry-run guards don't cover: accounts you
  add in *Settings* are real, so treat **Publish** and **Schedule** as live, even locally. Use test accounts.
- SocialPoster stores its local database and Data Protection keys in `src/ShortenerTools.Admin/socialposter-data/`. Keep
  that folder out of source control; delete it to start fresh.

## Running tests

```powershell
dotnet build src\shortenerTools.sln --configuration Release
dotnet test  src\ShortenerTools.Tests\ShortenerTools.Tests.csproj --configuration Release --no-build
node src\tools\Test-SocialPosterLinks.cjs
.\src\tools\Test-ProductionDeployment.ps1

# Infrastructure policy checks (needs Azure CLI + Bicep, no login)
aspire publish --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj --environment Production --non-interactive -o .\aspire-output
.\src\tools\Test-AcaArtifacts.ps1 -ArtifactPath .\aspire-output
aspire publish --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj --environment Staging --non-interactive -o .\aspire-output-staging
.\src\tools\Test-AcaArtifacts.ps1 -ArtifactPath .\aspire-output-staging -DeploymentEnvironment Staging
```

This is the same sequence the CI `validate` job runs. Keep `aspire-output*` folders out of Git.

## Local troubleshooting

| Symptom                                                    | Fix                                                                                                         |
|------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| Admin startup error "`Entra:…` must be a non-empty GUID"   | A parameter is missing or malformed in user secrets.                                                       |
| Sign-in loops back to `/unauthorized`                      | Signed in with a different account than `administrator-object-id`, or wrong tenant. Check admin logs for "Administrator sign-in failed". |
| `AADSTS50011` redirect URI mismatch                        | Add the exact `https://localhost:5001/signin-oidc` to the app registration.                                |
| Management API stuck "Waiting"                             | Ollama is still downloading `llama3`. Watch the `ollama` logs, or switch to Azure OpenAI.                 |
| Admin shows "SocialPoster is starting"                     | The SQLite store is opening; refresh after a few seconds.                                                 |
| `401 Authentication is required.` from the API             | Missing/incorrect `X-Admin-Api-Key`.                                                                        |
| URL list hits the redirect function / statistics `404`     | Route prefix doubled (`/api/api/…`); rebuild so `host.json` is copied.                                     |
| Docker errors on start                                     | Docker Desktop not running, or in Windows-containers mode.                                                 |
