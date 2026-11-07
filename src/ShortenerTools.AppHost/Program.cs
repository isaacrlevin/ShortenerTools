using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var isStaging = builder.Environment.IsStaging();
// A new production deployment must not race the still-live legacy timer.
// Only an explicit cutover input may enable the ACA scheduler.
var schedulerDisabled = builder.AddParameter("scheduler-disabled", "true", publishValueAsDefault: true);
// Coffee cutover is independent of shortener posting and its legacy host.
var coffeeSchedulerDisabled = builder.AddParameter("coffee-scheduler-disabled", "true", publishValueAsDefault: true);
string[] coffeeTimers = ["COSSTeaserTimer", "COSSAnnouncementTimer", "COSSArchiveTimer"];
var coffeePostingAllowed = !builder.ExecutionContext.IsRunMode && builder.Environment.IsProduction();
var aiDeploymentName = isStaging
    ? null
    : builder.ExecutionContext.IsRunMode
        ? builder.AddParameter("azure-openai-deployment-name",
            builder.Configuration["Parameters:azure-openai-deployment-name"] ?? "gpt-4o-mini")
        : builder.AddParameter("azure-openai-deployment-name");

var useOllama = !isStaging && builder.ExecutionContext.IsRunMode
                           && builder.Configuration.GetValue("Ollama:UseLocalModel", true);
var useGpu = builder.Configuration.GetValue<bool>("Ollama:UseGpu");

var chat = isStaging
    ? null
    : useOllama
        ? builder.AddOllama("ollama")
            .WithDataVolume()
            .WithContainerRuntimeArgs(useGpu ? ["--gpus=all"] : [])
            .WithOpenWebUI()
            .AddModel("chat", "llama3")
        : builder.AddConnectionString("chat");

// The URL tables stay in their existing account. ACA references it by resource
// ID and grants data-plane access to managed identities; Functions host state
// uses a separate account.
var storage = builder.AddAzureStorage("storage");
if (builder.ExecutionContext.IsRunMode)
{
    storage.RunAsEmulator();
}
else if (!isStaging)
{
    var existingStorageName = builder.AddParameter("existing-storage-account");
    var existingStorageResourceGroup = builder.AddParameter("existing-storage-resource-group");
    storage.AsExisting(existingStorageName, existingStorageResourceGroup);
}

var tables = storage.AddTables("tables");
var useProductionData = !isStaging && builder.ExecutionContext.IsRunMode
                                   && builder.Configuration.GetValue<bool>("Storage:UseProduction");
var productionData = useProductionData ? builder.AddConnectionString("production-data") : null;
var hostStorage = builder.AddAzureStorage("functions-host");
if (builder.ExecutionContext.IsRunMode)
{
    hostStorage.RunAsEmulator();
}

var entraTenantId = builder.AddParameter("entra-tenant-id");
var entraClientId = builder.AddParameter("entra-client-id");
var entraClientSecret = builder.AddParameter("entra-client-secret", secret: true);
var administratorObjectId = builder.AddParameter("administrator-object-id");
var adminApiKey = builder.AddParameter("admin-api-key", secret: true);
var socialAccountsConnectionStringConfigured = !string.IsNullOrWhiteSpace(
    builder.Configuration["Parameters:social-accounts-connection-string"]);
var socialAccountsReadOnlyConnectionStringConfigured = !string.IsNullOrWhiteSpace(
    builder.Configuration["Parameters:social-accounts-readonly-connection-string"]);
IResourceBuilder<ParameterResource>? socialAccountsConnectionString = null;
IResourceBuilder<ParameterResource>? socialAccountsReadOnlyConnectionString = null;
if (!builder.ExecutionContext.IsRunMode || socialAccountsConnectionStringConfigured)
{
    socialAccountsConnectionString = builder.AddParameter("social-accounts-connection-string", secret: true);
}

if (!builder.ExecutionContext.IsRunMode || socialAccountsReadOnlyConnectionStringConfigured)
{
    socialAccountsReadOnlyConnectionString =
        builder.AddParameter("social-accounts-readonly-connection-string", secret: true);
}

