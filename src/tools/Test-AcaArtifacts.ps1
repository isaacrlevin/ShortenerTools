param(
    [Parameter(Mandatory)]
    [string] $ArtifactPath,
    [ValidateSet('Production', 'Staging')]
    [string] $DeploymentEnvironment = 'Production'
)

$ErrorActionPreference = 'Stop'

function Read-Template([string] $Name) {
    $source = Join-Path $ArtifactPath "$Name\$Name.bicep"
    $output = Join-Path $ArtifactPath "$Name\$Name.json"
    if ($Name -eq 'main') {
        $source = Join-Path $ArtifactPath 'main.bicep'
        $output = Join-Path $ArtifactPath 'main.json'
    }
    az bicep build --file $source --outfile $output
    if ($LASTEXITCODE -ne 0) { throw "Bicep compilation failed for $Name." }
    return Get-Content $output -Raw | ConvertFrom-Json
}

function Read-ContainerApp([string] $Name) {
    $template = Read-Template $Name
    $app = @($template.resources | Where-Object type -EQ 'Microsoft.App/containerApps')
    if ($app.Count -ne 1) { throw "Expected one Container App in $Name." }
    return $app[0]
}

function Assert-Condition([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$redirect = Read-ContainerApp 'shortenertools-functions'
$api = Read-ContainerApp 'management-api'
$admin = Read-ContainerApp 'admin'
$scheduler = Read-ContainerApp 'shortenertools-scheduled-posts'

$insightsTemplate = Read-Template 'app-insights'
$insights = @($insightsTemplate.resources | Where-Object type -EQ 'Microsoft.Insights/components')
$workspaces = @($insightsTemplate.resources | Where-Object type -EQ 'Microsoft.OperationalInsights/workspaces')
Assert-Condition ($insights.Count -eq 1 -and $workspaces.Count -eq 1) 'Each deployment must provision workspace-based Application Insights.'
Assert-Condition ($workspaces[0].properties.sku.name -eq 'PerGB2018') 'Insights workspace must use the supported pay-as-you-go SKU.'
# Verify the component points to the workspace created in this module, not an
# unrelated resource or a manually supplied connection string.
$workspaceId = $insights[0].properties.WorkspaceResourceId
Assert-Condition ($workspaceId -like "[[]resourceId('Microsoft.OperationalInsights/workspaces',*") 'Insights must link to its managed workspace.'
$workspaceName = $workspaces[0].name.Trim('[', ']')
Assert-Condition ($workspaceId.Contains($workspaceName)) 'Insights must reference the provisioned workspace name.'
Assert-Condition ($insightsTemplate.outputs.appInsightsConnectionString.value -match "reference\(.+ConnectionString") 'Insights must output its generated connection string.'
$mainSource = Get-Content (Join-Path $ArtifactPath 'main.bicep') -Raw
Assert-Condition ($mainSource.Contains("module app_insights 'app-insights/app-insights.bicep'")) 'Main deployment must include the Insights module.'
Assert-Condition ($mainSource.Contains('app_insights.outputs.appInsightsConnectionString')) 'Main deployment must expose the generated connection for application deployment.'
$telemetryApps = @{
    'admin' = $admin
    'shortenertools-functions' = $redirect
    'management-api' = $api
    'shortenertools-scheduled-posts' = $scheduler
}
foreach ($name in $telemetryApps.Keys) {
    $app = $telemetryApps[$name]
    $envVars = $app.properties.template.containers[0].env
    $connection = @($envVars | Where-Object name -EQ 'APPLICATIONINSIGHTS_CONNECTION_STRING')
    Assert-Condition ($connection.Count -eq 1 -and
        $connection[0].value -eq "[parameters('app_insights_outputs_appinsightsconnectionstring')]") "Insights connection must be resource-reference-backed for $name."
    $template = Get-Content (Join-Path $ArtifactPath "$name/$name.json") -Raw | ConvertFrom-Json
    Assert-Condition ($template.parameters.app_insights_outputs_appinsightsconnectionstring.type -eq 'string') "Insights connection parameter must be declared for $name."
    Assert-Condition (@($envVars | Where-Object name -EQ 'OTEL_SERVICE_NAME')[0].value -eq $name) "Telemetry role must distinguish $name."
    if ($name -ne 'admin') {
        Assert-Condition (@($envVars | Where-Object name -EQ 'AzureFunctionsJobHost__telemetryMode')[0].value -eq 'OpenTelemetry') "Functions host must export OpenTelemetry for $name."
        Assert-Condition (@($envVars | Where-Object name -EQ 'IN_ASPIRE')[0].value -eq 'true') "Functions worker must use only Aspire telemetry for $name."
    }
}

Assert-Condition ($redirect.properties.configuration.ingress.external -eq $true) 'Redirect must be public.'
$domains = @($redirect.properties.configuration.ingress.customDomains)
if ($DeploymentEnvironment -eq 'Production') {
    Assert-Condition ($domains.Count -eq 1) 'Production redirect must declare its custom domain so redeploys retain the binding.'
    Assert-Condition ($domains[0].name -eq "[parameters('redirect_domain_name')]") 'Ingress must use a hostname parameter, not the full short-link URL.'
    Assert-Condition ($domains[0].bindingType -eq "[if(not(equals(parameters('redirect_certificate_name'), '')), 'SniEnabled', 'Disabled')]") 'Configured certificates must enable SNI binding.'
    Assert-Condition ($domains[0].certificateId -eq "[if(not(equals(parameters('redirect_certificate_name'), '')), format('{0}/certificates/{1}', parameters('aca_outputs_azure_container_apps_environment_id'), parameters('redirect_certificate_name')), null())]") 'Redirect must reuse the existing uploaded (Cloudflare Origin CA) certificate in its ACA environment.'
}
else {
    Assert-Condition (@($domains | Where-Object { $null -ne $_ }).Count -eq 0) 'Staging must not bind the production custom domain.'
}
foreach ($app in @($api, $admin, $scheduler)) {
    Assert-Condition (@($app.properties.configuration.ingress.customDomains | Where-Object { $null -ne $_ }).Count -eq 0) 'The short-link custom domain must only bind to the redirect app.'
}
Assert-Condition ($api.properties.configuration.ingress.external -eq ($DeploymentEnvironment -eq 'Production')) 'Management API must be public only in Production.'
Assert-Condition ($scheduler.properties.configuration.ingress.external -eq $false) 'Scheduler must not be public.'
foreach ($app in @($redirect, $scheduler)) {
    Assert-Condition ($app.properties.template.scale.minReplicas -eq 1 -and
        $app.properties.template.scale.maxReplicas -eq 1) 'Redirect and scheduler must each have exactly one replica.'
}
# Admin publishing calls the API interactively, so a cold start must not sit in front of Publish.
Assert-Condition ($api.properties.template.scale.minReplicas -eq 1 -and
    $api.properties.template.scale.maxReplicas -eq 1) 'Management API must stay warm with exactly one replica.'
# SocialPoster scheduling runs in the Admin host, so it must stay warm.
Assert-Condition ($admin.properties.template.scale.minReplicas -eq 1 -and
    $admin.properties.template.scale.maxReplicas -eq 1) 'Admin must have exactly one replica for SocialPoster scheduling.'

$redirectEnv = $redirect.properties.template.containers[0].env
$redirectResources = $redirect.properties.template.containers[0].resources
Assert-Condition ($redirectResources.memory -eq '0.5Gi' -and
    $redirectResources.cpu -in @("0.25", "[json('0.25')]")) 'Redirect must use the documented 0.25 vCPU / 0.5 GiB allocation.'
$redirectFunctions = @($redirectEnv | Where-Object name -Like 'AzureFunctionsJobHost__functions__*')
Assert-Condition ($redirectFunctions.Count -eq 1 -and
    $redirectFunctions[0].value -eq 'UrlRedirect') 'Public container must only enable redirects.'
Assert-Condition (@($redirectEnv | Where-Object name -EQ 'AdminApiKey').Count -eq 0) 'Redirect must not receive the admin key.'
# Settings the code never reads in a given app are not passed to it.
Assert-Condition (@($redirectEnv | Where-Object name -In @('CustomDomain', 'DisableExternalPosting', 'USE_OLLAMA', 'PostSocials')).Count -eq 0) 'Redirect must only receive settings UrlRedirect reads.'
Assert-Condition (@($redirectEnv | Where-Object name -EQ 'ConnectionStrings__social-accounts').Count -eq 0) 'Public redirect must not receive social account access.'
Assert-Condition (@($redirectEnv | Where-Object {
    $_.name -eq 'ConnectionStrings__chat' -or
    $_.name -match '^(Twitter|Mastodon|LinkedIn|Bluesky|Threads|Email|COMMUNICATION_SERVICES)'
}).Count -eq 0) 'Redirect must not receive AI/social/email credentials.'
$schedulerFunctions = @($scheduler.properties.template.containers[0].env |
    Where-Object name -Like 'AzureFunctionsJobHost__functions__*')
$coffeeTimers = @('COSSTeaserTimer', 'COSSAnnouncementTimer', 'COSSArchiveTimer')
$shortPostTimerFunction = 'ShortPostTimer'
$expectedTimers = @($shortPostTimerFunction) + $coffeeTimers
Assert-Condition ($schedulerFunctions.Count -eq $expectedTimers.Count -and
    @(Compare-Object $expectedTimers @($schedulerFunctions.value)).Count -eq 0) 'Scheduler must enable exactly the shortener and Coffee timers.'

$apiEnv = $api.properties.template.containers[0].env
$schedulerEnv = $scheduler.properties.template.containers[0].env
foreach ($envVars in @($apiEnv, $schedulerEnv)) {
    Assert-Condition (@($envVars | Where-Object name -EQ 'DefaultRedirectUrl').Count -eq 0) 'Only the redirect app reads DefaultRedirectUrl.'
}
Assert-Condition (@($schedulerEnv | Where-Object name -EQ 'USE_OLLAMA').Count -eq 0) 'The scheduler has AI disabled and must not receive USE_OLLAMA.'
Assert-Condition (@($schedulerEnv | Where-Object name -EQ 'AzureFunctionsJobHost__functions__0')[0].value -eq $shortPostTimerFunction) 'Scheduler selector must match the shortener timer function name.'
Assert-Condition (@($schedulerEnv | Where-Object name -EQ "AzureWebJobs.$shortPostTimerFunction.Disabled").Count -eq 1) 'Scheduler must disable the actual shortener timer function by its Azure Functions name.'
Assert-Condition (@($schedulerEnv | Where-Object name -EQ 'AzureWebJobs.SchedulePostTimer.Disabled').Count -eq 0) 'Scheduler must not use the obsolete shortener timer setting name.'
foreach ($app in @($redirect, $api)) {
    Assert-Condition (@($app.properties.template.containers[0].env |
        Where-Object name -EQ 'AzureWebJobs.ShortPostTimer.Disabled')[0].value -eq 'true') 'Redirect/API timers must always be disabled.'
    foreach ($timer in $coffeeTimers) {
        Assert-Condition (@($app.properties.template.containers[0].env |
            Where-Object name -EQ "AzureWebJobs.$timer.Disabled")[0].value -eq 'true') "Redirect/API must disable $timer."
    }
}
# Compile the subscription template and its infrastructure modules too.
$null = Read-Template 'main'
$coffeeDisabledValues = @($coffeeTimers | ForEach-Object {
    @($schedulerEnv | Where-Object name -EQ "AzureWebJobs.$_.Disabled")[0].value
})
$timerDisabled = @($schedulerEnv | Where-Object name -EQ "AzureWebJobs.$shortPostTimerFunction.Disabled")[0].value
if ($DeploymentEnvironment -eq 'Production') {
    # The runtime input controls cutover, not a baked-in enabled timer.
    Assert-Condition ($timerDisabled -eq "[parameters('scheduler_disabled_value')]") 'Production shortener scheduler must use its dedicated deployment opt-in.'
    $schedulerTemplate = Get-Content (Join-Path $ArtifactPath 'shortenertools-scheduled-posts/shortenertools-scheduled-posts.json') -Raw | ConvertFrom-Json
    Assert-Condition ($schedulerTemplate.parameters.scheduler_disabled_value.type -eq 'string' -and
        $null -eq $schedulerTemplate.parameters.scheduler_disabled_value.defaultValue) 'Shortener scheduler state must be supplied by the AppHost parameter, not hard-coded in its module.'
    foreach ($value in $coffeeDisabledValues) {
        Assert-Condition ($value -eq "[parameters('coffee_scheduler_disabled_value')]" -and
            $value -ne $timerDisabled) 'Coffee timers must share an independent disabled parameter, not the shortener gate.'
    }
    Assert-Condition ($schedulerTemplate.parameters.coffee_scheduler_disabled_value.type -eq 'string' -and
        $null -eq $schedulerTemplate.parameters.coffee_scheduler_disabled_value.defaultValue) 'Coffee scheduler state must be supplied by the AppHost parameter, not hard-coded in its module.'
    foreach ($envVars in @($apiEnv, $schedulerEnv)) {
        Assert-Condition (@($envVars | Where-Object name -EQ 'PostSocials')[0].value -eq 'True') 'Production Coffee must allow opted-in timers and administrator manual posting.'
    }
    Assert-Condition (@($apiEnv | Where-Object name -EQ 'DeploymentName')[0].value -match '^\[parameters\(') 'AI deployment name must be parameter-backed.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace(@($apiEnv | Where-Object name -EQ 'ConnectionStrings__chat')[0].secretRef)) 'AI connection must be a secret reference.'
}
else {
    Assert-Condition ($timerDisabled -eq 'true') 'Staging scheduler must remain disabled, regardless of cutover inputs.'
    Assert-Condition (@($coffeeDisabledValues | Where-Object { $_ -ne 'true' }).Count -eq 0) 'Staging Coffee timers must ignore cutover opt-ins.'
    foreach ($envVars in @($apiEnv, $schedulerEnv)) {
        Assert-Condition (@($envVars | Where-Object name -EQ 'PostSocials')[0].value -eq 'False') 'Staging Coffee must not post socials.'
    }
    foreach ($app in @($api, $scheduler)) {
        Assert-Condition (@($app.properties.template.containers[0].env |
            Where-Object name -EQ 'DisableExternalPosting')[0].value -eq 'True') 'Staging must block external posts.'
    }
    foreach ($app in @($redirect, $api, $scheduler)) {
        Assert-Condition (@($app.properties.template.containers[0].env | Where-Object {
            $_.name -match '^(Twitter|Mastodon|LinkedIn|Bluesky|Threads|Email|COMMUNICATION_SERVICES)'
        }).Count -eq 0) 'Staging must not receive social/email credentials.'
    }
    Assert-Condition (@($apiEnv | Where-Object name -EQ 'EnableDescriptionGeneration')[0].value -eq 'False') 'Staging must disable AI.'
    Assert-Condition (@($apiEnv | Where-Object name -EQ 'ConnectionStrings__chat').Count -eq 0) 'Staging must not receive AI credentials.'
}
Assert-Condition (@($apiEnv | Where-Object {
    $_.name -like 'AzureFunctionsJobHost__functions__*' -and $_.value -in (@('UrlRedirect') + $expectedTimers)
}).Count -eq 0) 'API must not enable redirects or timer.'
$expectedApiFunctions = @('UrlList', 'UrlCreate', 'UrlCheck', 'UrlUpdate', 'UrlArchive', 'UrlStats',
    'UrlClickStatsByDay', 'UrlPostVersions', 'CreateDescription', 'ShortPostHttp', 'TestShortUrl',
    'COSSPublishHttp', 'COSSTeaserHttp', 'COSSAnnouncementHttp', 'COSSArchiveHttp')
$apiFunctions = @($apiEnv | Where-Object name -Like 'AzureFunctionsJobHost__functions__*')
Assert-Condition ($apiFunctions.Count -eq $expectedApiFunctions.Count -and
    @(Compare-Object $expectedApiFunctions @($apiFunctions.value)).Count -eq 0) 'Management must allow exactly its HTTP functions, including Coffee.'
Assert-Condition (@($apiEnv | Where-Object name -EQ 'AdminApiKey')[0].secretRef -eq 'adminapikey') 'API key must be a secret reference.'
Assert-Condition (@($apiEnv | Where-Object name -EQ 'ConnectionStrings__social-accounts')[0].secretRef -eq 'connectionstrings--social-accounts') 'Management Functions must receive only the read-only SocialPoster configuration credential.'
Assert-Condition (@($schedulerEnv | Where-Object name -EQ 'ConnectionStrings__social-accounts')[0].secretRef -eq 'connectionstrings--social-accounts') 'Scheduled Functions must receive only the read-only SocialPoster configuration credential.'
foreach ($app in @($redirect, $api, $scheduler)) {
    Assert-Condition (@($app.properties.template.containers[0].env |
        Where-Object name -EQ 'tables__tableServiceUri').Count -eq 1) 'Each Functions app must receive the existing Table endpoint.'
}
$adminEnv = $admin.properties.template.containers[0].env
foreach ($envVars in @($redirectEnv, $apiEnv, $schedulerEnv, $adminEnv)) {
    Assert-Condition (@($envVars | Where-Object {
        $_.name -match '^(Twitter|Mastodon|LinkedIn|Bluesky|Threads)'
    }).Count -eq 0) 'Social account settings must come from App Configuration, not deployment settings.'
}
Assert-Condition (@($adminEnv | Where-Object name -EQ 'Entra__ClientSecret')[0].secretRef -eq 'entra--clientsecret') 'Entra client secret must be a secret reference.'
Assert-Condition (@($adminEnv | Where-Object name -EQ 'Entra__AdministratorObjectId').Count -eq 1) 'Admin must receive its allowed object ID.'
Assert-Condition (@($adminEnv | Where-Object name -EQ 'ConnectionStrings__social-accounts')[0].secretRef -eq 'connectionstrings--social-accounts') 'Admin must receive the read/write SocialPoster configuration credential.'
Write-Output 'ACA ingress, scaling, function isolation, storage, secrets, AI, independent shortener/Coffee scheduler cutover and workspace-based Insights/4-app telemetry assertions passed.'
