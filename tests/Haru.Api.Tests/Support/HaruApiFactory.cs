using Haru.Api.Agents;
using Haru.Api.Config;
using Haru.Api.Copilot;
using Haru.Api.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Haru.Api.Tests.Support;

/// <summary>Copilot host that never starts a client. Availability is a switch.</summary>
public sealed class FakeCopilotHost(HaruOptions options) : CopilotHost(options, NullLogger<CopilotHost>.Instance)
{
    public bool Available { get; set; } = true;

    public override Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);
}

/// <summary>The real Haru.Api app with the agent runner, the MCP gateway and the Copilot host replaced by offline fakes.</summary>
public sealed class HaruApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly bool _fakeHost;

    /// <summary>The single public constructor, so that xunit can use the factory as a class fixture.</summary>
    public HaruApiFactory() : this(true)
    {
    }

    /// <param name="aiAvailable">Availability reported by the fake Copilot host.</param>
    /// <param name="settings">Configuration keys, such as HARU_PLANS_PER_WINDOW, read by the app at start-up.</param>
    /// <param name="fakeHost">False keeps the real CopilotHost. Only safe with HARU_AI_ENABLED=false, which never starts a client.</param>
    internal HaruApiFactory(bool aiAvailable, Dictionary<string, string?>? settings = null, bool fakeHost = true)
    {
        AiAvailable = aiAvailable;
        _settings = settings ?? [];
        _fakeHost = fakeHost;
    }

    public bool AiAvailable { get; }

    public FakeAgentRunner Runner { get; } = new FakeAgentRunner().ScriptHappy();

    public FakeMcpGateway Mcp { get; } = new();

    /// <summary>The fake Copilot host, to flip availability while the app runs. Only for factories with fakeHost.</summary>
    public FakeCopilotHost Host => (FakeCopilotHost)Services.GetRequiredService<CopilotHost>();

    /// <summary>A client that looks like a different caller: the app rate-limits by the forwarded client address.</summary>
    public HttpClient ClientFrom(string ip)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentRunner>();
            services.AddSingleton<IAgentRunner>(Runner);
            services.RemoveAll<IMcpGateway>();
            services.AddSingleton<IMcpGateway>(Mcp);
            if (!_fakeHost) return;

            services.RemoveAll<CopilotHost>();
            services.AddSingleton<CopilotHost>(sp =>
                new FakeCopilotHost(sp.GetRequiredService<HaruOptions>()) { Available = AiAvailable });
        });
    }
}