// Reserved .invalid hosts prevent staging from defaulting to a production URL.
// Operators replace the short-link base after ACA assigns the redirect hostname.
var customDomain = isStaging
    ? builder.AddParameter("custom-domain",
        builder.Configuration["Parameters:custom-domain"] ?? "https://shortener.staging.invalid",
        publishValueAsDefault: true)
    : builder.AddParameter("custom-domain");
var redirectDomainName = builder.ExecutionContext.IsRunMode || isStaging
    ? null
    : builder.AddParameter("redirect-domain-name");
var redirectCertificateName = builder.ExecutionContext.IsRunMode || isStaging
    ? null
    : builder.AddParameter("redirect-certificate-name");
var defaultRedirectUrl = isStaging
    ? builder.AddParameter("default-redirect-url",
        builder.Configuration["Parameters:default-redirect-url"] ?? "https://redirect.staging.invalid",
        publishValueAsDefault: true)
    : builder.AddParameter("default-redirect-url");
var scheduleSettings = new Dictionary<string, IResourceBuilder<ParameterResource>>();
if (!builder.ExecutionContext.IsRunMode && !isStaging)
{
    scheduleSettings["COMMUNICATION_SERVICES_CONNECTION_STRING"] = builder.AddParameter(
        "communication-services-connection-string", secret: true);
    scheduleSettings["EmailFrom"] = builder.AddParameter("email-from", secret: true);
    scheduleSettings["EmailTo"] = builder.AddParameter("email-to", secret: true);
}

var aca = builder.AddAzureContainerAppEnvironment("aca");
// SocialPoster keeps its database (accounts, GitHub Copilot sign-in, contacts, history) and the data-protection keys
// that decrypt them on disk. Container filesystems are replaced on every revision, so deployed runs mount an
// Azure Files share instead; local runs keep using the working directory.
const string socialPosterShare = "socialposter-data";
const string socialPosterMount = "/mnt/socialposter-data";
if (!builder.ExecutionContext.IsRunMode)
{
    aca.ConfigureInfrastructure(infrastructure =>
    {
        var environment = infrastructure.GetProvisionableResources().OfType<ContainerAppManagedEnvironment>().Single();
        var files = new StorageAccount("socialposterfiles")
        {
            Name = BicepFunction.Take(
                BicepFunction.Interpolate($"socialposter{BicepFunction.GetUniqueString(BicepFunction.GetResourceGroup().Id)}"), 24),
            Kind = StorageKind.StorageV2,
            Sku = new StorageSku { Name = StorageSkuName.StandardLrs },
            // Container Apps authenticate to Azure Files with an account key.
            AllowSharedKeyAccess = true,
            AllowBlobPublicAccess = false,
            MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2
        };
        var fileService = new FileService("socialposterfileservice") { Parent = files };
        var share = new Azure.Provisioning.Storage.FileShare("socialposterfileshare")
        {
            Parent = fileService, Name = socialPosterShare
        };
        var mount = new ContainerAppManagedEnvironmentStorage("socialpostermount")
        {
            Parent = environment,
            Name = socialPosterShare,
            Properties = new ManagedEnvironmentStorageProperties
            {
                AzureFile = new ContainerAppAzureFileProperties
                {
                    AccountName = files.Name,
                    // Resolves to files.listKeys().keys[0].value in the generated Bicep.
                    AccountKey = new BicepValue<string>(new MemberExpression(new IndexExpression(
                        new MemberExpression(new FunctionCallExpression(new MemberExpression(
                            new IdentifierExpression(files.BicepIdentifier), "listKeys")), "keys"),
                        new IntLiteralExpression(0)), "value")),
                    ShareName = share.Name,
                    AccessMode = ContainerAppAccessMode.ReadWrite
                }
            }
        };
        infrastructure.Add(files);
        infrastructure.Add(fileService);
        infrastructure.Add(share);
        infrastructure.Add(mount);
    });
}
// Only publishing/deployment models Azure telemetry resources. Local runs use
// the Aspire dashboard without provisioning Azure or requiring an Insights secret.
var appInsights = builder.ExecutionContext.IsRunMode
    ? null
    : builder.AddAzureApplicationInsights("app-insights");

