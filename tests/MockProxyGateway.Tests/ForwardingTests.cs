using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MockProxyGateway.Tests;

public sealed class ForwardingTests
{
    private const string Body = """{"customer":{"riskScore":10,"document":"TEST-OTHER","type":"Individual"}}""";
    private static readonly string[] MultipleHeaderValues = ["first", "second"];

    [Fact]
    public async Task PinnedExampleSelectsMockOrRealActionWithCorrectOutboundCount()
    {
        await using var upstream = await Upstream.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mock-proxy-gateway.example.json"), ct))!;
        var destinations = json["destinations"]!.AsObject();
        foreach (var item in destinations)
            item.Value!["baseUrl"] = upstream.BaseUrl + item.Key + "/";
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(json, upstream, capture: capture);
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
        await CheckAsync("POST", "/compliance/analysis?simulate=unavailable", 503, "success", expectedBody: "Service unavailable");
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
        {
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
            Assert.Equal("normal", await success.Content.ReadAsStringAsync(ct));
        }
        Assert.Equal(countBefore, upstream.Requests.Count);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(2));
        using var delayed = CreateRequest("POST", "/mock-proxy/compliance/analysis", Body, "timeout");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(delayed, cancel.Token));
        // A cancellation before middleware entry would not establish the scenario.
        await WaitForGatewayLogAsync(capture, "rule=timeout-by-header action=mock", "category=cancelled", ct);
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
            {
                Assert.Contains(expectedBody, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            }
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
                Assert.Equal("upstream", await response.Content.ReadAsStringAsync(ct));
            }
        }
    }

    [Theory]
    [InlineData("default", "/primary/resource?simulate=remove&business=keep")]
    [InlineData("alternate", "/alternate/resource?business=keep")]
    public async Task AllEndToEndHeadersReachSelectedUpstreamWithoutConfiguration(string scenario, string expectedTarget)
    {
        await using var upstream = await Upstream.StartAsync();
        await using var alternate = await Upstream.StartAsync();
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"primary":{"baseUrl":"http://127.0.0.1/"},
              "alternate":{"baseUrl":"http://127.0.0.1/","removeQuery":["simulate"]}},
              "services":{"a":{"default":{"mode":"proxy","destination":"primary"},"rules":[
                {"id":"alternate","priority":1,"conditions":{"source":"header","key":"X-Test-Scenario","operator":"equals","value":"alternate"},
                 "action":{"mode":"proxy","destination":"alternate"}},
                {"id":"mock","priority":1,"conditions":{"source":"header","key":"X-Test-Scenario","operator":"equals","value":"mock"},
                 "action":{"mode":"mock","response":{"statusCode":200}}}]}}}
            """)!;
        json["destinations"]!["primary"]!["baseUrl"] = upstream.BaseUrl + "primary/";
        json["destinations"]!["alternate"]!["baseUrl"] = alternate.BaseUrl + "alternate/";
        await using var gateway = await StartGatewayAsync(json, upstream, configure: options =>
            options.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority, new Uri(alternate.BaseUrl).Authority]);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        using var request = CreateRequest("GET", "/mock-proxy/a/resource?simulate=remove&business=keep", null, scenario);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer synthetic-token",
            ["Cookie"] = "synthetic=cookie",
            ["X-Api-Key"] = "synthetic-key",
            ["Api-Key"] = "synthetic-other-key",
            ["X-Auth-Token"] = "synthetic-auth",
            ["X-Private-Token"] = "synthetic-private",
            ["X-Business-Reference"] = "synthetic-reference",
            ["X-Forwarded-For"] = "192.0.2.1",
            ["X-Forwarded-Host"] = "client.example.test",
            ["X-Forwarded-Proto"] = "https",
            ["X-Forwarded-Prefix"] = "/original",
            ["Forwarded"] = "for=192.0.2.1;proto=https"
        };
        foreach (var header in expected)
            Assert.True(request.Headers.TryAddWithoutValidation(header.Key, header.Value));
        Assert.True(request.Headers.TryAddWithoutValidation("X-Multiple", MultipleHeaderValues));
        Assert.True(request.Headers.TryAddWithoutValidation("Connection", "keep-alive"));
        Assert.True(request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic c3ludGhldGlj"));
        request.Headers.Host = "untrusted.example.test";

        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var selected = scenario == "alternate" ? alternate : upstream;
        var unselected = scenario == "alternate" ? upstream : alternate;
        var actual = Assert.Single(selected.Requests);
        Assert.Empty(unselected.Requests);
        Assert.Equal(expectedTarget, actual.Target);
        foreach (var header in expected)
            Assert.Equal(header.Value, actual.Headers[header.Key]);
        Assert.Equal(scenario, actual.Scenario);
        Assert.Equal(MultipleHeaderValues, actual.MultipleValues);
        Assert.False(actual.Headers.ContainsKey("Proxy-Authorization"));
        Assert.False(actual.Headers.ContainsKey("Connection"));
        Assert.Equal(new Uri(selected.BaseUrl).Authority, actual.Host);

        using var mock = CreateRequest("GET", "/mock-proxy/a/resource", null, "mock");
        Assert.True(mock.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-token"));
        using var mockResponse = await client.SendAsync(mock, ct);
        Assert.Equal(HttpStatusCode.OK, mockResponse.StatusCode);
        Assert.Single(selected.Requests);
        Assert.Empty(unselected.Requests);

        var primaryCount = upstream.Requests.Count;
        var alternateCount = alternate.Requests.Count;
        using var redirect = CreateRequest("GET", "/mock-proxy/a/redirect", null, scenario);
        using var redirectResponse = await client.SendAsync(redirect, ct);
        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        Assert.Equal("/must-not-be-followed", redirectResponse.Headers.Location?.OriginalString);
        Assert.Equal(primaryCount + (scenario == "default" ? 1 : 0), upstream.Requests.Count);
        Assert.Equal(alternateCount + (scenario == "alternate" ? 1 : 0), alternate.Requests.Count);
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
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(sample, upstream,
            provider: () => System.Text.Json.JsonElement.Parse(Volatile.Read(ref published)), capture: capture);
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
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        });
        await Task.WhenAll(independent);
        Assert.Empty(upstream.Requests);

        sample["services"]!["compliance"]!["rules"]![0]!["enabled"] = true;
        Volatile.Write(ref published, sample.ToJsonString());
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
        var count = upstream.Requests.Count;
        using (var maintenance = CreateRequest("GET", "/mock-proxy/compliance/other", null, "service-unavailable"))
        using (var response = await client.SendAsync(maintenance, ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Service unavailable", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
        Assert.Equal(count, upstream.Requests.Count);

        Rule("rejected-by-body")["priority"] = 400;
        Volatile.Write(ref published, sample.ToJsonString());
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 3 activated.", "", ct);
        using (var conflict = CreateRequest("POST", "/mock-proxy/compliance/analysis",
            """{"customer":{"riskScore":90}}""", "success"))
        using (var response = await client.SendAsync(conflict, ct))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync(ct));
        }
        Assert.Equal(count, upstream.Requests.Count);
        await WaitForGatewayLogAsync(capture, "Gateway rule conflict service=compliance", "configuration=3", ct);
        Assert.Contains(capture.Messages, x => x.Message.Contains("Gateway rule conflict service=compliance", StringComparison.Ordinal) &&
            x.Message.Contains("rejected-by-body", StringComparison.Ordinal) &&
            x.Message.Contains("success-by-header", StringComparison.Ordinal) &&
            x.Message.Contains("configuration=3", StringComparison.Ordinal));

        Rule("rejected-by-body")["priority"] = 600;
        Rule("success-by-header")["enabled"] = false;
        sample["services"]!["compliance"]!["rules"]![0]!["enabled"] = false;
        sample["services"]!["no-default"] = new JsonObject();
        sample["services"]!["disabled-service"] = new JsonObject { ["enabled"] = false };
        Volatile.Write(ref published, sample.ToJsonString());
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 4 activated.", "", ct);

        // Wait for publication without sending probe requests that mutate the call count.
        // A fresh request must select the correct fallback, not merely return status 201.
        await CheckForwardedAsync("POST", "/mock-proxy/compliance/analysis", Body, "success", "/compliance-real/analysis");
        await CheckForwardedAsync("GET", "/mock-proxy/compliance/other", null, "service-unavailable", "/compliance-real/other");

        var restored = upstream.Requests.Count;
        using (var absent = await client.GetAsync("/mock-proxy/no-default/check", ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            Assert.Equal("", await absent.Content.ReadAsStringAsync(ct));
        }
        using (var disabled = await client.GetAsync("/mock-proxy/disabled-service/check", ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
            Assert.Equal("", await disabled.Content.ReadAsStringAsync(ct));
        }
        Assert.Equal(restored, upstream.Requests.Count);

        Volatile.Write(ref published, "{");
        await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 4.", "", ct);
        await CheckForwardedAsync("POST", "/mock-proxy/compliance/analysis", Body, "success", "/compliance-real/analysis");

        async Task CheckForwardedAsync(string method, string path, string? body, string scenario, string destination)
        {
            var before = upstream.Requests.Count;
            using var request = CreateRequest(method, path, body, scenario);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("upstream", await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("from-upstream", response.Headers.GetValues("X-Upstream").Single());
            Assert.Equal(before + 1, upstream.Requests.Count);
            var actual = upstream.Requests.Last();
            Assert.Equal(destination, actual.Target);
            Assert.Equal(method, actual.Method);
            Assert.Equal(body ?? "", actual.Body);
        }
    }

    private static async Task WaitForGatewayLogAsync(CaptureProvider capture, string required, string additional, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (capture.Messages.Any(x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal) &&
                x.Message.Contains(required, StringComparison.Ordinal) &&
                x.Message.Contains(additional, StringComparison.Ordinal)))
                return;
            await Task.Delay(100, ct);
        }
        Assert.Fail("Expected gateway diagnostic was not observed: " + required + " " + additional);
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
        // Exceed typical HTTP/TCP copy buffers and the configured JSON inspection limit.
        // A streaming transport may coalesce small writes; the contract does not promise per-byte flushing.
        const int initialBytes = 131_072;
        const int totalBytes = 1_179_648;
        var payload = new byte[totalBytes];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 256);
        var headers = Encoding.ASCII.GetBytes($"POST /mock-proxy/stream/upload HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/octet-stream\r\nContent-Length: {totalBytes}\r\nConnection: close\r\n\r\n");
        await wire.WriteAsync(headers, ct);
        await wire.WriteAsync(payload.AsMemory(0, initialBytes), ct);
        // An eager whole-body buffer would deadlock this handshake.
        await firstByte.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
        await wire.WriteAsync(payload.AsMemory(initialBytes), ct);
        Assert.Equal(payload, await captured.Task.WaitAsync(TimeSpan.FromSeconds(8), ct));
        using var reader = new StreamReader(wire, Encoding.ASCII);
        Assert.Contains("201", (await reader.ReadLineAsync(ct)) ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatewayDiagnosticsDoNotLogUpstreamQueryOrCredentials()
    {
        await using var upstream = await Upstream.StartAsync();
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(json, upstream, capture: capture);
        using var client = gateway.GetTestClient();
        using var request = CreateRequest("POST", "/mock-proxy/a/resource?private=synthetic-query-secret",
            """{"private":"synthetic-body-secret"}""");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-auth-secret");
        request.Headers.TryAddWithoutValidation("Cookie", "test=synthetic-cookie-secret");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var logs = capture.Messages.Where(x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal) ||
            x.Category.StartsWith("Yarp.ReverseProxy.Forwarder", StringComparison.Ordinal)).ToArray();
        Assert.Contains(logs, x => x.Message.Contains("service=a", StringComparison.Ordinal) &&
            x.Message.Contains("rule=service-default", StringComparison.Ordinal) &&
            x.Message.Contains("action=proxy", StringComparison.Ordinal) &&
            x.Message.Contains("configuration=1", StringComparison.Ordinal) &&
            x.Message.Contains("correlation=", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, x => x.Message.Contains("synthetic-", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, x => x.Category.StartsWith("Yarp.ReverseProxy.Forwarder", StringComparison.Ordinal));
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        internal ConcurrentQueue<(string Category, string Message)> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Messages);
        public void Dispose() { }

        private sealed class CaptureLogger(string category, ConcurrentQueue<(string Category, string Message)> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue((category, formatter(state, exception)));
        }
    }

    [Theory]
    [InlineData("abort", 502)]
    [InlineData("timeout", 504)]
    public async Task FailedMutationIsNotRetriedOrReplacedWithMock(string failure, int status)
    {
        var calls = 0;
        await using var upstream = await Upstream.StartAsync(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            Interlocked.Increment(ref calls);
            if (failure == "abort")
                context.Abort();
            else
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        });
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"mock","response":{"statusCode":200}},"endpoints":[
              {"method":"POST","path":"/mutate","default":{"mode":"proxy","destination":"real"}}
             ]}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(json, upstream, configure: o => o.ProxyTimeoutSeconds = 1);
        using var client = gateway.GetTestClient();
        using var request = CreateRequest("POST", "/mock-proxy/a/mutate", Body);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task HostnameResolvingToLoopbackIsDeniedBeforeConnection()
    {
        await using var upstream = await Upstream.StartAsync();
        var uri = new Uri(upstream.BaseUrl);
        var target = $"https://localhost:{uri.Port}/";
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"https://localhost/"}},
            "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = target;
        await using var gateway = await StartGatewayAsync(json, upstream,
            configure: o => o.AllowedDestinationAuthorities = [new Uri(target).Authority]);
        using var client = gateway.GetTestClient();
        using var response = await client.GetAsync("/mock-proxy/a/check", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("https://169.254.169.254/")]
    [InlineData("https://100.100.100.200/")]
    [InlineData("https://[fd00:ec2::254]/")]
    [InlineData("http://192.0.2.1/")]
    [InlineData("https://user:password@127.0.0.1/")]
    [InlineData("https://127.0.0.1/a/../b")]
    [InlineData("https://127.0.0.1/a/%2e%2e/b")]
    public async Task UnsafeDestinationRejectedEvenWithExplicitAuthority(string target)
    {
        await using var upstream = await Upstream.StartAsync();
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"https://localhost/"}},
            "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = target;
        var uri = new Uri(target);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gateway = await StartGatewayAsync(json, upstream, configure: o =>
            {
                o.AllowedDestinationAuthorities = [uri.Authority];
                o.AllowedDestinationAddresses = [uri.Host.Trim('[', ']')];
            });
        });
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("not-json", "text/plain", 415)]
    public async Task RequiredInspectionFailureCannotReachDefaultProxy(string payload, string mediaType, int expected)
    {
        await using var upstream = await Upstream.StartAsync();
        var sample = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "mock-proxy-gateway.example.json"), TestContext.Current.CancellationToken))!;
        foreach (var destination in sample["destinations"]!.AsObject())
            destination.Value!["baseUrl"] = upstream.BaseUrl + destination.Key + "/";
        await using var gateway = await StartGatewayAsync(sample, upstream);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis")
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };
        using var rejected = await client.SendAsync(request, ct);
        Assert.Equal((HttpStatusCode)expected, rejected.StatusCode);
        Assert.Empty(upstream.Requests);

        using var oversized = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis")
        {
            Content = new StringContent(new string('x', 1_048_577), Encoding.UTF8, "application/json")
        };
        using var tooLarge = await client.SendAsync(oversized, ct);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Empty(upstream.Requests);

        // A higher-priority explicit real action must not inspect a body it does not need.
        using var real = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis")
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };
        real.Headers.Add("X-Test-Scenario", "real");
        using var forwarded = await client.SendAsync(real, ct);
        Assert.Equal(HttpStatusCode.Created, forwarded.StatusCode);
        Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
    }

    [Theory]
    [InlineData("$.items[?@.score >= 10]")]
    [InlineData("$.items[?@.score >= $.min]")]
    [InlineData("$.items[?count(@.score) == 1 && @.score >= 10]")]
    [InlineData("$.items[?value(@.score) >= 10]")]
    public async Task FilterInspectionSelectsMockOrRealAndFailsClosedBeforeAnyAdditionalCall(string selector)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"high-score","priority":1,"conditions":{"source":"body","key":"$.items[?@.score >= 10]","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        using var mockBody = new StringContent("{\"min\":10,\"items\":[{\"score\":12}]}", Encoding.UTF8, "application/json");
        using var mock = await client.PostAsync("/mock-proxy/a/check", mockBody, ct);
        Assert.Equal(HttpStatusCode.Accepted, mock.StatusCode);
        Assert.Empty(upstream.Requests);

        const string unmatched = "{ \"min\" : 10, \"items\" : [{ \"score\" : 9 }] }";
        using var realBody = new StringContent(unmatched, Encoding.UTF8, "application/json");
        using var real = await client.PostAsync("/mock-proxy/a/check?keep=encoded%20value", realBody, ct);
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
        Assert.Equal(unmatched, Assert.Single(upstream.Requests).Body);

        using var unsupportedNumber = new StringContent("{\"min\":10,\"items\":[{\"score\":1e1000}]}", Encoding.UTF8, "application/json");
        using var rejected = await client.PostAsync("/mock-proxy/a/check", unsupportedNumber, ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Single(upstream.Requests);

        var oversizedWork = "{\"min\":10,\"items\":[" + string.Join(',', Enumerable.Repeat("{\"score\":12}", 4000)) + "]}";
        using var costlyBody = new StringContent(oversizedWork, Encoding.UTF8, "application/json");
        using var costly = await client.PostAsync("/mock-proxy/a/check", costlyBody, ct);
        Assert.Equal(HttpStatusCode.BadRequest, costly.StatusCode);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("$.items[?@.score >= 10]", 201, 1)]
    [InlineData("$.items[?@.score >= $.min]", 400, 0)]
    public async Task RepeatedRootTraversalIsChargedSeparatelyFromRelativeFilterWork(string selector, int expectedStatus, int expectedCalls)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"high-score","priority":1,"conditions":{"source":"body","key":"$.items[?@.score >= 10]","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"min\":10,\"items\":[" + string.Join(',', Enumerable.Repeat("{\"score\":9}", 50)) + "]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.Equal(expectedCalls, upstream.Requests.Count);
        if (expectedCalls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
    }

    [Fact]
    public async Task OptionalNamedPolicyProtectsRealForwardingAndPreservesSeparateHostPolicy()
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthenticationHandler>(
            "synthetic", _ => { });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("gateway", p => p.AddAuthenticationSchemes("synthetic").RequireClaim("scope", "gateway"));
            o.AddPolicy("host", p => p.AddAuthenticationSchemes("synthetic").RequireClaim("scope", "host"));
        });
        builder.Services.AddMockProxyGateway(o =>
        {
            o.ProgrammaticRules = () => System.Text.Json.JsonSerializer.SerializeToElement(rules);
            o.AuthorizationPolicy = "gateway";
            o.AllowLoopbackForTests = true;
            o.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority];
        });
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMockProxyGateway();
        app.MapGet("/host", () => "host-owned").RequireAuthorization("host");
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();

        using var anonymous = await client.GetAsync("/mock-proxy/a/check", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/a/check");
        wrong.Headers.Add("X-Synthetic-Scope", "host");
        using var forbidden = await client.SendAsync(wrong, ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(upstream.Requests);

        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/a/check");
        allowed.Headers.Add("X-Synthetic-Scope", "gateway");
        using var proxied = await client.SendAsync(allowed, ct);
        Assert.Equal(HttpStatusCode.Created, proxied.StatusCode);
        Assert.Single(upstream.Requests);

        using var hostOnly = new HttpRequestMessage(HttpMethod.Get, "/host");
        hostOnly.Headers.Add("X-Synthetic-Scope", "host");
        using var hostResponse = await client.SendAsync(hostOnly, ct);
        Assert.Equal("host-owned", await hostResponse.Content.ReadAsStringAsync(ct));
        using var notHost = new HttpRequestMessage(HttpMethod.Get, "/host");
        notHost.Headers.Add("X-Synthetic-Scope", "gateway");
        using var notHostResponse = await client.SendAsync(notHost, ct);
        Assert.Equal(HttpStatusCode.Forbidden, notHostResponse.StatusCode);
        Assert.Single(upstream.Requests);
    }

    private sealed class SyntheticAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var scope = Request.Headers["X-Synthetic-Scope"].ToString();
            if (scope is not ("host" or "gateway"))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim("scope", scope)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    [Fact]
    public async Task CombinedScenarioNegationAndAlternateBodyBranchDoNotLeakAcrossRequests()
    {
        await using var upstream = await Upstream.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var sample = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mock-proxy-gateway.example.json"), ct))!;
        foreach (var destination in sample["destinations"]!.AsObject())
            destination.Value!["baseUrl"] = upstream.BaseUrl + destination.Key + "/";
        await using var gateway = await StartGatewayAsync(sample, upstream);
        using var client = gateway.GetTestClient();
        using var organization = CreateRequest("POST", "/mock-proxy/compliance/analysis", """{"customer":{"type":"Organization"}}""");
        organization.Headers.Add("X-Test-Session", "session-a");
        using var matched = await client.SendAsync(organization, ct);
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        Assert.Contains("composite", await matched.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);

        using var disabled = CreateRequest("POST", "/mock-proxy/compliance/analysis?simulate=success", Body);
        disabled.Headers.Add("X-Test-Session", "session-a");
        disabled.Headers.Add("X-Disable-Scenario", "true");
        using var proxy = await client.SendAsync(disabled, ct);
        Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal("/compliance-real/analysis?simulate=success", Assert.Single(upstream.Requests).Target);
    }

    [Fact]
    public async Task DeniedDnsAddressNeverEstablishesEvenATcpConnection()
    {
        await using var upstream = await Upstream.StartAsync();
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var target = $"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"https://localhost/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = target;
        await using var gateway = await StartGatewayAsync(rules, upstream, configure: o =>
        {
            o.AllowedDestinationAuthorities = [new Uri(target).Authority];
            o.ProxyTimeoutSeconds = 2;
        });
        using var client = gateway.GetTestClient();
        using var response = await client.GetAsync("/mock-proxy/a/check", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(listener.Pending());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task StandaloneCompositionUsesCoreThroughWebApplicationFactory()
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"local":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"demo":{"default":{"mode":"proxy","destination":"local"},"endpoints":[
               {"method":"GET","path":"/mock","default":{"mode":"mock","response":{"statusCode":200,"text":"factory-mock"}}}
             ]}}}
            """)!;
        rules["destinations"]!["local"]!["baseUrl"] = upstream.BaseUrl;
        using var factory = new WebApplicationFactory<MockProxyGateway.Host.HostAssemblyMarker>()
            .WithWebHostBuilder(host =>
            {
                host.UseEnvironment("Development");
                host.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MockProxyGateway:Enabled"] = "true",
                        ["MockProxyGateway:RuleSource"] = "Configuration",
                        ["MockProxyGateway:RulesJson"] = rules.ToJsonString(),
                        ["MockProxyGateway:AllowLoopbackForTests"] = "true",
                        ["MockProxyGateway:AllowedDestinationAuthorities:0"] = new Uri(upstream.BaseUrl).Authority
                    }));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("factory-mock", await client.GetStringAsync("/mock-proxy/demo/mock", ct));
        Assert.Empty(upstream.Requests);
        using var proxy = await client.GetAsync("/mock-proxy/demo/real", ct);
        Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal("upstream", await proxy.Content.ReadAsStringAsync(ct));
        Assert.Equal("/real", Assert.Single(upstream.Requests).Target);
        using var health = await client.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task UnapprovedAuthorityAndUnknownDestinationFailBeforeAnyRequest()
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"local":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"local"}}}}
            """)!;
        rules["destinations"]!["local"]!["baseUrl"] = upstream.BaseUrl;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gateway = await StartGatewayAsync(rules, upstream,
                configure: o => o.AllowedDestinationAuthorities = []);
        });
        rules["services"]!["a"]!["default"]!["destination"] = "unknown";
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gateway = await StartGatewayAsync(rules, upstream);
        });
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("//example.test/")]
    [InlineData("/safe/%2e%2e/escape")]
    [InlineData("/safe/%252fescape")]
    public async Task InvalidRewriteCannotEscapeRegisteredDestination(string rewrite)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"local":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"local","pathRewrite":"/safe"}}}}
            """)!;
        rules["destinations"]!["local"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["default"]!["pathRewrite"] = rewrite;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var gateway = await StartGatewayAsync(rules, upstream);
        });
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RequestSuppliedUrlRemainsDataAndCannotSelectAnAuthority()
    {
        await using var upstream = await Upstream.StartAsync();
        await using var forbidden = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"local":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"local"}}}}
            """)!;
        rules["destinations"]!["local"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        var target = Uri.EscapeDataString(forbidden.BaseUrl + "private");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/a/check?target=" + target);
        request.Headers.TryAddWithoutValidation("X-Target-Url", forbidden.BaseUrl);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/check?target=" + target, Assert.Single(upstream.Requests).Target);
        Assert.Empty(forbidden.Requests);
    }

    [Fact]
    public async Task ConflictAndReloadDiagnosticsIdentifyRulesAndActiveVersionWithoutSensitiveValues()
    {
        await using var upstream = await Upstream.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"local":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"local"},"rules":[
              {"id":"first","priority":1,"conditions":{"source":"query","key":"secret","operator":"equals","value":"synthetic-secret"},
               "action":{"mode":"mock","response":{"statusCode":200}}},
              {"id":"second","priority":1,"conditions":{"source":"query","key":"secret","operator":"equals","value":"synthetic-secret"},
               "action":{"mode":"mock","response":{"statusCode":201}}}
             ]}}}
            """)!;
        rules["destinations"]!["local"]!["baseUrl"] = upstream.BaseUrl;
        var published = rules.ToJsonString();
        await using var gateway = await StartGatewayAsync(rules, upstream,
            provider: () => System.Text.Json.JsonElement.Parse(Volatile.Read(ref published)), capture: capture);
        using var client = gateway.GetTestClient();
        using var conflict = await client.GetAsync("/mock-proxy/a/check?secret=synthetic-secret", ct);
        Assert.Equal(HttpStatusCode.InternalServerError, conflict.StatusCode);
        Assert.Empty(upstream.Requests);
        Assert.Contains(capture.Messages, x => x.Message.Contains("first=first", StringComparison.Ordinal) &&
            x.Message.Contains("second=second", StringComparison.Ordinal) &&
            x.Message.Contains("configuration=1", StringComparison.Ordinal));

        Volatile.Write(ref published, "{");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline &&
            !capture.Messages.Any(x => x.Message.Contains("reload rejected; retaining version 1", StringComparison.Ordinal)))
            await Task.Delay(100, ct);
        Assert.Contains(capture.Messages, x => x.Message.Contains("reload rejected; retaining version 1", StringComparison.Ordinal));
        using var stillConflict = await client.GetAsync("/mock-proxy/a/check?secret=synthetic-secret", ct);
        Assert.Equal(HttpStatusCode.InternalServerError, stillConflict.StatusCode);
        Assert.Empty(upstream.Requests);
        Assert.DoesNotContain(capture.Messages.Where(x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal)),
            x => x.Message.Contains("synthetic-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectedJsonPreservesExactBytesAndBusinessHeadersAtRealUpstream()
    {
        var received = new TaskCompletionSource<(byte[] Body, string Method, string ContentType, string Header)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await Upstream.StartAsync(async context =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
            received.TrySetResult((bytes.ToArray(), context.Request.Method, context.Request.ContentType!,
                context.Request.Headers["X-Business-Reference"].ToString()));
            context.Response.StatusCode = 202;
            context.Response.Headers["X-Real-Response"] = "kept";
            await context.Response.WriteAsync("real-body", context.RequestAborted);
        });
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"local":{"baseUrl":"http://127.0.0.1/"}},
              "services":{"a":{"default":{"mode":"proxy","destination":"local"},"rules":[
               {"id":"inspect","priority":1,"conditions":{"source":"body","key":"$.number","operator":"equals","value":42},
                "action":{"mode":"mock","response":{"statusCode":200}}}
              ]}}}
            """)!;
        rules["destinations"]!["local"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes("{\r\n \"name\" : \"café\", \"escaped\" : \"a\\/b\", \"number\" : 41 }\n");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/mock-proxy/a/check")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
        request.Headers.Add("X-Business-Reference", "synthetic-123");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("real-body", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("kept", response.Headers.GetValues("X-Real-Response").Single());
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(bytes, actual.Body);
        Assert.Equal("PUT", actual.Method);
        Assert.Equal("application/json; charset=utf-8", actual.ContentType);
        Assert.Equal("synthetic-123", actual.Header);
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
        Func<System.Text.Json.JsonElement>? provider = null, ILoggerProvider? capture = null, Action<GatewayOptions>? configure = null)
    {
        var builder = TestHosting.CreateBuilder();
        if (capture is not null)
        {
            builder.Logging.AddProvider(capture);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }
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
            configure?.Invoke(options);
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

    private sealed record Captured(string Method, string Target, string Body, string Authorization, string Cookie, string Scenario,
        string PrivateToken, string ForwardedFor, string Host, IReadOnlyDictionary<string, string> Headers, string[] MultipleValues);

    private sealed class Upstream(WebApplication app, ConcurrentQueue<Captured> requests) : IAsyncDisposable
    {
        internal string BaseUrl => app.Urls.Single().TrimEnd('/') + "/";
        internal ConcurrentQueue<Captured> Requests => requests;

        internal static async Task<Upstream> StartAsync(Func<HttpContext, Task>? handler = null)
        {
            var requests = new ConcurrentQueue<Captured>();
            var builder = TestHosting.CreateBuilder();
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
                    context.Request.Headers["X-Test-Scenario"].ToString(),
                    context.Request.Headers["X-Private-Token"].ToString(), context.Request.Headers["X-Forwarded-For"].ToString(),
                    context.Request.Host.Value ?? "",
                    context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    context.Request.Headers.GetCommaSeparatedValues("X-Multiple")));
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
