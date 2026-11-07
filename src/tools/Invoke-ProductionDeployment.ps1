#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Validate', 'Publish', 'Deploy')]
    [string] $Mode = 'Validate',
    [string] $LocalSettingsPath,
    [switch] $LoadUserSecrets,
    [string] $AppHostProject = (Join-Path $PSScriptRoot '../ShortenerTools.AppHost/ShortenerTools.AppHost.csproj'),
    [string] $OutputPath = (Join-Path $PSScriptRoot '../../aspire-output'),
    [switch] $EnableScheduler,
    [switch] $EnableCoffeeScheduler
)

$ErrorActionPreference = 'Stop'
# Do not use verbose/debug tracing, print configuration, or pass secrets on a
# command line. Only allowlisted settings are copied into the child environment.
$saved = @{}
function Set-DeploymentEnvironment([string] $Name, [string] $Value) {
    if (-not $saved.ContainsKey($Name)) {
        $saved[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Read-Settings([string] $Path) {
    try {
        $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        if ($settings -isnot [System.Collections.IDictionary]) { throw 'Configuration must be an object.' }
        return $settings
    }
    catch {
        # Parser exceptions can contain the original JSON (and its secrets).
        throw 'Unable to read configuration JSON. Check the path, permissions and JSON format.'
    }
}

try {
    $local = @{}
    if ($LocalSettingsPath) {
        $document = Read-Settings $LocalSettingsPath
        if ($document.IsEncrypted -eq $true) { throw 'Encrypted Functions settings are not supported; decrypt locally first.' }
        if ($document.Values -isnot [System.Collections.IDictionary]) { throw 'Functions settings must contain a Values object.' }
        $local = $document.Values
    }
    $user = @{}
    if ($LoadUserSecrets) {
        try {
            [xml] $project = Get-Content -LiteralPath $AppHostProject -Raw
            $id = [string] $project.Project.PropertyGroup.UserSecretsId
            if ([string]::IsNullOrWhiteSpace($id) -or $id -match '[/\\]') { throw 'Invalid ID.' }
            $base = if ($IsWindows) { Join-Path $env:APPDATA 'Microsoft/UserSecrets' } else { Join-Path $env:HOME '.microsoft/usersecrets' }
            $path = Join-Path $base "$id/secrets.json"
            $user = Read-Settings $path
        }
        catch { throw 'Unable to load AppHost user secrets. Check the project UserSecretsId and local secret store.' }
    }

    # Destination parameter -> legacy Functions Values key. Empty keys mean
    # deployment-only settings, supplied via process environment or user secrets.
    $mapping = [ordered]@{
        'existing-storage-account' = ''
        'existing-storage-resource-group' = ''
        'entra-tenant-id' = ''
        'entra-client-id' = ''
        'entra-client-secret' = ''
        'administrator-object-id' = ''
        'admin-api-key' = 'AdminApiKey'
        'custom-domain' = 'CustomDomain'
        'redirect-certificate-name' = ''
        'default-redirect-url' = 'DefaultRedirectUrl'
        'social-accounts-connection-string' = 'ConnectionStrings:social-accounts'
        'social-accounts-readonly-connection-string' = 'ConnectionStrings:social-accounts-readonly'
        'communication-services-connection-string' = 'COMMUNICATION_SERVICES_CONNECTION_STRING'
        'email-from' = 'EmailFrom'
        'email-to' = 'EmailTo'
        'azure-openai-deployment-name' = 'DeploymentName'
    }
    $missing = [System.Collections.Generic.List[string]]::new()
    $productionDefaults = @{
        'custom-domain' = 'CustomDomain'
        'redirect-certificate-name' = 'RedirectCertificateName'
        'default-redirect-url' = 'DefaultRedirectUrl'
        'existing-storage-account' = 'ExistingStorageAccount'
        'existing-storage-resource-group' = 'ExistingStorageResourceGroup'
    }
    foreach ($name in $mapping.Keys) {
        $canonical = "Parameters__$name"
        $portable = "Parameters__$($name.Replace('-', '_'))"
        $value = [Environment]::GetEnvironmentVariable($canonical, 'Process')
        if ([string]::IsNullOrWhiteSpace($value)) { $value = [Environment]::GetEnvironmentVariable($portable, 'Process') }
        if ([string]::IsNullOrWhiteSpace($value) -and $productionDefaults.ContainsKey($name)) {
            $value = $user["ProductionDeployment:$($productionDefaults[$name])"]
        }
        if ([string]::IsNullOrWhiteSpace($value)) { $value = $user["Parameters:$name"] }
        if ([string]::IsNullOrWhiteSpace($value) -and $mapping[$name]) { $value = $local[$mapping[$name]] }
        if ([string]::IsNullOrWhiteSpace($value)) { $missing.Add($canonical) }
        else { Set-DeploymentEnvironment $canonical $value }
    }
    foreach ($name in @('SubscriptionId', 'Location', 'ResourceGroup')) {
        $key = "Azure__$name"
        $value = [Environment]::GetEnvironmentVariable($key, 'Process')
        if ([string]::IsNullOrWhiteSpace($value)) { $value = $user["Azure:$name"] }
        if ([string]::IsNullOrWhiteSpace($value)) { $value = $user["ProductionDeployment:$name"] }
        if ([string]::IsNullOrWhiteSpace($value)) { $missing.Add($key) }
        else { Set-DeploymentEnvironment $key $value }
    }
    $chat = $env:ConnectionStrings__chat
    if ([string]::IsNullOrWhiteSpace($chat)) { $chat = $user['ConnectionStrings:chat'] }
    if ([string]::IsNullOrWhiteSpace($chat)) { $chat = $local['ConnectionStrings:chat'] }
    if ([string]::IsNullOrWhiteSpace($chat)) {
        if ([string]::IsNullOrWhiteSpace($local['AzureOpenAIEndpoint']) -or
            [string]::IsNullOrWhiteSpace($local['AzureOpenAIKey'])) {
            $missing.Add('ConnectionStrings__chat (or Functions Values AzureOpenAIEndpoint + AzureOpenAIKey)')
        }
        else {
            $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
            $connection['Endpoint'] = $local['AzureOpenAIEndpoint']
            $connection['Key'] = $local['AzureOpenAIKey']
            $chat = $connection.get_ConnectionString()
        }
    }
    if ($missing.Count) { throw "Missing required production settings: $($missing -join ', '). No operation performed." }
    try {
        $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
        $connection.set_ConnectionString($chat)
        $endpoint = $null
        if (-not $connection.ContainsKey('Endpoint') -or
            -not [Uri]::TryCreate([string] $connection['Endpoint'], [UriKind]::Absolute, [ref] $endpoint) -or
            $endpoint.Scheme -ne 'https' -or $endpoint.UserInfo -or
            ($connection.ContainsKey('Key') -and [string]::IsNullOrWhiteSpace($connection['Key']))) {
            throw 'Invalid endpoint or key.'
        }
    }
    catch { throw 'Invalid Azure OpenAI chat connection. Supply Endpoint=https://...;Key=... (or Endpoint only with managed identity access).' }
    Set-DeploymentEnvironment 'ConnectionStrings__chat' $chat

    foreach ($key in @('Parameters__custom-domain', 'Parameters__default-redirect-url')) {
        $uri = $null
        if (-not [Uri]::TryCreate([Environment]::GetEnvironmentVariable($key), [UriKind]::Absolute, [ref] $uri) -or
            $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.IsLoopback -or
            $uri.Host.EndsWith('.localhost') -or $uri.Host.EndsWith('.invalid')) {
            throw "Invalid production URL setting: $key. Use a non-loopback absolute HTTPS URL without credentials."
        }
        if ($key -eq 'Parameters__custom-domain' -and $uri.AbsoluteUri.EndsWith('/') -and
            [Environment]::GetEnvironmentVariable($key).EndsWith('/')) {
            throw 'Invalid production URL setting: Parameters__custom-domain. Omit the trailing slash.'
        }
        if ($key -eq 'Parameters__custom-domain') {
            if ($uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or -not $uri.IsDefaultPort) {
                throw 'Invalid production URL setting: Parameters__custom-domain. Use only the HTTPS origin, without a path, query, fragment or custom port.'
            }
            Set-DeploymentEnvironment 'Parameters__redirect-domain-name' $uri.IdnHost
            Set-DeploymentEnvironment 'Parameters__redirect_domain_name' $uri.IdnHost
        }
    }
    if ([Environment]::GetEnvironmentVariable('Parameters__redirect-certificate-name') -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$') {
        throw 'Invalid Parameters__redirect-certificate-name. Supply the name of the certificate uploaded to the ACA environment, not its resource ID.'
    }
    # Neither local settings, inherited parameters nor cached deployment state
    # may silently opt in to scheduled posts. The switch is the cutover boundary.
    Set-DeploymentEnvironment 'Parameters__scheduler-disabled' (-not $EnableScheduler.IsPresent).ToString().ToLowerInvariant()
    Set-DeploymentEnvironment 'Parameters__scheduler_disabled' (-not $EnableScheduler.IsPresent).ToString().ToLowerInvariant()
    Set-DeploymentEnvironment 'Parameters__coffee-scheduler-disabled' (-not $EnableCoffeeScheduler.IsPresent).ToString().ToLowerInvariant()
    Set-DeploymentEnvironment 'Parameters__coffee_scheduler_disabled' (-not $EnableCoffeeScheduler.IsPresent).ToString().ToLowerInvariant()
    Write-Output "Production configuration validated. Scheduler enabled: $($EnableScheduler.IsPresent). Coffee scheduler enabled: $($EnableCoffeeScheduler.IsPresent)."
    if ($Mode -eq 'Deploy') {
        $storageAccount = [Environment]::GetEnvironmentVariable('Parameters__existing-storage-account')
        $storageGroup = [Environment]::GetEnvironmentVariable('Parameters__existing-storage-resource-group')
        $null = & az storage account show --name $storageAccount --resource-group $storageGroup `
            --subscription $env:Azure__SubscriptionId --query id --output tsv 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Production data storage preflight failed: account '$storageAccount' in resource group '$storageGroup' is missing or inaccessible. Check Azure login, subscription and existing-storage-resource-group (including ProductionDeployment:ExistingStorageResourceGroup). No Aspire deployment performed."
        }
    }
    if ($Mode -ne 'Validate') {
        $arguments = @($Mode.ToLowerInvariant(), '--apphost', $AppHostProject,
            '--environment', 'Production', '--non-interactive')
        if ($Mode -eq 'Publish') { $arguments += @('-o', $OutputPath) }
        # Clear cached resolved parameters on deploy, especially scheduler state.
        if ($Mode -eq 'Deploy') { $arguments += '--clear-cache' }
        # Capture all tool output; never forward exception payloads/configuration.
        $null = & aspire @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Aspire $Mode failed (exit $LASTEXITCODE). Output suppressed to protect credentials." }
        Write-Output "Production $Mode completed."
    }
}
finally {
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}