var redirect = builder.AddAzureFunctionsProject<Projects.ShortenerTools_Functions>("shortenertools-functions")
    .WithExternalHttpEndpoints()
    .WithHostStorage(hostStorage)
    .WithRoleAssignments(
        hostStorage,
        StorageBuiltInRole.StorageBlobDataContributor,
        StorageBuiltInRole.StorageQueueDataContributor,
        StorageBuiltInRole.StorageAccountContributor)
    .WithRoleAssignments(storage, StorageBuiltInRole.StorageTableDataContributor)
    .WithReference(tables)
    .WithEnvironment("AzureFunctionsJobHost__functions__0", "UrlRedirect")
    .WithEnvironment("AzureFunctionsWebHost__hostid", "shortener-redirect")
    .WithEnvironment("EnableDescriptionGeneration", "false")
    .WithEnvironment("AZURE_FUNCTIONS_ENVIRONMENT", builder.Environment.EnvironmentName)
    // UrlRedirect reads only the table endpoint and DefaultRedirectUrl.
    .WithEnvironment("DefaultRedirectUrl", defaultRedirectUrl)
    .WithEnvironment("AzureWebJobs.ShortPostTimer.Disabled", "true")
    .WithEnvironment("IN_ASPIRE", "true")
    .PublishAsAzureContainerApp((infrastructure, app) =>
    {
        // The public redirect endpoint stays warm and has exactly one replica.
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;
        app.Template.Containers[0].Value.Resources.Cpu = 0.25;
        app.Template.Containers[0].Value.Resources.Memory = "0.5Gi";
        if (redirectDomainName is not null && redirectCertificateName is not null)
        {
            BindUploadedCertificate(infrastructure, app, redirectDomainName, redirectCertificateName);
        }
    });
if (builder.ExecutionContext.IsRunMode)
{
    redirect.WithEnvironment("DataStorage",
        productionData?.Resource.ConnectionStringExpression ?? tables.Resource.ConnectionStringExpression);
}

var managementApiProject = builder.AddAzureFunctionsProject<Projects.ShortenerTools_Functions>("management-api");
if (!isStaging && !builder.ExecutionContext.IsRunMode)
{
    managementApiProject.WithExternalHttpEndpoints();
}

var managementApi = managementApiProject
    .WithHostStorage(hostStorage)
    .WithRoleAssignments(hostStorage,
        StorageBuiltInRole.StorageBlobDataContributor,
        StorageBuiltInRole.StorageQueueDataContributor,
        StorageBuiltInRole.StorageAccountContributor)
    .WithRoleAssignments(storage, StorageBuiltInRole.StorageTableDataContributor)
    .WithReference(tables)
    .WithEnvironment("AdminApiKey", adminApiKey)
    .WithEnvironment("CustomDomain", customDomain)
    .WithEnvironment("AzureFunctionsWebHost__hostid", "shortener-management")
    .WithEnvironment("AzureWebJobs.ShortPostTimer.Disabled", "true")
    .WithEnvironment("IN_ASPIRE", "true")
    .WithEnvironment("USE_OLLAMA", useOllama.ToString())
    .WithEnvironment("AZURE_FUNCTIONS_ENVIRONMENT", builder.Environment.EnvironmentName)
    // Local runs are a dry run: nothing is sent to real social accounts.
    .WithEnvironment("DisableExternalPosting", (isStaging || builder.ExecutionContext.IsRunMode).ToString())
    // Production administrators may invoke Coffee manually; timer opt-in is
    // separate. Local/staging Coffee never sends, even with live-data opt-ins.
    .WithEnvironment("PostSocials", coffeePostingAllowed.ToString())
    .WithEnvironment("EnableDescriptionGeneration", (!isStaging).ToString())
    .PublishAsAzureContainerApp((_, app) =>
    {
        // Admin publishing calls this API interactively; a ~20s cold start stalled Publish, so keep it warm.
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;
    });

string[] managementFunctions =
[
    "UrlList", "UrlCreate", "UrlCheck", "UrlUpdate", "UrlArchive", "UrlStats",
    "UrlClickStatsByDay", "UrlPostVersions", "CreateDescription", "ShortPostHttp", "TestShortUrl",
    "COSSPublishHttp", "COSSTeaserHttp", "COSSAnnouncementHttp", "COSSArchiveHttp"
];
for (var index = 0; index < managementFunctions.Length; index++)
{
    managementApi.WithEnvironment($"AzureFunctionsJobHost__functions__{index}", managementFunctions[index]);
}

