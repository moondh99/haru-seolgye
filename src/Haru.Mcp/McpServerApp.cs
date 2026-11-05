using Haru.Mcp.Tools;

namespace Haru.Mcp;

/// <summary>Builds the MCP server host. Kept apart from Program.cs so tests can start the real server on any port.</summary>
public static class McpServerApp
{
    /// <param name="configure">Optional hook that runs before the host is built (tests use it to capture logs).</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddServiceDefaults();
        configure?.Invoke(builder);

        // ASPNETCORE_URLS, HTTP_PORTS and --urls are handled by ASP.NET Core itself. Platforms that only set PORT are covered here.
        if (int.TryParse(builder.Configuration["PORT"], out var port)
            && string.IsNullOrEmpty(builder.Configuration["URLS"])
            && string.IsNullOrEmpty(builder.Configuration["HTTP_PORTS"])
            && string.IsNullOrEmpty(builder.Configuration["HTTPS_PORTS"]))
        {
            builder.WebHost.UseUrls($"http://+:{port}");
        }

        // Tool payloads are tiny (50 short blocks at most); refuse anything bigger early.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 512 * 1024);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<HaruTools>(HaruTools.JsonOptions)
            .WithRequestFilters(filters => filters.AddCallToolFilter(MalformedArgumentsFilter.Create()));

        var app = builder.Build();

        app.MapDefaultEndpoints();
        app.MapMcp("/mcp");
        return app;
    }
}
