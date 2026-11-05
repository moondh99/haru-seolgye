using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Primitives;

var builder = DistributedApplication.CreateBuilder(args);

// Deploy-time parameters: `Parameters:<name>` (env var `Parameters__<name>`, user-secrets, appsettings)
// wins; otherwise the fallback keeps `aspire deploy` from prompting or failing. ACA secrets cannot be empty,
// so the token fallback is the literal "unset", which Haru.Api treats as "no token" (offline sample).
IResourceBuilder<ParameterResource> AddParameterWithDefault(string name, string fallback, bool secret = false) =>
    builder.AddParameter(
        name,
        () => builder.Configuration[$"Parameters:{name}"] is { Length: > 0 } configured ? configured : fallback,
        secret: secret);

var copilotToken = AddParameterWithDefault("copilot-token", "unset", secret: true);
var modelFast = AddParameterWithDefault("haru-model-fast", "claude-haiku-5.5");
var modelSmart = AddParameterWithDefault("haru-model-smart", "claude-sonnet-5.5");

// One Log Analytics workspace shared by the Container Apps environment and Application Insights.
var logs = builder.AddAzureLogAnalyticsWorkspace("haru-logs");

// The service now creates Express environments when no mode is set. Express rejects session affinity,
// the .NET runtime setting and internal service discovery, so request a workload-profiles environment.
// Azure.Provisioning.AppContainers 1.2.0 has no EnvironmentMode property yet; this shim mirrors Aspire's own AsExpress().
builder.AddAzureContainerAppEnvironment("haru-env")
    .WithAzureLogAnalyticsWorkspace(logs)
    .WithDashboard(false)
    .ConfigureInfrastructure(infra =>
    {
        var environment = infra.GetProvisionableResources().OfType<ContainerAppManagedEnvironment>().Single();
        environment.ResourceVersion = "2026-03-02-preview";
        var mode = new BicepValue<string>("WorkloadProfiles");
        ((IBicepValue)mode).Self = new BicepValueReference(environment, "EnvironmentMode", ["properties", "environmentMode"]);
        environment.ProvisionableProperties["EnvironmentMode"] = mode;
    });

var insights = builder.AddAzureApplicationInsights("haru-insights", logs);

// Always-on replicas so the first request is never a cold start.
void ConfigureApp(ContainerApp app, int minReplicas, int maxReplicas, double cpu, string memory, bool stickySessions = false)
{
    app.Template.Scale.MinReplicas = minReplicas;
    app.Template.Scale.MaxReplicas = maxReplicas;
    var container = app.Template.Containers[0].Value!;
    container.Resources.Cpu = cpu;
    container.Resources.Memory = memory;

    // The api keeps rate-limit and request state in memory, so pin a client to one replica.
    if (stickySessions)
    {
        app.Configuration.Ingress.StickySessionsAffinity = StickySessionAffinity.Sticky;
    }
}

// Container Apps probes. The liveness probe is loose on purpose: a plan keeps the CPU busy and a restart would end it.
// WithHttpProbe is an Aspire evaluation API (ASPIREPROBES001). The package versions are pinned, so the risk is low.
#pragma warning disable ASPIREPROBES001
IResourceBuilder<ProjectResource> WithProbes(IResourceBuilder<ProjectResource> project) => project
    .WithHttpProbe(ProbeType.Liveness, "/alive", initialDelaySeconds: 10, periodSeconds: 15, timeoutSeconds: 5, failureThreshold: 5)
    .WithHttpProbe(ProbeType.Readiness, "/health", periodSeconds: 10, timeoutSeconds: 5, failureThreshold: 3);
#pragma warning restore ASPIREPROBES001

// No WithExternalHttpEndpoints(): ingress stays internal to the Container Apps environment.
var mcp = WithProbes(builder.AddProject<Projects.Haru_Mcp>("mcp"))
    .WithReference(insights)
    .PublishAsAzureContainerApp((_, app) => ConfigureApp(app, 1, 2, 0.25, "0.5Gi"));

WithProbes(builder.AddProject<Projects.Haru_Api>("api"))
    .WithExternalHttpEndpoints()
    .WithReference(insights)
    .WithReference(mcp).WaitFor(mcp)
    .WithEnvironment("COPILOT_GITHUB_TOKEN", copilotToken)
    .WithEnvironment("HARU_MODEL_FAST", modelFast)
    .WithEnvironment("HARU_MODEL_SMART", modelSmart)
    .WithEnvironment("HARU_COPILOT_HOME", "/tmp/haru-copilot")
    .PublishAsAzureContainerApp((_, app) => ConfigureApp(app, 1, 2, 0.5, "1.0Gi", stickySessions: true));

builder.Build().Run();
