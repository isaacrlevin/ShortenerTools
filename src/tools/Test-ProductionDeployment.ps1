#requires -Version 7.0
# Standalone offline regression tests; all credentials are synthetic.
$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot 'Invoke-ProductionDeployment.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())
$originalEnvironment = @{
    'APPDATA' = $env:APPDATA
    'HOME' = $env:HOME
}
foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
    if ($entry.Key -match '^(Parameters__|ConnectionStrings__|Azure__)' -or $entry.Key -in @('APPDATA', 'HOME')) {
        $originalEnvironment[$entry.Key] = $entry.Value
    }
}
function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Expect-Failure([scriptblock] $Action, [string] $Message) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert ($null -ne $failure -and $failure -like "*$Message*") 'Expected a safe, actionable failure.'
    Assert ($failure -notlike '*synthetic-private*') 'Error leaked synthetic credential content.'
}
function az {
    Set-Variable -Name LASTEXITCODE -Value ([int]$global:ProductionDeploymentTestState.StorageExitCode) -Scope 1
}
function aspire {
    $global:ProductionDeploymentTestState.Invocations += ,@($args)
    $global:ProductionDeploymentTestState.Observed = @{}
    foreach ($key in @('Parameters__scheduler-disabled', 'Parameters__azure-openai-deployment-name',
        'Parameters__scheduler_disabled', 'Parameters__coffee-scheduler-disabled', 'Parameters__coffee_scheduler_disabled',
        'ConnectionStrings__chat', 'Parameters__admin-api-key',
        'Parameters__social-accounts-connection-string',
        'Parameters__social-accounts-readonly-connection-string',
        'Parameters__custom-domain', 'Parameters__default-redirect-url',
        'Parameters__redirect-domain-name', 'Parameters__redirect_domain_name', 'Parameters__redirect-certificate-name',
        'Parameters__existing-storage-account', 'Parameters__existing-storage-resource-group')) {
        $global:ProductionDeploymentTestState.Observed[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    }
    Set-Variable -Name LASTEXITCODE -Value $global:ProductionDeploymentTestState.ExitCode -Scope 1
    # A misbehaving native tool's output must still not leak.
    'synthetic-private-output'
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
        if ($entry.Key -match '^(Parameters__|ConnectionStrings__|Azure__)') {
            [Environment]::SetEnvironmentVariable($entry.Key, $null, 'Process')
        }
    }
    $global:ProductionDeploymentTestState = @{ Invocations = @(); Observed = @{}; ExitCode = 0 }
    Expect-Failure { & $helper } 'Missing required production settings'
    Assert ($global:ProductionDeploymentTestState.Invocations.Count -eq 0) 'Missing inputs must not invoke Aspire.'
    $settings = Join-Path $scratch 'local.settings.json'
    Set-Content $settings '{"Values":{"synthetic-private":"broken"'
    Expect-Failure { & $helper -LocalSettingsPath $settings } 'Unable to read configuration JSON'
    Set-Content $settings '{"IsEncrypted":true,"Values":{}}'
    Expect-Failure { & $helper -LocalSettingsPath $settings } 'Encrypted Functions settings'

    $values = @{}
    foreach ($key in @('COMMUNICATION_SERVICES_CONNECTION_STRING',
        'EmailFrom', 'EmailTo', 'AdminApiKey', 'AzureOpenAIKey')) {
        $values[$key] = 'synthetic-private'
    }
    $values.CustomDomain = 'https://short.example.com'
    $values.DefaultRedirectUrl = 'https://example.com'
    $values.AzureOpenAIEndpoint = 'https://ai.example.com'
    $values['ConnectionStrings:social-accounts'] = 'Endpoint=https://social-config.example.com;Id=fixture;Secret=synthetic-private'
    $values['ConnectionStrings:social-accounts-readonly'] = 'Endpoint=https://social-config.example.com;Id=reader;Secret=synthetic-private'
    $values.DeploymentName = 'synthetic-model'
    @{ IsEncrypted = $false; Values = $values } | ConvertTo-Json | Set-Content $settings

    # Exercise user-secret import without opening the real user's secret store.
    $env:APPDATA = $scratch
    $env:HOME = $scratch
    $project = Join-Path $scratch 'Fixture.csproj'
    Set-Content $project '<Project><PropertyGroup><UserSecretsId>fixture</UserSecretsId></PropertyGroup></Project>'
    $base = if ($IsWindows) { Join-Path $scratch 'Microsoft/UserSecrets/fixture' } else { Join-Path $scratch '.microsoft/usersecrets/fixture' }
    New-Item -ItemType Directory -Path $base -Force | Out-Null
    $secrets = @{
        'ProductionDeployment:SubscriptionId' = 'synthetic-subscription'
        'ProductionDeployment:Location' = 'synthetic-region'
        'ProductionDeployment:ResourceGroup' = 'synthetic-group'
        'ProductionDeployment:CustomDomain' = 'https://production.example.com'
        'ProductionDeployment:RedirectCertificateName' = 'production.example.com-managed'
        'ProductionDeployment:DefaultRedirectUrl' = 'https://fallback.example.com'
        'ProductionDeployment:ExistingStorageAccount' = 'productionstorage'
        'ProductionDeployment:ExistingStorageResourceGroup' = 'production-data-group'
        'Parameters:custom-domain' = 'https://localhost:5001'
        'Parameters:default-redirect-url' = 'https://localhost:5001/signin-oidc'
    }
    foreach ($key in @('existing-storage-account', 'existing-storage-resource-group', 'entra-tenant-id',
        'entra-client-id', 'entra-client-secret', 'administrator-object-id')) {
        $secrets["Parameters:$key"] = 'synthetic-private'
    }
    $secrets | ConvertTo-Json | Set-Content (Join-Path $base 'secrets.json')
    $options = @{ LocalSettingsPath = $settings; LoadUserSecrets = $true; AppHostProject = $project }
    $result = & $helper @options
    Assert ($global:ProductionDeploymentTestState.Invocations.Count -eq 0) 'Default mode must only validate.'
    Assert ([string]::IsNullOrEmpty($env:ConnectionStrings__chat)) 'Validation must restore imported environment.'

    # Inherited timer opt-in must not bypass explicit cutover switch.
    [Environment]::SetEnvironmentVariable('Parameters__scheduler-disabled', 'false', 'Process')
    [Environment]::SetEnvironmentVariable('Parameters__coffee-scheduler-disabled', 'false', 'Process')
    $env:Parameters__coffee_scheduler_disabled = 'false'
    $env:Parameters__admin_api_key = 'synthetic-environment-override'
    $result = & $helper @options -Mode Publish -OutputPath $scratch
    $observed = $global:ProductionDeploymentTestState.Observed
    $invocations = $global:ProductionDeploymentTestState.Invocations
    Assert ($observed['Parameters__scheduler-disabled'] -eq 'true') 'Publish must default to disabled scheduler.'
    Assert ($observed['Parameters__coffee-scheduler-disabled'] -eq 'true' -and
        $observed['Parameters__coffee_scheduler_disabled'] -eq 'true') 'Publish must normalize both Coffee forms to disabled.'
    Assert ($observed['Parameters__azure-openai-deployment-name'] -eq 'synthetic-model') 'AI deployment must map from Functions settings.'
    Assert ($observed['Parameters__admin-api-key'] -eq 'synthetic-environment-override') 'Environment must override local configuration.'
    Assert ($observed['Parameters__custom-domain'] -eq 'https://production.example.com') 'Production domain must override local Parameters.'
    Assert ($observed['Parameters__redirect-domain-name'] -eq 'production.example.com' -and
        $observed['Parameters__redirect_domain_name'] -eq 'production.example.com') 'Ingress hostname must be derived from the production URL in both parameter forms.'
    Assert ($observed['Parameters__redirect-certificate-name'] -eq 'production.example.com-managed') 'Production managed certificate must be imported.'
    Assert ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('Parameters__redirect-domain-name'))) 'Derived hostname must be restored.'
    Assert ($observed['Parameters__default-redirect-url'] -eq 'https://fallback.example.com') 'Production fallback must override local Parameters.'
    Assert ($observed['Parameters__existing-storage-account'] -eq 'productionstorage') 'Production storage must override local Parameters.'
    Assert ($observed['Parameters__existing-storage-resource-group'] -eq 'production-data-group') 'Production storage group must override local Parameters.'
    Assert ($observed['ConnectionStrings__chat'] -match 'endpoint=https://ai.example.com' -and
        $observed['ConnectionStrings__chat'] -match 'key=synthetic-private') 'Endpoint/key must become the Aspire chat connection.'
    Assert (($result -join ' ') -notlike '*synthetic-private*') 'Tool output must be suppressed.'
    Assert ($invocations[0][0] -eq 'publish' -and $invocations[0] -contains '--non-interactive') 'Publish must be noninteractive.'
    Assert ([Environment]::GetEnvironmentVariable('Parameters__scheduler-disabled') -eq 'false') 'Inherited timer setting must be restored.'
    Assert ([Environment]::GetEnvironmentVariable('Parameters__coffee-scheduler-disabled') -eq 'false' -and
        $env:Parameters__coffee_scheduler_disabled -eq 'false') 'Inherited Coffee settings must be restored.'
    Assert ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('Parameters__admin-api-key'))) 'Normalized environment must be restored.'

    $env:ConnectionStrings__chat = 'Endpoint=https://existing.example.com;Key=synthetic-private'
    $global:ProductionDeploymentTestState.StorageExitCode = 1
    $previousInvocationCount = $global:ProductionDeploymentTestState.Invocations.Count
    Expect-Failure { & $helper @options -Mode Deploy } 'storage preflight failed'
    Assert ($global:ProductionDeploymentTestState.Invocations.Count -eq $previousInvocationCount) 'Missing storage must fail before invoking Aspire.'
    $global:ProductionDeploymentTestState.StorageExitCode = 0
    $result = & $helper @options -Mode Deploy -EnableScheduler
    $observed = $global:ProductionDeploymentTestState.Observed
    $invocations = $global:ProductionDeploymentTestState.Invocations
    Assert ($invocations[1][0] -eq 'deploy' -and $invocations[1] -contains '--clear-cache') 'Explicit deploy must invalidate parameter cache.'
    Assert ($observed['Parameters__scheduler-disabled'] -eq 'false') 'Explicit cutover must enable timer.'
    Assert ($observed['Parameters__scheduler_disabled'] -eq 'false') 'Shortener gate must normalize both forms.'
    Assert ($observed['Parameters__coffee-scheduler-disabled'] -eq 'true' -and
        $observed['Parameters__coffee_scheduler_disabled'] -eq 'true') 'Shortener opt-in must never enable Coffee.'
    Assert ($observed['ConnectionStrings__chat'] -eq $env:ConnectionStrings__chat) 'Existing chat connection must take precedence.'
    $result = & $helper @options -Mode Deploy -EnableCoffeeScheduler
    $observed = $global:ProductionDeploymentTestState.Observed
    Assert ($observed['Parameters__scheduler-disabled'] -eq 'true' -and
        $observed['Parameters__scheduler_disabled'] -eq 'true') 'Coffee opt-in must never enable shortener.'
    Assert ($observed['Parameters__coffee-scheduler-disabled'] -eq 'false' -and
        $observed['Parameters__coffee_scheduler_disabled'] -eq 'false') 'Explicit Coffee cutover must normalize both forms to enabled.'
    $result = & $helper @options -Mode Deploy -EnableScheduler -EnableCoffeeScheduler
    Assert ($global:ProductionDeploymentTestState.Observed['Parameters__scheduler-disabled'] -eq 'false' -and
        $global:ProductionDeploymentTestState.Observed['Parameters__coffee-scheduler-disabled'] -eq 'false') 'Both schedulers require both switches.'
    $result = & $helper @options -Mode Deploy
    Assert ($global:ProductionDeploymentTestState.Observed['Parameters__scheduler-disabled'] -eq 'true' -and
        $global:ProductionDeploymentTestState.Observed['Parameters__coffee-scheduler-disabled'] -eq 'true') 'A default redeploy must disable both schedulers after cutover.'
    $global:ProductionDeploymentTestState.ExitCode = 1
    Expect-Failure { & $helper @options -Mode Deploy } 'Output suppressed'
    Assert ($env:ConnectionStrings__chat -match 'existing.example.com') 'Failure must restore inherited environment.'
    $env:ConnectionStrings__chat = 'synthetic-private-malformed-connection'
    Expect-Failure { & $helper @options } 'Invalid Azure OpenAI chat connection'
    Assert ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('Parameters__entra-client-secret'))) 'Invalid chat must restore imported secrets.'
    $env:ConnectionStrings__chat = 'Endpoint=https://existing.example.com'
    # The Functions DeploymentName value maps explicitly and never silently defaults in Production.
    $values.DeploymentName = 'synthetic-legacy-model'
    @{ Values = $values } | ConvertTo-Json | Set-Content $settings
    $global:ProductionDeploymentTestState.ExitCode = 0
    $env:Parameters__redirect_certificate_name = 'environment-managed-certificate'
    $env:Parameters__redirect_domain_name = 'incorrect.example.com'
    $result = & $helper @options -Mode Publish
    Assert ($global:ProductionDeploymentTestState.Observed['Parameters__redirect-certificate-name'] -eq 'environment-managed-certificate') 'Environment certificate must override user secrets.'
    Assert ($global:ProductionDeploymentTestState.Observed['Parameters__redirect-domain-name'] -eq 'production.example.com' -and
        $global:ProductionDeploymentTestState.Observed['Parameters__redirect_domain_name'] -eq 'production.example.com') 'Inherited hostname must not diverge from the short-link URL.'
    Assert ($env:Parameters__redirect_domain_name -eq 'incorrect.example.com') 'Inherited hostname must be restored.'
    $env:Parameters__redirect_domain_name = $null
    $env:Parameters__redirect_certificate_name = '/subscriptions/synthetic/certificate'
    Expect-Failure { & $helper @options } 'existing ACA managed certificate name'
    $env:Parameters__redirect_certificate_name = $null
    foreach ($url in @('https://production.example.com/path', 'https://production.example.com?query=1',
        'https://production.example.com#fragment', 'https://production.example.com:8443')) {
        $env:Parameters__custom_domain = $url
        Expect-Failure { & $helper @options } 'HTTPS origin'
    }
    $env:Parameters__custom_domain = $null
    $secrets.Remove('ProductionDeployment:RedirectCertificateName')
    $secrets | ConvertTo-Json | Set-Content (Join-Path $base 'secrets.json')
    $previousInvocationCount = $global:ProductionDeploymentTestState.Invocations.Count
    Expect-Failure { & $helper @options -Mode Deploy } 'Parameters__redirect-certificate-name'
    Assert ($global:ProductionDeploymentTestState.Invocations.Count -eq $previousInvocationCount) 'Missing certificate must fail before deploying.'
    $secrets['ProductionDeployment:RedirectCertificateName'] = 'production.example.com-managed'
    $secrets | ConvertTo-Json | Set-Content (Join-Path $base 'secrets.json')
    $env:Parameters__default_redirect_url = 'https://localhost:5001/signin-oidc'
    Expect-Failure { & $helper @options } 'non-loopback'
    Assert ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('Parameters__default-redirect-url'))) 'Rejected local fallback must restore environment.'
    $env:Parameters__default_redirect_url = $null
    $result = & $helper @options -Mode Publish
    Assert ($global:ProductionDeploymentTestState.Observed['Parameters__azure-openai-deployment-name'] -eq 'synthetic-legacy-model') 'Functions DeploymentName must map explicitly.'
    $values.Remove('DeploymentName')
    @{ Values = $values } | ConvertTo-Json | Set-Content $settings
    Expect-Failure { & $helper @options } 'Parameters__azure-openai-deployment-name'
    Write-Output 'Production helper offline configuration, precedence, redaction, restoration, and independent shortener/Coffee cutover tests passed.'
}
finally {
    Remove-Item Function:aspire
    Remove-Item Function:az
    Remove-Variable ProductionDeploymentTestState -Scope Global -ErrorAction SilentlyContinue
    foreach ($entry in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) {
        if ($entry.Key -match '^(Parameters__|ConnectionStrings__|Azure__)') {
            [Environment]::SetEnvironmentVariable($entry.Key, $null, 'Process')
        }
    }
    foreach ($key in $originalEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $originalEnvironment[$key], 'Process')
    }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
