# Admin

<!-- ALL-CONTRIBUTORS-BADGE:START - Do not remove or modify this section -->
[![All Contributors](https://img.shields.io/badge/all_contributors-6-orange.svg?style=flat-square)](#contributors-)
<!-- ALL-CONTRIBUTORS-BADGE:END -->

Admin tools for ShortenerTools using server-interactive Blazor on .NET 11. Microsoft Entra sign-in is restricted to
one configured administrator. API calls run on the server against the private management Container App; no API key or
storage secret is sent to the browser.

![Admin home page][tinyBA_home]

Once authenticated you can manage your URLs and see statistics using [MudBlazor](https://mudblazor.com/), an open-source
component library with no license key required. This project targets .NET 11.

The URL manager initially retrieves only the newest 100 active links (by storage timestamp). Sorting, per-column
filtering, and paging request the matching page from the API rather than downloading the complete list. Page sizes of
15, 30, 50, and 100 are available. Azure Table Storage cannot order by arbitrary columns, so the API scans active links
server-side to calculate the matching count and sorted page; only that page is sent to the browser. Create, edit,
archive, and clipboard copy remain available. Publishing, scheduling, and AI drafting are handled by SocialPoster.

Statistics open on the last month (in your browser's time zone), so the page doesn't wait for a link's whole click
history; pick earlier dates or clear the range to load more (clearing loads every click, which is slower). Crawler clicks (link-preview fetchers and crawlers recognised from the
user agent) are hidden; turn off **Hide crawler clicks** to include them. Clicks recorded before crawler flagging existed are
always shown. The date-range picker filters by inclusive local calendar dates and updates the
daily chart, category charts, and 50-row click-data grid together; changing it reloads from the server. URL-specific
statistics use the same behavior.

## SocialPoster

SocialPoster is the authenticated application at `/` and `/socialposter`. Its workspace contains Compose,
Links, Contacts, History and account settings; click statistics remain available from the navigation.
The deployment project stays named `ShortenerTools.Admin` so its existing Aspire wiring, persistent volume,
Entra callbacks and production host continue to work.

In **Compose & schedule**, enter the destination in **Draft from a link**, optionally set a custom shortcode
and link title, then click **Generate** to draft the shared and per-platform messages from that page. Edit the
text, select accounts and media, then click **Publish** or **Schedule**. Generation does not create a link record.
Both submission paths first create the link through the management API and replace the destination in each selected
platform's message with the shortened URL (or append the short URL if the generated text omitted the destination).
The metadata records the title (the destination host if no title was entered) and the shared message at first creation.

A vanity conflict or storage failure stops submission before anything is published or scheduled. The draft ID,
destination, vanity, title and short-link workflow state are saved with the SocialPoster draft. A retry after a
publish failure, network timeout or page reload reuses the original link and preserves its original metadata.
Starting a new post resets this identity. Scheduled messages receive their final short URL when Schedule is clicked.

**Generate short link** (top of Compose & schedule) opens a dialog that creates a link without posting it: enter the
destination and an optional custom shortcode, which is checked for availability as you type (`GET /api/UrlCheck`).
The link is stored as already posted, so no timer picks it up.

**Links** lists, edits, archives and reuses existing links; **Compose** on a link reuses that record without creating
another, and preserves existing draft text, platform selections and customized platform versions.

The reusable composer lives in the SocialPoster packages (`SocialPoster.Components` / `SocialPoster.Posting`), whose
versions are pinned in `src/Directory.Packages.props` and restored from nuget.org.
SocialPoster owns generation, publishing, scheduling and account management; the host workflow only prepares links.

New links created in this workspace use `PostToSocial=false`, keeping them out of the legacy Function publishing
queue. Existing external API and automation contracts remain available for compatibility; historical `Posted`
values are not rewritten. Legacy `/urlmanager` and statistics URLs remain valid.

Custom shortcodes are case-sensitive and use up to 128 ASCII letters, digits, hyphens or underscores.
`KEY`, `api`, `health` and `alive` are reserved regardless of case. Blank codes are generated automatically.
Creation uses an atomic Table insert, returning HTTP 409 for an occupied custom code, including archived links;
it never upserts a new link over an existing record. Validation applies to new codes only.

SocialPoster keeps its accounts, contacts, history, schedules, uploaded media, and Data Protection keys in a dedicated
SQLite data directory. Set `SocialPoster__DataDirectory` to a durable, access-controlled directory in deployed
environments and persist the entire directory, including `keys/`, together. The store is single-user and file-based; run
one Admin replica against it. The Admin Container App keeps one replica running because the scheduled-post worker runs
in that host. Local development defaults to `src/ShortenerTools.Admin/socialposter-data`.

Bluesky, X, LinkedIn, Mastodon, and Threads credentials entered in SocialPoster Settings are the source of truth. Create a
dedicated Azure App Configuration store for these settings and issue separate read/write and read-only access keys;
the AppHost expects its connection strings and does not provision this store. Admin
synchronizes the settings values to Azure App Configuration every five seconds; the management and scheduled Functions
apps read that shared store and refresh after changes. Configure an App Configuration read/write connection string as
`Parameters:social-accounts-connection-string` and a read-only connection string as
`Parameters:social-accounts-readonly-connection-string` in AppHost user secrets (or matching
`Parameters__...` environment variables during deployment). Admin receives the read/write credential; Functions receive
only the read-only credential. Do not place provider credentials in
Functions settings or deployment secrets. At local run time, omit the connection string to keep sync disabled, or
configure it to test cross-process updates.

New composer-created `UrlsDetails` records have one optional string property, `CreationId`, to make creation
idempotent. Table keys, destination, title, message and click tables keep their existing meaning. No new tables,
backfill or Azure provisioning are required. Existing rows have no `CreationId` and continue to work.
Automatic codes for identified composer requests are derived from the draft's creation token; older API callers
without a token retain the existing generated-code path. No provider credentials are stored in the link record.

The daily chart now uses the server-side `/api/UrlClickStatsByDay` aggregation endpoint, sending the browser's time zone
so daily boundaries match the existing local-date filtering. Individual click records still come from `/api/UrlStats`
for the category charts and detailed grid; changing dates refreshes only daily aggregates and filters the cached
details. Existing timestamp interpretation is preserved, including legacy timestamps without a time-zone suffix. These
timestamps still require a storage scan to aggregate; this is not a precomputed rollup or an indexed date query.

To build locally, run `dotnet build src\ShortenerTools.Admin\ShortenerTools.Admin.csproj` with the .NET 11 SDK. API
requests use Aspire service discovery (`management-api`), not the browser origin or SWA emulator.

## Local development and debugging with Aspire

Install the .NET 11 SDK, Aspire CLI, Azure Functions Core Tools v4, and Docker Desktop with Linux containers running.
Node/SWA CLI is no longer required. Configure the AppHost's user secrets using your local secret-management workflow:

```powershell
dotnet user-secrets set "Parameters:entra-tenant-id" "<tenant-id>" --project src\ShortenerTools.AppHost
dotnet user-secrets set "Parameters:entra-client-id" "<client-id>" --project src\ShortenerTools.AppHost
dotnet user-secrets set "Parameters:administrator-object-id" "<your-object-id>" --project src\ShortenerTools.AppHost
dotnet user-secrets set "Parameters:custom-domain" "https://<short-link-host>" --project src\ShortenerTools.AppHost
dotnet user-secrets set "Parameters:default-redirect-url" "https://example.com" --project src\ShortenerTools.AppHost
```

Store `Parameters:entra-client-secret` and a strong `Parameters:admin-api-key` with the same secret provider without
recording secret values in shell history. Configure Entra Web callback URLs as described in
the [deployment guide](../../doc/deployment.md#5-entra-app-registration-for-admin-sign-in).

Set both App Configuration connection strings through your secret manager before publishing or deploying. The
production deployment helper accepts `Parameters__social-accounts-connection-string` and
`Parameters__social-accounts-readonly-connection-string`, or the corresponding
`ConnectionStrings:social-accounts` and `ConnectionStrings:social-accounts-readonly` values in the supplied Functions
settings file.

Open `src\shortenerTools.sln` in an Aspire-compatible Visual Studio version, set `ShortenerTools.AppHost` as the startup
project, select its `https` launch profile, and press F5. Breakpoints in Functions and the Blazor server now run in
server processes; this is not WebAssembly debugging.

For VS Code, open `src` as the workspace folder and select **Launch ShortenerTools AppHost**. The checked-in `.vscode`
paths are relative to that folder, not the repository root.

For terminal-based development:

```powershell
aspire start --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj --launch-profile https
aspire wait admin --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj
```

The CLI starts the processes but does not attach an IDE debugger. Use the IDE-managed AppHost launch for debugging.

### Rider

Open `src\ShortenerTools.sln` and use an **Aspire Host** run configuration, not the admin's standalone .NET/IIS Express
configuration. Select project `ShortenerTools.AppHost`, target framework `net11.0`, and launch profile `https` (Azurite)
or `https-production-data` (live URL/click data). Ensure the .NET Aspire plugin is enabled and compatible with your
installed Rider version. After project/profile renames, remove obsolete run configurations and regenerate them from the
current `launchSettings.json`.

If the dashboard opens but the admin times out, inspect the admin's startup logs and process ID. A resource marked
Running/Healthy without startup logs or an actual listening process is not proof that Rider launched the child
application. In the observed failing Rider session, the admin had PID `0`, no logs, and neither its dashboard endpoint
nor its allocated backend port was listening. A CLI launch of the same production-data profile started the admin and
returned HTTP 200 at `https://localhost:5001/`.

To bypass Rider's child-process launch integration while retaining debugging, stop the Rider AppHost session first, then
run the CLI commands above (use `--launch-profile https-production-data` for live data). In Rider, choose **Run > Attach
to Process**, select the `ShortenerTools.Admin` process, and use the managed/.NET debugger. The process may be displayed
as `dotnet`; use the admin's process ID from the dashboard resource details to identify it. Open the admin **HTTPS
endpoint from the dashboard**, not the internal allocated backend port. Aspire keeps the API, storage, environment
variables, and service discovery wired; attaching does not launch a second admin.

This is a workaround for the observed IDE launch failure, not a change to Entra or storage settings. For IDE-managed
launches that still fail, enable **Help > Diagnostic Tools > Choose Trace Scenarios > Aspire** and inspect the Rider
logs using
the [JetBrains troubleshooting guidance](https://www.jetbrains.com/help/rider/NET_Aspire_troubleshooting.html).

Open the **admin HTTPS** endpoint in the dashboard. Add that exact origin's `/signin-oidc` and `/signout-callback-oidc`
URLs to the Entra app registration. Sign in using your real configured Entra account; mock SWA identities are no longer
supported. If the endpoint is unavailable, check the AppHost and admin logs. The first Ollama download can delay the
management API, but public redirects do not wait for AI.

Functions must register the admin endpoints as `/api/UrlList`, `/api/UrlStats`, and `/api/UrlClickStatsByDay`, not
`/api/api/...`. The Functions project always copies its `host.json` into build and publish output to preserve the empty
HTTP route prefix. If the startup logs show a doubled prefix, rebuild and restart the Functions resource; with the
default prefix, URL-list requests can hit the short-link redirect function instead and statistics requests return 404.

Azurite is used for Functions host state and URL data by default. The scheduled-posting timer is disabled in local runs.
Keep social/email credentials in local Functions settings or secrets when exercising those integrations explicitly.

### Using production storage data

To test statistics against real links and clicks, store the production storage connection string in the AppHost's user
secrets (never in source control):

```powershell
dotnet user-secrets set "ConnectionStrings:production-data" "<production storage connection string>" --project src\ShortenerTools.AppHost
```

Select `https-production-data` to opt into production data for that launch, or set `Storage:UseProduction` to `true` in
AppHost user secrets and use its HTTPS profile. Only redirect/management data switches to production; Functions host
state and the disabled scheduler stay on Azurite. Remove persistent opt-ins after live-data testing.

From a terminal at the repository root, the equivalent launch is:

```powershell
aspire start --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj --launch-profile https-production-data
aspire wait admin --apphost src\ShortenerTools.AppHost\ShortenerTools.AppHost.csproj
```

The local admin calls the **local management API**, which reads the existing production tables through this connection
string. It does not call the deployed SWA/Functions admin API and does not require deploying Container Apps first.
Obtain the connection string from the existing URL-data storage account's **Access keys** page in Azure, not from a
Functions host-state account. Treat it as a secret with production write access. The local Entra registration must
include `https://localhost:5001/signin-oidc` and `https://localhost:5001/signout-callback-oidc` when using the
checked-in admin endpoint.

> **Warning:** this is live data. Viewing statistics is read-only, but creating, editing, or archiving URLs, and
> following short links through the local `UrlRedirect` function (which records clicks), change production data. If the
> secret is missing, Aspire marks the `production-data` resource as missing and Functions does not start.

Ollama runs locally and persists its model data in a Docker volume. The first start downloads `llama3` and may take
several minutes; Functions waits for the model. CPU mode is the default. To opt into NVIDIA GPU acceleration when your
Docker runtime supports it:

```powershell
dotnet user-secrets set "Ollama:UseGpu" "true" --project src\shortenerTools.AppHost
```

Use the dashboard for resource logs and lifecycle controls. Stop the terminal-started app with:

```powershell
aspire stop --apphost src\shortenerTools.AppHost\shortenerTools.AppHost.csproj
```

The historical SWA CLI settings/scripts are not used by the migrated AppHost.

![Admin URLs manager page][tinyBA_urls]

![Admin Statistics page][tinyBA_stats]

# Deployment

Use the [deployment guide](../../doc/deployment.md) for Aspire publish/deploy, Entra
app registration, GitHub Actions secrets, and DNS cutover.

# Contributing

If you find a bug or would like to add a feature, check out those resources:

[tinyBA_home]: /Media/tinyBA_home.png
[tinyBA_stats]: /Media/tinyBA_stats.png
[tinyBA_urls]: /Media/tinyBA_urls.png

## Contributors ✨

Thanks goes to these wonderful people ([emoji key](https://allcontributors.org/docs/en/emoji-key)):

<!-- ALL-CONTRIBUTORS-LIST:START - Do not remove or modify this section -->
<!-- prettier-ignore-start -->
<!-- markdownlint-disable -->
<table>
  <tbody>
    <tr>
      <td align="center"><a href="https://github.com/FBoucher"><img src="https://avatars3.githubusercontent.com/u/2404846?v=4?s=100" width="100px;" alt="Frank Boucher"/><br /><sub><b>Frank Boucher</b></sub></a><br /><a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=FBoucher" title="Documentation">📖</a> <a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=FBoucher" title="Code">💻</a> <a href="#ideas-FBoucher" title="Ideas, Planning, & Feedback">🤔</a></td>
      <td align="center"><a href="http://www.mayoclinic.org"><img src="https://avatars3.githubusercontent.com/u/765798?v=4?s=100" width="100px;" alt="jbrule"/><br /><sub><b>jbrule</b></sub></a><br /><a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=jbrule" title="Documentation">📖</a></td>
      <td align="center"><a href="https://cmatskas.com"><img src="https://avatars3.githubusercontent.com/u/4126750?v=4?s=100" width="100px;" alt="Christos Matskas"/><br /><sub><b>Christos Matskas</b></sub></a><br /><a href="#security-cmatskas" title="Security">🛡️</a> <a href="https://github.com/FBoucher/TinyBlazorAdmin/issues?q=author%3Acmatskas" title="Bug reports">🐛</a></td>
      <td align="center"><a href="https://github.com/ronhowe"><img src="https://avatars1.githubusercontent.com/u/5210043?v=4?s=100" width="100px;" alt="Ron Howe"/><br /><sub><b>Ron Howe</b></sub></a><br /><a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=ronhowe" title="Documentation">📖</a></td>
      <td align="center"><a href="https://github.com/Mark-Phillipson"><img src="https://avatars0.githubusercontent.com/u/16239024?v=4?s=100" width="100px;" alt="Mark Phillipson"/><br /><sub><b>Mark Phillipson</b></sub></a><br /><a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=Mark-Phillipson" title="Documentation">📖</a> <a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=Mark-Phillipson" title="Code">💻</a> <a href="https://github.com/FBoucher/TinyBlazorAdmin/pulls?q=is%3Apr+reviewed-by%3AMark-Phillipson" title="Reviewed Pull Requests">👀</a></td>
      <td align="center"><a href="https://github.com/fatpacket"><img src="https://avatars.githubusercontent.com/u/5621063?v=4?s=100" width="100px;" alt="fatpacket"/><br /><sub><b>fatpacket</b></sub></a><br /><a href="https://github.com/FBoucher/TinyBlazorAdmin/commits?author=fatpacket" title="Documentation">📖</a></td>
    </tr>
  </tbody>
</table>

<!-- markdownlint-restore -->
<!-- prettier-ignore-end -->

<!-- ALL-CONTRIBUTORS-LIST:END -->

This project follows the [all-contributors](https://github.com/all-contributors/all-contributors) specification.
Contributions of any kind welcome!
