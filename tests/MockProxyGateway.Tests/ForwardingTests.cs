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

    [Fact]
    public async Task RawPathAndRemainingQueryEncodingReachRealUpstreamUnchanged()
    {
        await using var upstream = await Upstream.StartAsync();
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/","removeQuery":["simulate"]}},
            "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl + "real/";
        await using var gateway = await StartGatewayAsync(json, upstream, realServer: true);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        var uri = new Uri(gateway.Urls.Single().TrimEnd('/') +
            "/mock-proxy/a/%7e/%41%20b?x=a%2fb&x=%41+%20&simulate=ignore&y=1",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var response = await client.GetAsync(uri, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/real/%7e/%41%20b?x=a%2fb&x=%41+%20&y=1", upstream.Requests.Last().Target);

        var count = upstream.Requests.Count;
        var unsafeUri = new Uri(gateway.Urls.Single().TrimEnd('/') + "/mock-proxy/a/%2e%2e/escape",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var blocked = await client.GetAsync(unsafeUri, ct);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal(count, upstream.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentRequestsAndHotRuleChangesRemainIsolated()
    {
        await using var upstream = await Upstream.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var sample = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mock-proxy-gateway.example.json"), ct))!;
        foreach (var destination in sample["destinations"]!.AsObject())
            destination.Value!["baseUrl"] = upstream.BaseUrl + destination.Key + "/";
        var published = sample.ToJsonString();
        await using var gateway = await StartGatewayAsync(sample, upstream,
            provider: () => System.Text.Json.JsonElement.Parse(Volatile.Read(ref published)));
        using var client = gateway.GetTestClient();
        var endpointRules = sample["services"]!["compliance"]!["endpoints"]![0]!["rules"]!.AsArray();
        JsonNode Rule(string id) => endpointRules.Single(x => (string?)x!["id"] == id)!;

        var independent = Enumerable.Range(0, 12).Select(async index =>
        {
            var isRejected = index % 2 != 0;
            using var request = CreateRequest("POST", "/mock-proxy/compliance/analysis",
                isRejected ? """{"customer":{"riskScore":90}}""" : Body, "success");
            request.Headers.TryAddWithoutValidation("X-Test-Session", "session-" + index);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(isRejected ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(isRejected ? "Rejected" : "Approved", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        });
        await Task.WhenAll(independent);
        Assert.Empty(upstream.Requests);

        sample["services"]!["compliance"]!["rules"]![0]!["enabled"] = true;
        Volatile.Write(ref published, sample.ToJsonString());
        await EventuallyAsync("GET", "/mock-proxy/compliance/other", "service-unavailable", 503);
        var count = upstream.Requests.Count;
        using (var maintenance = CreateRequest("GET", "/mock-proxy/compliance/other", null, "service-unavailable"))
        using (var response = await client.SendAsync(maintenance, ct))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(count, upstream.Requests.Count);

        Rule("rejected-by-body")["priority"] = 400;
        Volatile.Write(ref published, sample.ToJsonString());
        await EventuallyAsync("POST", "/mock-proxy/compliance/analysis", "success", 500, """{"customer":{"riskScore":90}}""");
        Assert.Equal(count, upstream.Requests.Count);

        Rule("rejected-by-body")["priority"] = 600;
        Rule("success-by-header")["enabled"] = false;
        sample["services"]!["compliance"]!["rules"]![0]!["enabled"] = false;
        sample["services"]!["no-default"] = new JsonObject();
        sample["services"]!["disabled-service"] = new JsonObject { ["enabled"] = false };
        Volatile.Write(ref published, sample.ToJsonString());
        await EventuallyAsync("POST", "/mock-proxy/compliance/analysis", "success", 201, Body);
        var restored = upstream.Requests.Count;
        await EventuallyAsync("GET", "/mock-proxy/compliance/other", "service-unavailable", 201);
        Assert.Equal(restored + 1, upstream.Requests.Count);
        using (var absent = await client.GetAsync("/mock-proxy/no-default/check", ct))
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using (var disabled = await client.GetAsync("/mock-proxy/disabled-service/check", ct))
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal(restored + 1, upstream.Requests.Count);

        Volatile.Write(ref published, "{");
        await Task.Delay(1300, ct);
        using var lastGood = CreateRequest("POST", "/mock-proxy/compliance/analysis", Body, "success");
        using var forwarded = await client.SendAsync(lastGood, ct);
        Assert.Equal(HttpStatusCode.Created, forwarded.StatusCode);
        Assert.Equal(restored + 2, upstream.Requests.Count);

        async Task EventuallyAsync(string method, string path, string scenario, int status, string? body = null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                using var request = CreateRequest(method, path, body, scenario);
                using var response = await client.SendAsync(request, ct);
                if ((int)response.StatusCode == status)
                    return;
                await Task.Delay(100, ct);
            }
            Assert.Fail("A validated configuration update did not become active.");
        }
    }

    [Fact]
    public async Task NonInspectedBinaryBodyStreamsBeforeClientFinishesSending()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstByte = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await Upstream.StartAsync(async context =>
        {
            var first = new byte[1];
            await context.Request.Body.ReadExactlyAsync(first, context.RequestAborted);
            firstByte.TrySetResult();
            using var bytes = new MemoryStream();
            bytes.WriteByte(first[0]);
            await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
            captured.TrySetResult(bytes.ToArray());
            context.Response.StatusCode = 201;
            await context.Response.WriteAsync("streamed", context.RequestAborted);
        });
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
            "services":{"stream":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(json, upstream, realServer: true);
        var address = new Uri(gateway.Urls.Single());
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(address.Host, address.Port, ct);
        using var wire = client.GetStream();
        var headers = Encoding.ASCII.GetBytes("POST /mock-proxy/stream/upload HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/octet-stream\r\nContent-Length: 4\r\nConnection: close\r\n\r\n");
        await wire.WriteAsync(headers, ct);
        await wire.WriteAsync(new byte[] { 0xff }, ct);
        // An eager body buffer would deadlock this handshake. Only send the rest after upstream sees byte one.
        await firstByte.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
        await wire.WriteAsync(new byte[] { 0x00, 0x01, 0xfe }, ct);
        Assert.Equal(new byte[] { 0xff, 0x00, 0x01, 0xfe }, await captured.Task.WaitAsync(TimeSpan.FromSeconds(8), ct));
        using var reader = new StreamReader(wire, Encoding.ASCII);
        Assert.Contains("201", (await reader.ReadLineAsync(ct)) ?? "", StringComparison.Ordinal);
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

    private static async Task<WebApplication> StartGatewayAsync(JsonNode json, Upstream upstream, bool realServer = false,
        Func<System.Text.Json.JsonElement>? provider = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        if (realServer)
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        else
            builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(options =>
        {
            options.ProgrammaticRules = provider ?? (() => System.Text.Json.JsonSerializer.SerializeToElement(json));
            options.ReloadOnChange = provider is not null;
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

        internal static async Task<Upstream> StartAsync(Func<HttpContext, Task>? handler = null)
        {
            var requests = new ConcurrentQueue<Captured>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Run(async context =>
            {
                if (handler is not null)
                {
                    await handler(context);
                    return;
                }
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