if (builder.ExecutionContext.IsRunMode)
{
    managementApi.WithEnvironment("DataStorage",
        productionData?.Resource.ConnectionStringExpression ?? tables.Resource.ConnectionStringExpression);
}

if (socialAccountsReadOnlyConnectionString is not null)
{
    managementApi.WithEnvironment("ConnectionStrings__social-accounts", socialAccountsReadOnlyConnectionString);
}

if (chat is not null)
{
    managementApi.WithReference(chat);
    managementApi.WithEnvironment("DeploymentName", aiDeploymentName!);
    if (useOllama)
    {
        managementApi.WaitFor(chat);
    }
}

// Keep shortener and Coffee schedules on one non-public Functions app.
// Neither is copied into the public redirect app; opt in to each at cutover.
var scheduledPosts = builder
    .AddAzureFunctionsProject<Projects.ShortenerTools_Functions>("shortenertools-scheduled-posts")
    .WithHostStorage(hostStorage)
    .WithRoleAssignments(
        hostStorage,
        StorageBuiltInRole.StorageBlobDataContributor,
        StorageBuiltInRole.StorageQueueDataContributor,
        StorageBuiltInRole.StorageAccountContributor)
    .WithRoleAssignments(storage, StorageBuiltInRole.StorageTableDataContributor)
    .WithReference(tables)
    .WithEnvironment("CustomDomain", customDomain)
    .WithEnvironment("IN_ASPIRE", "true")
    .WithEnvironment("EnableDescriptionGeneration", "false")
    .WithEnvironment("AZURE_FUNCTIONS_ENVIRONMENT", builder.Environment.EnvironmentName)
    .WithEnvironment("DisableExternalPosting", isStaging.ToString())
    .WithEnvironment("PostSocials", coffeePostingAllowed.ToString())
    .WithEnvironment("AzureFunctionsWebHost__hostid", "shortener-scheduler")
    .WithEnvironment("AzureFunctionsJobHost__functions__0", "ShortPostTimer")
    .PublishAsAzureContainerApp((infrastructure, app) =>
    {
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;
        if (!isStaging)
        {
            // Preserve each AppHost parameter's default while carrying explicit
            // cutover values into the standalone scheduler module.
            schedulerDisabled.Resource
                .AsProvisioningParameter(infrastructure, "scheduler_disabled_value");
            coffeeSchedulerDisabled.Resource
                .AsProvisioningParameter(infrastructure, "coffee_scheduler_disabled_value");
        }
    });
for (var index = 0; index < coffeeTimers.Length; index++)
{
    scheduledPosts.WithEnvironment($"AzureFunctionsJobHost__functions__{index + 1}", coffeeTimers[index]);
}

foreach (var timer in coffeeTimers)
{
    redirect.WithEnvironment($"AzureWebJobs.{timer}.Disabled", "true");
    managementApi.WithEnvironment($"AzureWebJobs.{timer}.Disabled", "true");
    if (builder.ExecutionContext.IsRunMode || isStaging)
    {
        scheduledPosts.WithEnvironment($"AzureWebJobs.{timer}.Disabled", "true");
    }
    else
    {
        scheduledPosts.WithEnvironment($"AzureWebJobs.{timer}.Disabled", coffeeSchedulerDisabled);
    }
}

if (builder.ExecutionContext.IsRunMode || isStaging)
{
    scheduledPosts.WithEnvironment("AzureWebJobs.ShortPostTimer.Disabled", "true");
}
else
{
    scheduledPosts.WithEnvironment("AzureWebJobs.ShortPostTimer.Disabled", schedulerDisabled);
}

if (builder.ExecutionContext.IsRunMode)
{
    scheduledPosts.WithEnvironment("DataStorage", tables.Resource.ConnectionStringExpression);
}

if (socialAccountsReadOnlyConnectionString is not null)
{
    scheduledPosts.WithEnvironment("ConnectionStrings__social-accounts", socialAccountsReadOnlyConnectionString);
}

foreach (var (settingName, parameter) in scheduleSettings)
{
    scheduledPosts.WithEnvironment(settingName, parameter);
    managementApi.WithEnvironment(settingName, parameter);
}

