using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MockProxyGateway.Tests;

public sealed class ForwardingTests
{
    private const string Body = """{"customer":{"riskScore":10,"document":"TEST-OTHER","type":"Individual"}}""";

    [Fact]
    public async Task PinnedExampleSelectsMockOrRealActionWithCorrectOutboundCount()
    {
        await using var upstream = await Upstream.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mock-proxy-gateway.example.json"), ct))!;
        var destinations = json["destinations"]!.AsObject();
        foreach (var item in destinations)
            item.Value!["baseUrl"] = upstream.BaseUrl + item.Key + "/";
        await using var gateway = await StartGatewayAsync(json, upstream);
        using var client = gateway.GetTestClient();

        // All inputs are synthetic. Each assertion checks actual outgoing call counts.
        await CheckAsync("POST", "/compliance/analysis", 201, expectedUpstream: "/compliance-real/analysis", body: Body);
        await CheckAsync("POST", "/compliance/analysis", 200, "success", expectedBody: "Approved");
        await CheckAsync("POST", "/compliance/analysis", 422, body: """{"customer":{"riskScore":80}}""", expectedBody: "Rejected");
        await CheckAsync("POST", "/compliance/analysis?simulate=unavailable", 503, expectedBody: "Service unavailable");
        await CheckAsync("POST", "/compliance/analysis", 429, "rate-limited", expectedBody: "Too many requests", retryAfter: "30");
        await CheckAsync("POST", "/compliance/analysis", 401, "unauthorized", expectedBody: "Unauthorized");
        await CheckAsync("POST", "/compliance/analysis", 202, body: """{"customer":{"document":"TEST-1001"}}""", expectedBody: "Pending");
        await CheckAsync("POST", "/compliance/analysis", 201, "alternate", expectedUpstream: "/compliance-v2/v2/analysis");
        await CheckAsync("POST", "/compliance/analysis?simulate=unavailable", 201, "real", expectedUpstream: "/compliance-real/analysis?simulate=unavailable");
        await CheckAsync("POST", "/compliance/analysis?simulate=success", 200, session: "session-a", expectedBody: "composite");
        await CheckAsync("POST", "/compliance/analysis", 200, body: """{"products":[{"type":"TestProduct"}]}""", expectedBody: "array");
        await CheckAsync("POST", "/compliance/analysis", 200, cookie: "test-session=session-b", expectedBody: "cookie");
        await CheckAsync("POST", "/compliance/analysis?simulate=unavailable", 503, "success");
        await CheckAsync("POST", "/compliance/analysis", 201, "disabled", expectedUpstream: "/compliance-real/analysis");
        await CheckAsync("POST", "/compliance/analysis", 201, "expired", expectedUpstream: "/compliance-real/analysis");
        await CheckAsync("GET", "/compliance/analysis/analysis-test-1001", 200, expectedBody: "Pending");
        await CheckAsync("GET", "/compliance/health", 200, expectedBody: "Healthy");
        await CheckAsync("GET", "/compliance/legacy/a/b", 201, expectedUpstream: "/compliance-v2/legacy/a/b");
        await CheckAsync("GET", "/compliance/other", 201, expectedUpstream: "/compliance-real/other");
        await CheckAsync("GET", "/documents/anything", 201, expectedUpstream: "/documents-real/anything");
        await CheckAsync("GET", "/unknown/anything", 404);

        var countBefore = upstream.Requests.Count;
        using (var success = await client.GetAsync("/api/health", ct))
            Assert.Equal("normal", await success.Content.ReadAsStringAsync(ct));
        Assert.Equal(countBefore, upstream.Requests.Count);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(150));
        using var delayed = CreateRequest("POST", "/mock-proxy/compliance/analysis", Body, "timeout");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(delayed, cancel.Token));
        Assert.Equal(countBefore, upstream.Requests.Count);

        using var malformed = CreateRequest("POST", "/mock-proxy/compliance/analysis", "{");
        using var invalid = await client.SendAsync(malformed, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(countBefore, upstream.Requests.Count);

        async Task CheckAsync(string method, string path, int status, string? scenario = null, string? expectedBody = null,
            string? expectedUpstream = null, string? body = null, string? session = null, string? cookie = null, string? retryAfter = null)
        {
            var before = upstream.Requests.Count;
            var payload = method == "POST" ? body ?? Body : null;
            using var request = CreateRequest(method, "/mock-proxy" + path, payload, scenario);
            if (session is not null)
                request.Headers.TryAddWithoutValidation("X-Test-Session", session);
            if (cookie is not null)
                request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            if (expectedBody is not null)
                Assert.Contains(expectedBody, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            if (retryAfter is not null)
                Assert.Equal(retryAfter, response.Headers.GetValues("Retry-After").Single());
            Assert.Equal(before + (expectedUpstream is null ? 0 : 1), upstream.Requests.Count);
            if (expectedUpstream is not null)
            {
                var forwarded = upstream.Requests.Last();
                Assert.Equal(expectedUpstream, forwarded.Target);
                Assert.Equal(method, forwarded.Method);
                Assert.Equal(payload ?? "", forwarded.Body);
                Assert.Equal("from-upstream", response.Headers.GetValues("X-Upstream").Single());
            }
        }
    }

    [Fact]
    public async Task CredentialPolicyIsPerDestinationAndRedirectIsNotFollowed()
    {
        await using var upstream = await Upstream.StartAsync();
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"restricted":{"baseUrl":"http://127.0.0.1/"},
              "permitted":{"baseUrl":"http://127.0.0.1/","forwardCredentials":["Authorization","Cookie"],"removeHeaders":["X-Test-Scenario"],"removeQuery":["simulate"]}},
              "services":{"a":{"default":{"mode":"proxy","destination":"restricted"}},
                          "b":{"default":{"mode":"proxy","destination":"permitted"}}}}
            """)!;
        json["destinations"]!["restricted"]!["baseUrl"] = upstream.BaseUrl + "restricted/";
        json["destinations"]!["permitted"]!["baseUrl"] = upstream.BaseUrl + "permitted/";
        await using var gateway = await StartGatewayAsync(json, upstream);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        using var restricted = CreateRequest("GET", "/mock-proxy/a/resource?simulate=keep", null, "keep");
        restricted.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-token");
        restricted.Headers.TryAddWithoutValidation("Cookie", "synthetic=cookie");
        using var restrictedResponse = await client.SendAsync(restricted, ct);
        Assert.Equal(HttpStatusCode.Created, restrictedResponse.StatusCode);
        Assert.Equal("", upstream.Requests.Last().Authorization);
        Assert.Equal("", upstream.Requests.Last().Cookie);
        Assert.Equal("keep", upstream.Requests.Last().Scenario);

        using var permitted = CreateRequest("GET", "/mock-proxy/b/resource?simulate=remove&business=keep", null, "remove");
        permitted.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-token");
        permitted.Headers.TryAddWithoutValidation("Cookie", "synthetic=cookie");
        using var permittedResponse = await client.SendAsync(permitted, ct);
        Assert.Equal(HttpStatusCode.Created, permittedResponse.StatusCode);
        var actual = upstream.Requests.Last();
        Assert.Equal("Bearer synthetic-token", actual.Authorization);
        Assert.Equal("synthetic=cookie", actual.Cookie);
        Assert.Equal("", actual.Scenario);
        Assert.Equal("/permitted/resource?business=keep", actual.Target);

        var count = upstream.Requests.Count;
        using var redirect = await client.GetAsync("/mock-proxy/a/redirect", ct);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal(count + 1, upstream.Requests.Count);
    }

    private static HttpRequestMessage CreateRequest(string method, string url, string? body, string? scenario = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (scenario is not null)
            request.Headers.TryAddWithoutValidation("X-Test-Scenario", scenario);
        return request;
    }

    private static async Task<WebApplication> StartGatewayAsync(JsonNode json, Upstream upstream)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(options =>
        {
            options.ProgrammaticRules = () => System.Text.Json.JsonSerializer.SerializeToElement(json);
            options.ReloadOnChange = false;
            options.AllowLoopbackForTests = true;
            options.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority];
        });
        var app = builder.Build();
        try
        {
            app.UseMockProxyGateway();
            app.MapGet("/api/health", () => "normal");
            await app.StartAsync(TestContext.Current.CancellationToken);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private sealed record Captured(string Method, string Target, string Body, string Authorization, string Cookie, string Scenario);

    private sealed class Upstream(WebApplication app, ConcurrentQueue<Captured> requests) : IAsyncDisposable
    {
        internal string BaseUrl => app.Urls.Single().TrimEnd('/') + "/";
        internal ConcurrentQueue<Captured> Requests => requests;

        internal static async Task<Upstream> StartAsync()
        {
            var requests = new ConcurrentQueue<Captured>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Run(async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                requests.Enqueue(new Captured(context.Request.Method, context.Features.Get<IHttpRequestFeature>()!.RawTarget,
                    body, context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString(),
                    context.Request.Headers["X-Test-Scenario"].ToString()));
                if (context.Request.Path.Value!.EndsWith("/redirect", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers.Location = "/must-not-be-followed";
                    return;
                }
                context.Response.StatusCode = 201;
                context.Response.Headers["X-Upstream"] = "from-upstream";
                await context.Response.WriteAsync("upstream", context.RequestAborted);
            });
            try
            {
                await app.StartAsync(TestContext.Current.CancellationToken);
                return new Upstream(app, requests);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
