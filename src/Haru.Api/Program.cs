using Haru.Api.Agents;
using Haru.Api.Config;
using Haru.Api.Contracts;
using Haru.Api.Copilot;
using Haru.Api.Endpoints;
using Haru.Api.Infrastructure;
using Haru.Api.Mcp;
using Haru.Api.Workflow;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024;
});

var options = HaruOptions.From(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<CopilotHost>();
builder.Services.AddSingleton<AgentCatalog>();
builder.Services.AddSingleton<PromptLoader>(sp => new PromptLoader(sp.GetRequiredService<AgentCatalog>()));
builder.Services.AddSingleton<IMcpGateway, McpGateway>();
builder.Services.AddSingleton<IAgentRunner, CopilotAgentRunner>();
builder.Services.AddSingleton<AgentCaller>();
builder.Services.AddSingleton<PlanService>();
builder.Services.AddSingleton<UsageLimiter>();
builder.Services.AddSingleton<SampleStore>();
builder.Services.AddHostedService<CopilotWarmup>();

// Container Apps ingress is the only way in, so one proxy hop is trusted.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// Fail at start-up, not on the first request, when instruction files or the sample are missing.
_ = app.Services.GetRequiredService<PromptLoader>();
_ = app.Services.GetRequiredService<SampleStore>();

// Outermost, and written in OnStarting: the exception handler clears the response, so headers set
// earlier would be lost on the 500 path. HSTS reads the scheme at that moment, after UseForwardedHeaders.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var h = context.Response.Headers;
        h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
                                  "connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        if (context.Request.IsHttps) h.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
        return Task.CompletedTask;
    });
    await next();
});
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(
        new { error = new { code = "INTERNAL", message = "서버에서 문제가 생겼습니다. 잠시 후 다시 시도해 주세요." } },
        HaruJson.Options);
}));
app.UseForwardedHeaders();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapDefaultEndpoints();
app.MapHaruApi();

app.Run();

public partial class Program;