var admin = builder.AddProject<Projects.ShortenerTools_Admin>("admin")
    .WithExternalHttpEndpoints()
    .WithReference(managementApi)
    .WithEnvironment("Entra__TenantId", entraTenantId)
    .WithEnvironment("Entra__ClientId", entraClientId)
    .WithEnvironment("Entra__ClientSecret", entraClientSecret)
    .WithEnvironment("Entra__AdministratorObjectId", administratorObjectId)
    .WithEnvironment("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")
    .WithEnvironment("AdminApiKey", adminApiKey)
    // The container user cannot write under /app, so deployed runs use the Azure Files mount declared above.
    .WithEnvironment(context =>
    {
        if (!builder.ExecutionContext.IsRunMode) context.EnvironmentVariables["SocialPoster__DataDirectory"] = socialPosterMount;
    })
    .PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Volumes.Add(new ContainerAppVolume
        {
            Name = socialPosterShare,
            StorageType = ContainerAppStorageType.AzureFile,
            StorageName = socialPosterShare,
            // SQLite cannot take byte-range locks over SMB; nobrl makes the share behave for a single writer.
            MountOptions = "nobrl,dir_mode=0777,file_mode=0666,uid=0,gid=0"
        });
        app.Template.Containers[0].Value!.VolumeMounts.Add(new ContainerAppVolumeMount
        {
            VolumeName = socialPosterShare,
            MountPath = socialPosterMount
        });
        // SocialPoster scheduling runs in the Admin host and must keep polling while idle.
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;
    });
if (socialAccountsConnectionString is not null)
{
    admin.WithEnvironment("ConnectionStrings__social-accounts", socialAccountsConnectionString);
}

if (appInsights is not null)
{
    admin.WithReference(appInsights);
    foreach (var functions in new[] { redirect, managementApi, scheduledPosts })
    {
        functions.WithReference(appInsights);
    }
}

// The host and worker share a role and trace context; each app has a distinct
// service.name even though the three Functions apps share the same executable.
admin.WithEnvironment("OTEL_SERVICE_NAME", admin.Resource.Name);
foreach (var functions in new[] { redirect, managementApi, scheduledPosts })
{
    functions.WithEnvironment("OTEL_SERVICE_NAME", functions.Resource.Name);
    functions.WithEnvironment("AzureFunctionsJobHost__telemetryMode", "OpenTelemetry");
}

builder.Build().Run();

// Binds the short-link hostname to a certificate uploaded to the ACA environment (the Cloudflare Origin CA certificate
// for the proxied short-link domain). Aspire's ConfigureCustomDomain only targets ACA managed certificates
// (.../managedCertificates/{name}); uploaded certificates live at .../certificates/{name}. An empty certificate name
// keeps the hostname declared but disables the binding, matching ConfigureCustomDomain's bootstrap behavior.
static void BindUploadedCertificate(
    AzureResourceInfrastructure infrastructure,
    ContainerApp app,
    IResourceBuilder<ParameterResource> domainName,
    IResourceBuilder<ParameterResource> certificateName)
{
    var environmentId = infrastructure.GetProvisionableResources().OfType<ProvisioningParameter>()
        .Single(parameter => parameter.BicepIdentifier.EndsWith(
            "outputs_azure_container_apps_environment_id", StringComparison.Ordinal));
    var domain = domainName.AsProvisioningParameter(infrastructure);
    var certificate = certificateName.AsProvisioningParameter(infrastructure);
    var hasCertificate = new BinaryExpression(
        new IdentifierExpression(certificate.BicepIdentifier), BinaryBicepOperator.NotEqual,
        new StringLiteralExpression(string.Empty));

    app.Configuration.Ingress.CustomDomains.Add(new ContainerAppCustomDomain
    {
        Name = new BicepValue<string>(new IdentifierExpression(domain.BicepIdentifier)),
        BindingType = new BicepValue<ContainerAppCustomDomainBindingType>(new ConditionalExpression(
            hasCertificate, new StringLiteralExpression("SniEnabled"), new StringLiteralExpression("Disabled"))),
        CertificateId = new BicepValue<Azure.Core.ResourceIdentifier>(new ConditionalExpression(
            hasCertificate,
            new InterpolatedStringExpression(
            [
                new IdentifierExpression(environmentId.BicepIdentifier),
                new StringLiteralExpression("/certificates/"),
                new IdentifierExpression(certificate.BicepIdentifier)
            ]),
            new NullLiteralExpression()))
    });
}
