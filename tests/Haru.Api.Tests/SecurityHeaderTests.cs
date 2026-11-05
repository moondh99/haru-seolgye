using System.Net;
using Haru.Api.Tests.Support;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Haru.Api.Tests.Support.ApiHttp;

namespace Haru.Api.Tests;

public class SecurityHeaderTests(HaruApiFactory factory) : IClassFixture<HaruApiFactory>
{
    private const string ExpectedCsp =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private HttpClient Client => factory.ClientFrom($"10.4.0.{Interlocked.Increment(ref _next)}");

    private static int _next;

    private static readonly string ValidExport = System.Text.Json.JsonSerializer.Serialize(new
    {
        confirmed = true,
        day = DayFromToday(0),
        blocks = new[] { new { id = "b1", title = "보고서", start = "09:00", end = "10:00", kind = "task", reason = "" } },
    });

    public static TheoryData<string, string, string?> Responses => new()
    {
        { "GET", "/api/config", null },
        { "GET", "/api/sample", null },
        { "GET", "/health", null },
        { "GET", "/", null },
        { "GET", "/app.js", null },
        { "GET", "/api/does-not-exist", null },
        { "GET", "/api/plan", null },
        { "POST", "/api/plan", "{}" },
        { "POST", "/api/plan", "{\"text\":\"보고서 쓰기\"}" },
        { "POST", "/api/export", "{}" },
        { "POST", "/api/export", ValidExport },
    };

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = Raw(body);
        return await Client.SendAsync(request);
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Response_Any_HasTheContentSecurityPolicy(string method, string path, string? body)
    {
        var response = await SendAsync(method, path, body);

        Assert.Equal(ExpectedCsp, response.Header("Content-Security-Policy"));
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Response_Any_HasTheHardeningHeaders(string method, string path, string? body)
    {
        var response = await SendAsync(method, path, body);

        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Equal("DENY", response.Header("X-Frame-Options"));
        Assert.Equal("no-referrer", response.Header("Referrer-Policy"));
        Assert.Equal("same-origin", response.Header("Cross-Origin-Opener-Policy"));
        Assert.Equal("camera=(), microphone=(), geolocation=(), payment=()", response.Header("Permissions-Policy"));
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Response_Any_DoesNotAdvertiseTheServer(string method, string path, string? body)
    {
        var response = await SendAsync(method, path, body);

        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False(response.Headers.Contains("X-AspNet-Version"));
    }

    [Fact]
    public void ContentSecurityPolicy_NeverAllowsInlineOrEvalOrWildcardSources()
    {
        Assert.DoesNotContain("unsafe-inline", ExpectedCsp);
        Assert.DoesNotContain("unsafe-eval", ExpectedCsp);
        Assert.DoesNotContain(" * ", ExpectedCsp + " ");
        Assert.DoesNotContain("http:", ExpectedCsp);
    }

    [Fact]
    public async Task Response_OverPlainHttp_HasNoStrictTransportSecurity()
    {
        var response = await Client.GetAsync("/api/config");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Response_BehindATlsProxy_HasStrictTransportSecurity()
    {
        var client = Client;
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/api/config");

        Assert.Equal("max-age=31536000; includeSubDomains", response.Header("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Response_ToACrossOriginRequest_GrantsNoCorsAccess()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/plan");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await Client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task GetApi_Responses_AreNotCachedBySharedCaches()
    {
        var export = await Client.PostAsync("/api/export", Raw(ValidExport));

        Assert.Contains("no-store", export.Header("Cache-Control"));
    }

    // the front end has to work under that policy

    [Fact]
    public async Task GetIndex_ServedPage_HasNoInlineScriptStyleOrHandlers()
    {
        var response = await Client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-cache", response.Header("Cache-Control"));
        Assert.DoesNotMatch(@"<script(?![^>]*\ssrc=)", html);
        Assert.DoesNotMatch(@"<style", html);
        Assert.DoesNotMatch(@"\sstyle\s*=", html);
        Assert.DoesNotMatch(@"\son[a-z]+\s*=", html);
        Assert.DoesNotMatch(@"javascript:", html);
    }

    [Fact]
    public async Task GetAppScript_ServerTextIsNeverParsedAsHtml()
    {
        var script = await (await Client.GetAsync("/app.js")).Content.ReadAsStringAsync();

        Assert.DoesNotMatch(@"\.(innerHTML|outerHTML)\s*=", script);
        Assert.DoesNotContain("insertAdjacentHTML", script);
        Assert.DoesNotContain("document.write", script);
        Assert.DoesNotMatch(@"\beval\s*\(", script);
        Assert.DoesNotMatch(@"new\s+Function\s*\(", script);
    }
}

public class HostConfigurationTests(HaruApiFactory factory) : IClassFixture<HaruApiFactory>
{
    [Fact]
    public void Kestrel_Options_HideTheServerHeaderAndCapTheRequestBody()
    {
        var kestrel = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(kestrel.AddServerHeader);
        Assert.Equal(64 * 1024, kestrel.Limits.MaxRequestBodySize);
    }

    [Fact]
    public async Task PostExport_UnexpectedFailure_Returns500InternalWithoutDetails()
    {
        using var crashing = new HaruApiFactory();
        crashing.Mcp.Throw("validate_schedule", new InvalidOperationException("secret-stack-detail"));
        var request = Json(new
        {
            confirmed = true,
            day = DayFromToday(0),
            blocks = new[] { new { id = "b1", title = "보고서", start = "09:00", end = "10:00", kind = "task", reason = "" } },
        });

        var response = await crashing.ClientFrom("10.5.0.1").PostAsync("/api/export", request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("INTERNAL", System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("secret-stack-detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    [Fact]
    public async Task PostExport_UnexpectedFailure_StillHasTheSecurityHeaders()
    {
        using var crashing = new HaruApiFactory();
        crashing.Mcp.Throw("validate_schedule", new InvalidOperationException("boom"));
        var request = Json(new
        {
            confirmed = true,
            day = DayFromToday(0),
            blocks = new[] { new { id = "b1", title = "보고서", start = "09:00", end = "10:00", kind = "task", reason = "" } },
        });

        var response = await crashing.ClientFrom("10.5.0.2").PostAsync("/api/export", request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.NotEmpty(response.Header("Content-Security-Policy"));
    }
}
