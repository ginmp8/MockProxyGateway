using System.Collections.Concurrent;
using System.Diagnostics;
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
    [InlineData("default", "/primary/resource?simulate=remove&business=keep", false)]
    [InlineData("alternate", "/alternate/resource?business=keep", false)]
    [InlineData("default", "/primary/resource?simulate=remove&business=keep", true)]
    [InlineData("alternate", "/alternate/resource?business=keep", true)]
    public async Task AllEndToEndHeadersReachSelectedUpstreamWithoutConfiguration(string scenario, string expectedTarget, bool inspect)
    {
        await using var upstream = await Upstream.StartAsync();
        await using var alternate = await Upstream.StartAsync();
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"primary":{"baseUrl":"http://127.0.0.1/"},
              "alternate":{"baseUrl":"http://127.0.0.1/","removeQuery":["simulate"]}},
              "services":{"a":{"default":{"mode":"proxy","destination":"primary"},"rules":[
                {"id":"inspect","priority":2,"conditions":{"all":[
                  {"source":"header","key":"X-Inspect","operator":"equals","value":"true"},
                  {"source":"body","key":"$.forceMock","operator":"equals","value":true}]},
                 "action":{"mode":"mock","response":{"statusCode":202}}},
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
        const string payload = "{\r\n \"forceMock\" : false, \"name\" : \"café\", \"escaped\" : \"a\\/b\" }\n";
        using var request = CreateRequest(inspect ? "POST" : "GET", "/mock-proxy/a/resource?simulate=remove&business=keep",
            inspect ? payload : null, scenario);
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
        if (inspect)
            expected.Add("X-Inspect", "true");
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
        Assert.Equal(inspect ? "POST" : "GET", actual.Method);
        Assert.Equal(inspect ? payload : "", actual.Body);
        Assert.Equal(Encoding.UTF8.GetBytes(inspect ? payload : ""), actual.BodyBytes);
        Assert.Equal("from-upstream", response.Headers.GetValues("X-Upstream").Single());
        Assert.Equal("upstream", await response.Content.ReadAsStringAsync(ct));
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

        if (inspect)
        {
            using var bodyMock = CreateRequest("POST", "/mock-proxy/a/resource", """{"forceMock":true}""", scenario);
            bodyMock.Headers.Add("X-Inspect", "true");
            bodyMock.Headers.Add("Authorization", "Bearer synthetic-token");
            using var bodyMockResponse = await client.SendAsync(bodyMock, ct);
            Assert.Equal(HttpStatusCode.Accepted, bodyMockResponse.StatusCode);
            using var malformed = CreateRequest("POST", "/mock-proxy/a/resource", "{", scenario);
            malformed.Headers.Add("X-Inspect", "true");
            malformed.Headers.Add("Authorization", "Bearer synthetic-token");
            using var malformedResponse = await client.SendAsync(malformed, ct);
            Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
            Assert.Single(selected.Requests);
            Assert.Empty(unselected.Requests);
        }

        var primaryCount = upstream.Requests.Count;
        var alternateCount = alternate.Requests.Count;
        using var redirect = CreateRequest("GET", "/mock-proxy/a/redirect", null, scenario);
        using var redirectResponse = await client.SendAsync(redirect, ct);
        Assert.Equal(HttpStatusCode.Redirect, redirectResponse.StatusCode);
        Assert.Equal("/must-not-be-followed", redirectResponse.Headers.Location?.OriginalString);
        Assert.Equal(primaryCount + (scenario == "default" ? 1 : 0), upstream.Requests.Count);
        Assert.Equal(alternateCount + (scenario == "alternate" ? 1 : 0), alternate.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbientHostActivityDoesNotSynthesizeUpstreamTraceHeaders(bool includeIncomingHeaders)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(options =>
        {
            options.ProgrammaticRules = () => System.Text.Json.JsonSerializer.SerializeToElement(rules);
            options.ReloadOnChange = false;
            options.AllowLoopbackForTests = true;
            options.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority];
        });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            using var activity = new Activity("synthetic-host-operation").SetIdFormat(ActivityIdFormat.W3C).Start();
            activity.TraceStateString = "host=synthetic";
            activity.AddBaggage("private-host-context", "synthetic-only");
            await next(context);
        });
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/a/resource");
        var traceHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["traceparent"] = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
            ["tracestate"] = "client=synthetic",
            ["baggage"] = "client-context=synthetic",
            ["Request-Id"] = "|synthetic.client.",
            ["Correlation-Context"] = "client-context=synthetic"
        };
        if (includeIncomingHeaders)
        {
            foreach (var header in traceHeaders)
                Assert.True(request.Headers.TryAddWithoutValidation(header.Key, header.Value));
        }

        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var actual = Assert.Single(upstream.Requests);
        foreach (var header in traceHeaders)
        {
            if (includeIncomingHeaders)
                Assert.Equal(header.Value, actual.Headers[header.Key]);
            else
                Assert.False(actual.Headers.ContainsKey(header.Key));
        }
        Assert.Equal("/resource", actual.Target);
    }

    [Theory]
    [InlineData("/mock-proxy/a")]
    [InlineData("/%6dock-proxy/a")]
    [InlineData("/mock-proxy/%61")]
    [InlineData("/%6dock-proxy/%61")]
    public async Task RawPathAndRemainingQueryEncodingReachRealUpstreamUnchanged(string rawPrefix)
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
            rawPrefix + "/%7e/%41%20b?x=a%2fb&x=%41+%20&simulate=ignore&y=1",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var response = await client.GetAsync(uri, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/real/%7e/%41%20b?x=a%2fb&x=%41+%20&y=1", upstream.Requests.Last().Target);

        var rootUri = new Uri(gateway.Urls.Single().TrimEnd('/') + rawPrefix,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var rootResponse = await client.GetAsync(rootUri, ct);
        Assert.Equal(HttpStatusCode.Created, rootResponse.StatusCode);
        Assert.Equal("/real/", upstream.Requests.Last().Target);
        Assert.Equal(2, upstream.Requests.Count);

        var count = upstream.Requests.Count;
        var unsafeUri = new Uri(gateway.Urls.Single().TrimEnd('/') + rawPrefix + "/%2e%2e/escape",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var blocked = await client.GetAsync(unsafeUri, ct);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Equal(count, upstream.Requests.Count);
    }

    [Fact]
    public async Task QueryRemovalUsesDecodedNamesAndPreservesEveryRetainedWireField()
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        // Include a large catalog, mixed-case duplicates and decoded names.
        var removals = new JsonArray();
        for (var i = 0; i < 8192; i++)
            removals.Add("unused-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        removals.Add("simulate");
        removals.Add("SIMULATE");
        removals.Add("test flag");
        rules["destinations"]!["real"]!["removeQuery"] = removals;
        await using var gateway = await StartGatewayAsync(rules, upstream, realServer: true);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        var uri = new Uri(gateway.Urls.Single().TrimEnd('/') +
            "/mock-proxy/a/resource?%73IMULATE=one&keep=a%2fb&test+flag=yes&simulate=two&keep=%41+%20&SIMULATE&test%20flag=no&simulator=keep&other",
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-query-removal-test");
        request.Headers.TryAddWithoutValidation("X-Test-Scenario", "pass-through");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var actual = Assert.Single(upstream.Requests);
        Assert.Equal("/resource?keep=a%2fb&keep=%41+%20&simulator=keep&other", actual.Target);
        Assert.Equal("Bearer synthetic-query-removal-test", actual.Authorization);
        Assert.Equal("pass-through", actual.Scenario);
    }

    [Theory]
    [InlineData("?simulate=one", "")]
    [InlineData("?simulate=one&", "?")]
    [InlineData("?&simulate=one", "?")]
    [InlineData("?simulate=one&&", "?&")]
    [InlineData("?keep=a%2fb&&simulate=one", "?keep=a%2fb&")]
    [InlineData("?&keep=a%2fb&simulate=one", "?&keep=a%2fb")]
    [InlineData("?&", "?&")]
    public async Task QueryRemovalPreservesEmptyRetainedWireFields(string query, string expectedQuery)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/","removeQuery":["simulate"]}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream, realServer: true);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        var uri = new Uri(gateway.Urls.Single().TrimEnd('/') + "/mock-proxy/a/resource" + query,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-empty-query-test");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var actual = Assert.Single(upstream.Requests);
        Assert.Equal("/resource" + expectedQuery, actual.Target);
        Assert.Equal("Bearer synthetic-empty-query-test", actual.Authorization);
    }

    [Theory]
    [InlineData("/mock-proxy/a/../../api/health", 400)]
    [InlineData("/%6dock-proxy/a/../../api/health", 400)]
    [InlineData("/%6dock-proxy/a/%2e%2e/%2e%2e/api/health", 400)]
    [InlineData("/mock-proxy-other/../api/health", 200)]
    [InlineData("/%6dock-proxy-other/../api/health", 200)]
    [InlineData("/api/health", 200)]
    public async Task NormalizedGatewayTraversalCannotReachUnrelatedHostRoute(string target, int expected)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(rules, upstream, realServer: true, capture: capture);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var ct = TestContext.Current.CancellationToken;
        var uri = new Uri(gateway.Urls.Single().TrimEnd('/') + target,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        using var response = await client.GetAsync(uri, ct);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        if (expected == 200)
        {
            Assert.Equal("normal", await response.Content.ReadAsStringAsync(ct));
            Assert.DoesNotContain(capture.Messages, x => x.Message.Contains("category=invalid-target", StringComparison.Ordinal));
        }
        else
        {
            await WaitForGatewayLogAsync(capture, "category=invalid-target", "status=400", ct);
            Assert.Contains(capture.Messages, x => x.Message.Contains("category=invalid-target", StringComparison.Ordinal) &&
                x.Message.Contains("configuration=1", StringComparison.Ordinal) &&
                x.Message.Contains("correlation=", StringComparison.Ordinal));
            Assert.DoesNotContain(capture.Messages.Where(x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal)),
                x => x.Message.Contains(target, StringComparison.Ordinal));
        }
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("/mock-proxy?private=synthetic-query-secret", false, 404, "missing-service")]
    [InlineData("/mock-proxy/a/resource?private=synthetic-query-secret", true, 400, "unsupported-protocol")]
    public async Task EarlyGatewayRejectionIsRedactedAndNeverForwards(string target, bool upgrade, int status, string category)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-auth-secret");
        request.Headers.TryAddWithoutValidation("Cookie", "test=synthetic-cookie-secret");
        if (upgrade)
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "category=" + category, "status=" + status.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        var logs = capture.Messages.Where(x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal)).ToArray();
        Assert.Contains(logs, x => x.Message.Contains("category=" + category, StringComparison.Ordinal) &&
            x.Message.Contains("service=unknown rule=none action=none", StringComparison.Ordinal) &&
            x.Message.Contains("configuration=1", StringComparison.Ordinal) &&
            x.Message.Contains("durationMs=", StringComparison.Ordinal) &&
            x.Message.Contains("correlation=", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, x => x.Message.Contains("synthetic-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrencyRejectionIsRedactedAndDoesNotStartAnotherUpstreamRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var upstream = await Upstream.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(context.RequestAborted);
            context.Response.StatusCode = 200;
        });
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture,
            configure: options => options.MaxConcurrentRequests = 1);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        var pending = client.GetAsync("/mock-proxy/a/hold", ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/a/rejected?secret=synthetic-query-secret");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-auth-secret");
            using var rejected = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal(1, Volatile.Read(ref calls));
            await WaitForGatewayLogAsync(capture, "category=concurrency-limit", "status=503", ct);
            Assert.Contains(capture.Messages, x => x.Message.Contains("category=concurrency-limit", StringComparison.Ordinal) &&
                x.Message.Contains("configuration=1", StringComparison.Ordinal) &&
                x.Message.Contains("correlation=", StringComparison.Ordinal));
            Assert.DoesNotContain(capture.Messages.Where(x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal)),
                x => x.Message.Contains("synthetic-", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
            using var completed = await pending;
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        }
        // The completion event is emitted after releasing the concurrency slot.
        await WaitForGatewayLogAsync(capture, "category=completed", "status=200", ct);
        using var recovered = await client.GetAsync("/mock-proxy/a/recovered", ct);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, Volatile.Read(ref calls));
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

    [Theory]
    [InlineData("2030-01-01T00:00:00Z", "\"\\ud800\"", "\"any\"", "temporary")]
    [InlineData("2030-01-01T00:00:00Z", "\"\\udc00\"", "\"any\"", "temporary")]
    [InlineData("2030-01-01T00:00:00Z", "null", "null", "temporary")]
    [InlineData("01/01/2030 00:00:00Z", "null", "\"any\"", "temporary")]
    [InlineData("2030-01-01T00:00Z", "null", "\"any\"", "temporary")]
    [InlineData("2030-01-01T00:00:00Z", "null", "\"any\"", "temporary\n")]
    public async Task InvalidTypedRuleReloadRetainsLastGoodAndAcceptsCorrectedDocument(
        string expiry, string payload, string quantifier, string ruleId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var published = rules.ToJsonString();
        await using var gateway = await StartGatewayAsync(rules, upstream,
            provider: () => System.Text.Json.JsonElement.Parse(Volatile.Read(ref published)), capture: capture);
        using var client = gateway.GetTestClient();
        const string candidate = """
            {"schemaVersion":1,"destinations":{},"services":{"a":{
              "default":{"mode":"mock","response":{"statusCode":200,"body":{"nested":[PAYLOAD,null]}}},
              "rules":[{"id":"temporary","priority":10,"expiresAt":"EXPIRY",
                "conditions":{"source":"header","key":"X-Scenario","operator":"equals","value":"synthetic","quantifier":QUANTIFIER},
                "action":{"mode":"mock","response":{"statusCode":201}}}]}}}
            """;
        Volatile.Write(ref published, candidate.Replace("EXPIRY", expiry, StringComparison.Ordinal)
            .Replace("PAYLOAD", payload, StringComparison.Ordinal)
            .Replace("QUANTIFIER", quantifier, StringComparison.Ordinal)
            .Replace("\"temporary\"", System.Text.Json.JsonSerializer.Serialize(ruleId), StringComparison.Ordinal));
        await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 1.", "", ct);
        using (var retained = await client.GetAsync("/mock-proxy/a/check", ct))
        {
            Assert.Equal(HttpStatusCode.Created, retained.StatusCode);
            Assert.Equal("upstream", await retained.Content.ReadAsStringAsync(ct));
        }
        Assert.Single(upstream.Requests);

        Volatile.Write(ref published, candidate.Replace("EXPIRY", "2030-01-01T00:00:00+00:00", StringComparison.Ordinal)
            .Replace("PAYLOAD", "\"\\ud83d\\ude00\"", StringComparison.Ordinal)
            .Replace("QUANTIFIER", "\"any\"", StringComparison.Ordinal));
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
        using var corrected = await client.GetAsync("/mock-proxy/a/check", ct);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(await corrected.Content.ReadAsStringAsync(ct));
        Assert.Equal("\U0001F600", body.RootElement.GetProperty("nested")[0].GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.RootElement.GetProperty("nested")[1].ValueKind);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("header", "X-Scenario\n", "X-Scenario")]
    [InlineData("cookie", "test=session", "test-session")]
    public async Task InvalidSelectorReloadCannotReplaceActiveMockWithProxy(string source, string invalidKey, string validKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},
              "rules":[{"id":"selected","priority":10,"conditions":{"source":"header","key":"X-Scenario","operator":"equals","value":"synthetic"},
                "action":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var condition = rules["services"]!["a"]!["rules"]![0]!["conditions"]!;
        condition["source"] = source;
        condition["key"] = validKey;
        var published = rules.ToJsonString();
        await using var gateway = await StartGatewayAsync(rules, upstream,
            provider: () => System.Text.Json.JsonElement.Parse(Volatile.Read(ref published)), capture: capture);
        using var client = gateway.GetTestClient();
        await CheckMockAsync();

        condition["key"] = invalidKey;
        Volatile.Write(ref published, rules.ToJsonString());
        await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 1.", "", ct);
        await CheckMockAsync();

        condition["key"] = validKey;
        rules["services"]!["a"]!["rules"]![0]!["enabled"] = false;
        Volatile.Write(ref published, rules.ToJsonString());
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
        using var proxy = await client.GetAsync("/mock-proxy/a/check", ct);
        Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal("upstream", await proxy.Content.ReadAsStringAsync(ct));
        Assert.Single(upstream.Requests);

        async Task CheckMockAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/a/check");
            request.Headers.TryAddWithoutValidation("X-Scenario", "synthetic");
            request.Headers.TryAddWithoutValidation("Cookie", "test-session=synthetic");
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("selected", await response.Content.ReadAsStringAsync(ct));
            Assert.Empty(upstream.Requests);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvalidSerializedRuleTextCannotReplaceActiveMockWithProxy(bool external, bool oversized)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}},
             "unused":{"default":{"mode":"mock","response":{"statusCode":200,"text":"VALUE"}}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(capture);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        var key = external ? "RulesJson" : "MockProxyGateway:RulesJson";
        IConfiguration source = external
            ? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = rules.ToJsonString() }).Build()
            : builder.Configuration;
        source[key] = rules.ToJsonString();
        void Configure(GatewayOptions options)
        {
            options.AllowLoopbackForTests = true;
            options.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority];
        }
        if (external)
            builder.Services.AddMockProxyGateway(source, Configure);
        else
        {
            builder.Configuration["MockProxyGateway:RuleSource"] = "Configuration";
            builder.Services.AddMockProxyGateway(Configure);
        }
        await using var gateway = builder.Build();
        gateway.UseMockProxyGateway();
        await gateway.StartAsync(ct);
        using var client = gateway.GetTestClient();
        Assert.Equal("selected", await client.GetStringAsync("/mock-proxy/a/check", ct));
        Assert.Empty(upstream.Requests);

        rules["services"]!["a"]!["default"] = JsonNode.Parse("""
            {"mode":"proxy","destination":"real"}
            """);
        var candidate = rules.ToJsonString();
        source[key] = oversized
            ? new string(' ', 4_194_304) + candidate
            : candidate.Replace("VALUE", new string('\uD800', 1), StringComparison.Ordinal);
        await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 1.", "", ct);
        Assert.Equal("selected", await client.GetStringAsync("/mock-proxy/a/check", ct));
        Assert.Empty(upstream.Requests);

        // A genuine replacement character is valid Unicode and must not be
        // confused with an invalid surrogate that was silently repaired.
        source[key] = candidate.Replace("VALUE", "\uFFFD", StringComparison.Ordinal);
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
        using var proxy = await client.GetAsync("/mock-proxy/a/check", ct);
        Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal("upstream", await proxy.Content.ReadAsStringAsync(ct));
        Assert.Equal("/check", Assert.Single(upstream.Requests).Target);
        Assert.Equal("\uFFFD", await client.GetStringAsync("/mock-proxy/unused/check", ct));
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousSerializedRuleReloadCannotReplaceActiveMockWithProxy(bool nullInlineSource)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(capture);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MockProxyGateway:RuleSource"] = "Configuration",
            ["MockProxyGateway:RulesJson"] = rules.ToJsonString()
        });
        builder.Services.AddMockProxyGateway(options =>
        {
            options.AllowLoopbackForTests = true;
            options.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority];
        });
        await using var gateway = builder.Build();
        gateway.UseMockProxyGateway();
        await gateway.StartAsync(ct);
        using var client = gateway.GetTestClient();
        Assert.Equal("selected", await client.GetStringAsync("/mock-proxy/a/check", ct));
        Assert.Empty(upstream.Requests);

        rules["services"]!["a"]!["default"] = JsonNode.Parse("""
            {"mode":"proxy","destination":"real"}
            """);
        var candidate = rules.ToJsonString();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MockProxyGateway:RulesJson"] = candidate,
            [nullInlineSource ? "MockProxyGateway:Rules" : "MockProxyGateway:RulesJson:unexpected"] = nullInlineSource ? null : "synthetic"
        });
        var conflictingSource = builder.Configuration.Sources[^1];
        await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 1.", "", ct);
        Assert.Equal("selected", await client.GetStringAsync("/mock-proxy/a/check", ct));
        Assert.Empty(upstream.Requests);

        Assert.True(builder.Configuration.Sources.Remove(conflictingSource));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MockProxyGateway:RulesJson"] = candidate
        });
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
        using var proxy = await client.GetAsync("/mock-proxy/a/check", ct);
        Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal("upstream", await proxy.Content.ReadAsStringAsync(ct));
        Assert.Equal("/check", Assert.Single(upstream.Requests).Target);
    }

    [Fact]
    public async Task ExplicitScalarSectionReloadRejectsNullChildBeforeForwarding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var external = new ConfigurationManager();
        external.AddInMemoryCollection(new Dictionary<string, string?> { ["GatewayRules"] = rules.ToJsonString() });
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(capture);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddMockProxyGateway(external.GetSection("GatewayRules"), options =>
        {
            options.AllowLoopbackForTests = true;
            options.AllowedDestinationAuthorities = [new Uri(upstream.BaseUrl).Authority];
        });
        await using var gateway = builder.Build();
        gateway.UseMockProxyGateway();
        await gateway.StartAsync(ct);
        using var client = gateway.GetTestClient();
        Assert.Equal("selected", await client.GetStringAsync("/mock-proxy/a/check", ct));
        Assert.Empty(upstream.Requests);

        rules["services"]!["a"]!["default"] = JsonNode.Parse("""
            {"mode":"proxy","destination":"real"}
            """);
        var candidate = rules.ToJsonString();
        external.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewayRules"] = candidate,
            ["GatewayRules:RulesJson"] = null
        });
        var conflictingSource = external.Sources[^1];
        await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 1.", "", ct);
        Assert.Equal("selected", await client.GetStringAsync("/mock-proxy/a/check", ct));
        Assert.Empty(upstream.Requests);

        Assert.True(external.Sources.Remove(conflictingSource));
        external.AddInMemoryCollection(new Dictionary<string, string?> { ["GatewayRules"] = candidate });
        await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
        using var proxy = await client.GetAsync("/mock-proxy/a/check", ct);
        Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal("upstream", await proxy.Content.ReadAsStringAsync(ct));
        Assert.Equal("/check", Assert.Single(upstream.Requests).Target);
    }

    [Fact]
    public async Task MissingFixtureReloadRecoversWithoutAnotherRuleEdit()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mock-proxy-fixture-recovery-").FullName;
        try
        {
            await using var upstream = await Upstream.StartAsync();
            var rules = JsonNode.Parse("""
                {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
                 "services":{"a":{"default":{"mode":"proxy","destination":"real"}}}}
                """)!;
            rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
            var published = rules.ToJsonString();
            using var capture = new CaptureProvider();
            await using var gateway = await StartGatewayAsync(rules, upstream,
                provider: () => System.Text.Json.JsonElement.Parse(Volatile.Read(ref published)),
                capture: capture, configure: options => options.FixtureRoot = directory);
            using var client = gateway.GetTestClient();
            rules["services"]!["a"]!["default"] = JsonNode.Parse("""
                {"mode":"mock","response":{"statusCode":200,"bodyFile":"late.txt","headers":{"Content-Type":"text/plain"}}}
                """);
            Volatile.Write(ref published, rules.ToJsonString());
            await WaitForGatewayLogAsync(capture, "Gateway configuration reload rejected; retaining version 1.", "", ct);
            using (var old = await client.GetAsync("/mock-proxy/a/check", ct))
            {
                Assert.Equal(HttpStatusCode.Created, old.StatusCode);
                Assert.Equal("upstream", await old.Content.ReadAsStringAsync(ct));
            }
            Assert.Single(upstream.Requests);

            // Only the temporarily missing operator-owned fixture becomes available.
            await File.WriteAllTextAsync(Path.Combine(directory, "late.txt"), "fixture-ready", ct);
            await WaitForGatewayLogAsync(capture, "Gateway configuration version 2 activated.", "", ct);
            using var recovered = await client.GetAsync("/mock-proxy/a/check", ct);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal("fixture-ready", await recovered.Content.ReadAsStringAsync(ct));
            Assert.Equal("text/plain", recovered.Content.Headers.ContentType?.MediaType);
            Assert.Single(upstream.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
    [InlineData("abort", 502, "proxy-transport")]
    [InlineData("timeout", 504, "proxy-timeout")]
    [InlineData("upstream-502", 502, "completed")]
    [InlineData("upstream-504", 504, "completed")]
    public async Task FailedMutationIsNotRetriedOrReplacedWithMock(string failure, int status, string category)
    {
        var calls = 0;
        await using var upstream = await Upstream.StartAsync(async context =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            Interlocked.Increment(ref calls);
            if (failure == "abort")
                context.Abort();
            else if (failure == "timeout")
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            else
                context.Response.StatusCode = status;
        });
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"mock","response":{"statusCode":200}},"endpoints":[
              {"method":"POST","path":"/mutate","default":{"mode":"proxy","destination":"real"}}
             ]}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(json, upstream, capture: capture, configure: o => o.ProxyTimeoutSeconds = 1);
        using var client = gateway.GetTestClient();
        using var request = CreateRequest("POST", "/mock-proxy/a/mutate?secret=synthetic-failure-query", Body);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-failure-token");
        var ct = TestContext.Current.CancellationToken;
        using var response = await client.SendAsync(request, ct);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await WaitForGatewayLogAsync(capture, "action=proxy status=" + status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "category=" + category, ct);
        Assert.DoesNotContain(capture.Messages, x => x.Category.StartsWith("MockProxyGateway.", StringComparison.Ordinal) &&
            x.Message.Contains("synthetic-failure-", StringComparison.Ordinal));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task CancelledProxyIsDiagnosedWithoutRetryOrFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var upstream = await Upstream.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(20), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                aborted.TrySetResult();
            }
        });
        var json = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"mock","response":{"statusCode":200}},"endpoints":[
              {"method":"GET","path":"/wait","default":{"mode":"proxy","destination":"real"}}
             ]}}}
            """)!;
        json["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(json, upstream, realServer: true, capture: capture);
        using var client = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = client.GetAsync("/mock-proxy/a/wait", cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            await cancel.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitForGatewayLogAsync(capture, "action=proxy", "category=cancelled", ct);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiteralAsteriskMethodUsesWildcardPathSpecificityRegardlessOfOrder(bool reverseOrder)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"endpoints":[
              {"method":"*","path":"/items/fixed","default":{"mode":"mock","response":{"statusCode":200,"text":"literal"}}},
              {"method":"*","path":"/items/{id}","default":{"mode":"mock","response":{"statusCode":202,"text":"template"}}},
              {"method":"*","path":"/items/{**rest}","default":{"mode":"proxy","destination":"real"}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        if (reverseOrder)
        {
            var endpoints = rules["services"]!["a"]!["endpoints"]!.AsArray();
            var first = endpoints[0]!.DeepClone();
            endpoints.RemoveAt(0);
            endpoints.Add(first);
        }
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();

        foreach (var method in new[] { "*", "GET" })
        {
            using var literal = CreateRequest(method, "/mock-proxy/a/items/fixed", null);
            using var literalResponse = await client.SendAsync(literal, ct);
            Assert.Equal(HttpStatusCode.OK, literalResponse.StatusCode);
            Assert.Equal("literal", await literalResponse.Content.ReadAsStringAsync(ct));

            using var template = CreateRequest(method, "/mock-proxy/a/items/other", null);
            using var templateResponse = await client.SendAsync(template, ct);
            Assert.Equal(HttpStatusCode.Accepted, templateResponse.StatusCode);
            Assert.Equal("template", await templateResponse.Content.ReadAsStringAsync(ct));
            Assert.Empty(upstream.Requests);
        }

        using var real = await client.GetAsync("/mock-proxy/a/items/other/child", ct);
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
        Assert.Single(upstream.Requests);
        Assert.Equal("/items/other/child", upstream.Requests.Single().Target);
    }

    [Fact]
    public async Task EndpointCandidateWorkIsBoundedBeforeAnyOutboundCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},
              "endpoints":[{"method":"GET","path":"/control","default":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var endpoints = rules["services"]!["a"]!["endpoints"]!.AsArray();
        var prefix = string.Concat(Enumerable.Repeat("/common", 96));
        for (var i = 0; i < 1023; i++)
        {
            var endpoint = JsonNode.Parse("""
                {"method":"GET","path":"/candidate","default":{"mode":"mock","response":{"statusCode":202}}}
                """)!;
            endpoint["path"] = prefix + "/candidate-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            endpoints.Add(endpoint);
        }

        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture);
        using var client = gateway.GetTestClient();
        var unmatched = prefix + "/unmatched";
        using (var rejected = await client.GetAsync("/mock-proxy/a" + unmatched, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Empty(await rejected.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "status=400", "category=evaluation-budget", ct);

        using (var mock = await client.GetAsync("/mock-proxy/a/control", ct))
        {
            Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
            Assert.Equal("selected", await mock.Content.ReadAsStringAsync(ct));
        }
        Assert.Empty(upstream.Requests);

        using (var proxy = await client.GetAsync("/mock-proxy/a/ordinary", ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Single(upstream.Requests);

        // Method-ineligible endpoints do not inspect the long path at all.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/a" + unmatched);
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.Equal(unmatched, upstream.Requests.Last().Target);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("cookie")]
    public async Task UnmatchedTextSelectorWorkIsBoundedBeforeAnyOutboundCall(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},
              "endpoints":[{"method":"GET","path":"/uninspected","default":{"mode":"proxy","destination":"real"}}],
              "rules":[{"id":"selected","priority":10,"conditions":{"source":"query","key":"scenario","operator":"equals","value":"selected"},
                "action":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var serviceRules = rules["services"]!["a"]!["rules"]!.AsArray();
        serviceRules[0]!["conditions"]!["source"] = source;
        for (var i = 0; i < 1024; i++)
        {
            var rule = JsonNode.Parse("""
                {"id":"missing","priority":1,"conditions":{"source":"query","key":"missing","operator":"exists"},
                 "action":{"mode":"mock","response":{"statusCode":202}}}
                """)!;
            rule["id"] = "missing-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule["conditions"]!["source"] = source;
            serviceRules.Add(rule);
        }

        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture);
        using var client = gateway.GetTestClient();
        var fields = string.Join(source == "query" ? "&" : "; ", Enumerable.Range(0, 96)
            .Select(i => "field" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=synthetic"));

        // Many unrelated values and no winning rule used to consume uncharged
        // scans, then reach the default proxy. The shared budget must stop it.
        using (var request = Request("/check", fields))
        using (var rejected = await client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Empty(await rejected.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "status=400", "category=evaluation-budget", ct);

        using (var request = Request("/check", "scenario=selected"))
        using (var mock = await client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
            Assert.Equal("selected", await mock.Content.ReadAsStringAsync(ct));
        }
        Assert.Empty(upstream.Requests);

        using (var request = Request("/check", "scenario=other"))
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Single(upstream.Requests);

        // The same valid large input is forwarded transparently when the
        // endpoint's explicit default does not require inspecting that source.
        using (var request = Request("/uninspected", fields))
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        var forwarded = upstream.Requests.Last();
        if (source == "query")
            Assert.Equal("/uninspected?" + fields, forwarded.Target);
        else
            Assert.Equal(fields, forwarded.Cookie);

        HttpRequestMessage Request(string path, string values)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                "/mock-proxy/a" + path + (source == "query" ? "?" + values : ""));
            if (source == "cookie")
                request.Headers.TryAddWithoutValidation("Cookie", values);
            return request;
        }
    }

    [Theory]
    [InlineData("equals")]
    [InlineData("in")]
    public async Task RepeatedNumericComparisonWorkIsBoundedBeforeAnyOutboundCall(string op)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},
              "endpoints":[{"method":"POST","path":"/uninspected","default":{"mode":"proxy","destination":"real"}}],
              "rules":[{"id":"selected","priority":10,"conditions":{"source":"body","key":"$.number","operator":"equals","value":7},
                "action":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var serviceRules = rules["services"]!["a"]!["rules"]!.AsArray();
        for (var i = 0; i < 1024; i++)
        {
            var rule = JsonNode.Parse("""
                {"id":"missing","priority":1,"conditions":{"source":"body","key":"$.number","operator":"equals","value":1},
                 "action":{"mode":"mock","response":{"statusCode":202}}}
                """)!;
            rule["id"] = "missing-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rule["conditions"]!["operator"] = op;
            if (op == "in")
                rule["conditions"]!["value"] = JsonNode.Parse("[1,2]");
            serviceRules.Add(rule);
        }

        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture);
        using var client = gateway.GetTestClient();
        // Valid JSON numeric tokens can be much larger than machine numbers.
        // Reusing a cached selection does not make repeated comparisons free.
        var large = "{\"number\":0." + new string('1', 6000) + "}";

        using (var request = CreateRequest("POST", "/mock-proxy/a/check", large))
        using (var rejected = await client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Empty(await rejected.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "status=400", "category=evaluation-budget", ct);

        using (var request = CreateRequest("POST", "/mock-proxy/a/check", """{"number":7}"""))
        using (var mock = await client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
            Assert.Equal("selected", await mock.Content.ReadAsStringAsync(ct));
        }
        Assert.Empty(upstream.Requests);

        using (var request = CreateRequest("POST", "/mock-proxy/a/check", """{"number":8}"""))
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Single(upstream.Requests);

        using (var request = CreateRequest("POST", "/mock-proxy/a/uninspected", large))
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.Equal(Encoding.UTF8.GetBytes(large), upstream.Requests.Last().BodyBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedMediaTypeInspectionIsBoundedBeforeAnyOutboundCall(bool manyParameters)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},
              "endpoints":[{"method":"POST","path":"/uninspected","default":{"mode":"proxy","destination":"real"}}],
              "rules":[{"id":"selected","priority":10,"conditions":{"source":"contentType","operator":"equals","value":"application/json"},
                "action":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var serviceRules = rules["services"]!["a"]!["rules"]!.AsArray();
        for (var i = 0; i < 1024; i++)
        {
            var rule = JsonNode.Parse("""
                {"id":"missing","priority":1,"conditions":{"source":"contentType","operator":"equals","value":"application/xml"},
                 "action":{"mode":"mock","response":{"statusCode":202}}}
                """)!;
            rule["id"] = "missing-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            serviceRules.Add(rule);
        }

        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture);
        using var client = gateway.GetTestClient();
        var mediaType = "text/plain" + (manyParameters
            ? string.Concat(Enumerable.Range(0, 96).Select(i =>
                "; p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "=x"))
            : "; p=" + new string('x', 6000));

        using (var request = Request("/check", mediaType))
        using (var rejected = await client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Empty(await rejected.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "status=400", "category=evaluation-budget", ct);

        using (var request = Request("/check", "application/json; charset=utf-8"))
        using (var mock = await client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
            Assert.Equal("selected", await mock.Content.ReadAsStringAsync(ct));
        }
        Assert.Empty(upstream.Requests);

        using (var request = Request("/check", "text/plain; charset=utf-8"))
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Single(upstream.Requests);

        using (var request = Request("/uninspected", mediaType))
        using (var proxy = await client.SendAsync(request, ct))
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.Equal(mediaType, upstream.Requests.Last().Headers["Content-Type"]);

        static HttpRequestMessage Request(string path, string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/a" + path);
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", value);
            return request;
        }
    }

    [Theory]
    [InlineData("cookie", "synthetic-malformed-cookie", "cookie-syntax")]
    [InlineData("cookie", "other=synthetic; malformed", "cookie-syntax")]
    [InlineData("contentType", "synthetic invalid media type", "content-type-syntax")]
    [InlineData("contentType", "*/*", "content-type-syntax")]
    [InlineData("contentType", "application/*", "content-type-syntax")]
    public async Task MalformedRequiredHeaderInspectionCannotReachDefaultProxy(
        string source, string malformed, string category)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await Upstream.StartAsync();
        using var capture = new CaptureProvider();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},
              "rules":[{"id":"selected","priority":10,"conditions":{"source":"cookie","key":"scenario","operator":"equals","value":"selected"},
                "action":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        var condition = rules["services"]!["a"]!["rules"]![0]!["conditions"]!.AsObject();
        if (source == "contentType")
        {
            condition.Remove("key");
            condition["source"] = source;
            condition["value"] = "application/json";
        }

        await using var gateway = await StartGatewayAsync(rules, upstream, capture: capture);
        using var client = gateway.GetTestClient();

        using (var invalid = Request(malformed))
        using (var response = await client.SendAsync(invalid, ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        }
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "status=400", "category=" + category, ct);
        Assert.DoesNotContain(capture.Messages, x =>
            x.Category.Contains("MockProxyGateway", StringComparison.Ordinal) &&
            x.Message.Contains(malformed, StringComparison.Ordinal));

        using (var matching = Request(source == "cookie" ? "scenario=selected; other=synthetic" : "application/json; charset=utf-8"))
        using (var mock = await client.SendAsync(matching, ct))
        {
            Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
            Assert.Equal("selected", await mock.Content.ReadAsStringAsync(ct));
        }
        Assert.Empty(upstream.Requests);

        var unmatched = source == "cookie" ? "scenario=other" : "text/plain; charset=utf-8";
        using (var valid = Request(unmatched))
        using (var proxy = await client.SendAsync(valid, ct))
        {
            Assert.Equal(HttpStatusCode.Created, proxy.StatusCode);
            Assert.Equal("upstream", await proxy.Content.ReadAsStringAsync(ct));
        }
        var forwarded = Assert.Single(upstream.Requests);
        Assert.Equal(unmatched, forwarded.Headers[source == "cookie" ? "Cookie" : "Content-Type"]);

        using var absent = Request(null);
        using var defaultProxy = await client.SendAsync(absent, ct);
        Assert.Equal(HttpStatusCode.Created, defaultProxy.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);

        HttpRequestMessage Request(string? headerValue)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/a/check");
            if (headerValue is null)
                return request;
            if (source == "cookie")
                request.Headers.TryAddWithoutValidation("Cookie", headerValue);
            else
            {
                request.Content = new ByteArrayContent([]);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", headerValue);
            }
            return request;
        }
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("not-json", "text/plain", 415)]
    [InlineData("{\"customer\":{\"riskScore\":10},\"unselected\":\"\\ud800\"}", "application/json", 400)]
    [InlineData("{\"customer\":{\"riskScore\":10},\"unselected\":\"\\udc00\"}", "application/json", 400)]
    [InlineData("{\"customer\":{\"riskScore\":10},\"\\ud800\":1}", "application/json", 400)]
    [InlineData("{\"customer\":{\"riskScore\":10},\"customer\":{\"riskScore\":90}}", "application/json", 400)]
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredSuffixJsonRequiresStartupAdmissionWithoutFilteringProxyHeaders(bool admitted)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"explicit-real","priority":10,"conditions":{"source":"header","key":"X-Test-Scenario","operator":"equals","value":"real"},
                "action":{"mode":"proxy","destination":"real"}},
               {"id":"body","priority":1,"conditions":{"source":"body","key":"$.selected","operator":"equals","value":true},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream,
            configure: o => o.AllowedJsonMediaTypes = admitted ? ["application/problem+json"] : []);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        const string mediaType = "Application/Problem+Json";
        // Mutating host options after startup cannot enable or disable inspection.
        gateway.Services.GetRequiredService<IOptions<GatewayOptions>>().Value.AllowedJsonMediaTypes =
            admitted ? [] : ["application/problem+json"];
        using var matchingBody = new StringContent("{\"selected\":true}", Encoding.UTF8, mediaType);
        using var matching = await client.PostAsync("/mock-proxy/a/check", matchingBody, ct);
        Assert.Equal(admitted ? HttpStatusCode.Accepted : HttpStatusCode.UnsupportedMediaType, matching.StatusCode);
        Assert.Empty(upstream.Requests);
        using var unconfiguredBody = new StringContent("{\"selected\":true}", Encoding.UTF8, "application/other+json");
        using var unconfigured = await client.PostAsync("/mock-proxy/a/check", unconfiguredBody, ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unconfigured.StatusCode);
        Assert.Empty(upstream.Requests);
        using var standardBody = new StringContent("{\"selected\":true}", Encoding.UTF8, "application/json");
        using var standard = await client.PostAsync("/mock-proxy/a/check", standardBody, ct);
        Assert.Equal(HttpStatusCode.Accepted, standard.StatusCode);
        Assert.Empty(upstream.Requests);
        const string payload = "{ \"selected\": false }";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/a/check")
        {
            Content = new StringContent(payload, Encoding.UTF8, mediaType)
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic");
        if (!admitted)
            request.Headers.Add("X-Test-Scenario", "real");
        using var forwarded = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, forwarded.StatusCode);
        var actual = Assert.Single(upstream.Requests);
        Assert.Equal(payload, actual.Body);
        Assert.Equal("Bearer synthetic", actual.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostBodyLimitRejectionCannotReachDefaultProxy(bool chunked)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"body","priority":1,"conditions":{"source":"body","key":"$.selected","operator":"equals","value":true},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(rules, upstream, realServer: true,
            capture: capture, hostBodyLimit: 64);
        var ct = TestContext.Current.CancellationToken;
        var address = new Uri(gateway.Urls.Single());
        var payload = "{\"selected\":false,\"padding\":\"" + new string('x', 128) + "\"}";
        var framing = chunked
            ? "Transfer-Encoding: chunked\r\n"
            : $"Content-Length: {payload.Length}\r\n";
        var body = chunked
            ? $"{payload.Length:X}\r\n{payload}\r\n0\r\n\r\n"
            : payload;
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            await tcp.ConnectAsync(address.Host, address.Port, ct);
            using var wire = tcp.GetStream();
            var message = "POST /mock-proxy/a/check HTTP/1.1\r\nHost: localhost\r\n" +
                "Content-Type: application/json\r\nConnection: close\r\n" + framing + "\r\n" + body;
            await wire.WriteAsync(Encoding.ASCII.GetBytes(message), ct);
            using var reader = new StreamReader(wire, Encoding.ASCII);
            var status = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.StartsWith("HTTP/1.1 413 ", status, StringComparison.Ordinal);
        }
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "category=body-size", "status=413", ct);

        // A valid smaller body still follows the ordinary matching and proxy path.
        using var client = new HttpClient { BaseAddress = address };
        using var small = new StringContent("{\"selected\":false}", Encoding.UTF8, "application/json");
        using var forwarded = await client.PostAsync("/mock-proxy/a/check", small, ct);
        Assert.Equal(HttpStatusCode.Created, forwarded.StatusCode);
        Assert.Equal("{\"selected\":false}", Assert.Single(upstream.Requests).Body);
    }

    [Fact]
    public async Task InvalidUtf8InUnselectedBodyStringCannotReachDefaultProxy()
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"body","priority":1,"conditions":{"source":"body","key":"$.selected","operator":"equals","value":true},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        // Invalid UTF-8 is in a field not selected by the rule.
        byte[] payload = [.. Encoding.UTF8.GetBytes("{\"selected\":false,\"other\":\""), 0xff,
            .. Encoding.UTF8.GetBytes("\"}")];
        using var body = new ByteArrayContent(payload);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    // RFC 9535 orders strings by Unicode scalar value, not UTF-16 code units.
    // U+E000 is less than U+10000 even though its UTF-16 code unit is greater
    // than the first surrogate of U+10000.
    [Theory]
    [InlineData("$.items[?@.low < @.high]", 202, 0)]
    [InlineData("$.items[?@.high > @.low]", 202, 0)]
    [InlineData("$.items[?@.low <= @.high]", 202, 0)]
    [InlineData("$.items[?@.high >= @.low]", 202, 0)]
    [InlineData("$.items[?@.high < @.low]", 201, 1)]
    [InlineData("$.items[?@.low > @.high]", 201, 1)]
    [InlineData("$.items[?@.absent < @.missing]", 201, 1)]
    [InlineData("$.items[?@.absent > @.missing]", 201, 1)]
    [InlineData("$.items[?@.absent <= @.missing]", 202, 0)]
    [InlineData("$.items[?value(@.low) < $.high]", 202, 0)]
    [InlineData("$.items[?@.nested[?@.low < @.high]]", 202, 0)]
    public async Task OrderedJsonPathComparisonsUseScalarOrderAndNothingSemantics(string selector, int status, int calls)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"scalar-order","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        const string payload = """{"high":"\uD800\uDC00","items":[{"low":"\uE000","high":"\uD800\uDC00","nested":[{"low":"\uE000","high":"\uD800\uDC00"}]}]}""";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(calls, upstream.Requests.Count);
        if (calls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
    }

    [Theory]
    [InlineData("$.items[?@.score >= 10]")]
    [InlineData("$.items[?@.score >= $.min]")]
    [InlineData("$.items[?count(@.score) == 1 && @.score >= 10]")]
    [InlineData("$.items[?value(@.score) >= 10]")]
    [InlineData("$.items[?count($.items[0:1]) == 1 && @.score >= 10]")]
    [InlineData("$.items[?value($.items[0:1].score) >= 10]")]
    [InlineData("$.items[?@ .score >= 10]")]
    [InlineData("$.items[?@ ['score'] >= 10]")]
    [InlineData("$.items[?@.score >= $ .min]")]
    [InlineData("$.items[?count(@ .score) == 1 && @.score >= 10]")]
    [InlineData("$.items[?value(@\t.score) >= 10]")]
    [InlineData("$.items[?@.score >= $\r\n.min]")]
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
    [InlineData("$..items..score")]
    [InlineData("$.items[?@.score][?@.value]")]
    [InlineData("$..items[?@.score >= 10]..value")]
    [InlineData("$.items[?count(@..nested..score) == 2]")]
    [InlineData("$.items[?count($..nested..score) == 2]")]
    [InlineData("$.items[0,?@.score >= 10]")]
    [InlineData("$.items[?@.score >= 10,?@.value == 42]")]
    [InlineData("$.items[0,?@.nested[?@.active]]")]
    [InlineData("$.items[?@.nested[?@.active]][?@.value]")]
    public async Task RepeatedPathWorkIsRejectedBeforeAnyOutboundCall(string selector)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"repeated-path","priority":1,"conditions":{"source":"body","key":"$.items","operator":"notExists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"items\":[" + string.Join(',', Enumerable.Repeat("{\"score\":9,\"value\":42}", 3000)) + "]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("$.items[?length(@.name) == 2]", false, 202, 0)]
    [InlineData("$.items[?length(@.name) == 1]", false, 201, 1)]
    [InlineData("$.items[?length($.label) == 2]", true, 400, 0)]
    [InlineData("$.items[?length('é') == 2]", false, 202, 0)]
    [InlineData("$.items[?length('é') == 1]", false, 201, 1)]
    [InlineData("$.items[?length(\"😀\") == 1]", false, 202, 0)]
    [InlineData("$.items[?length(42) != 0]", false, 202, 0)]
    [InlineData("$.items[?length(null) == 0]", false, 201, 1)]
    [InlineData("$.items[?length(@.name) == @.missing]", false, 201, 1)]
    [InlineData("$.items[?length(@.missing) == @.other]", false, 202, 0)]
    [InlineData("$.items[?length(@.name) == $.label]", false, 201, 1)]
    [InlineData("$.items[?length(@.name) != $.label]", true, 400, 0)]
    [InlineData("$.items[?length(@.name) == length('é')]", false, 202, 0)]
    [InlineData("$.items[?length(@.name) == length('😀')]", false, 201, 1)]
    [InlineData("$.items[?length(@.missing) == length(@.other)]", false, 202, 0)]
    [InlineData("$.items[?length(@.missing) != length(@.other)]", false, 201, 1)]
    [InlineData("$.items[?length(@.name) == length($.label)]", true, 400, 0)]
    [InlineData("$.items[?length(value(@.*)) == 2]", false, 202, 0)]
    [InlineData("$.items[?length(value(@.*)) == 1]", false, 201, 1)]
    [InlineData("$.items[?length(value(@.missing[*])) == @.other]", false, 202, 0)]
    [InlineData("$.items[?length(value($.items[*].name)) == 2]", true, 400, 0)]
    [InlineData("$.items[?length(@.name) > count(@.*)]", false, 202, 0)]
    [InlineData("$.items[?count(@.*) > length(@.name)]", false, 201, 1)]
    [InlineData("$.items[?length(@.name) == count($.items[*].name)]", false, 201, 1)]
    [InlineData("$.items[?length(@.name) == count($.items[*].name)]", true, 400, 0)]
    [InlineData("$.items[?length(@.name) == value(@.*)]", false, 201, 1)]
    [InlineData("$.items[?value(@.*) != length(@.name)]", false, 202, 0)]
    [InlineData("$.items[?length(@.missing) == value(@.missing[*])]", false, 202, 0)]
    [InlineData("$.items[?value(@.missing[*]) == length(@.missing)]", false, 202, 0)]
    [InlineData("$.items[?value($.items[*].name) != length(@.name)]", true, 400, 0)]
    [InlineData("$.items[?length(count(@.*)) == @.missing]", false, 202, 0)]
    [InlineData("$.items[?length(count(@.*)) == 0]", false, 201, 1)]
    [InlineData("$.items[?length(length(@.name)) == length(@.missing)]", false, 202, 0)]
    [InlineData("$.items[?length(count($.items[*].name)) == @.missing]", true, 400, 0)]
    public async Task LengthFilterIsScalarCorrectAndBudgetedWithoutGlobalRegistration(string selector, bool large, int status, int calls)
    {
        Assert.True(Json.Path.FunctionRepository.TryGet("length", out var originalFunction));
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"length","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"label\":\"e\\u0301\",\"items\":[" +
            string.Join(',', Enumerable.Repeat("{\"name\":\"e\\u0301\"}", large ? 80 : 1)) + "]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(calls, upstream.Requests.Count);
        if (calls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
        Assert.True(Json.Path.FunctionRepository.TryGet("length", out var currentFunction));
        Assert.Same(originalFunction, currentFunction);
    }

    [Theory]
    [InlineData("$.items[?match(@.name, '..')]", false, 202, 0)]
    [InlineData("$.items[?search(@.name, 'z')]", false, 201, 1)]
    [InlineData("$.items[?match(@.name, '..')]", true, 400, 0)]
    [InlineData("$.items[?match($.label, '..')]", false, 202, 0)]
    [InlineData("$.items[?match(@.name, '[^x]{2}')]", false, 202, 0)]
    [InlineData("$.items[?match(@.name, '[^e]{2}')]", false, 201, 1)]
    [InlineData("$.items[?match(@.name, '[^x]{2}')]", true, 400, 0)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}\\\\p{M}')]", false, 202, 0)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}{2}')]", false, 201, 1)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}\\\\p{M}')]", true, 400, 0)]
    [InlineData("$.items[?match(@.name, '..') && length(@.name) == 2]", false, 202, 0)]
    [InlineData("$.items[?length(@.name) == 1 || search(@.name, 'z')]", false, 201, 1)]
    [InlineData("$.items[?!search(@.name, 'z') && match(@.name, '..')]", false, 202, 0)]
    [InlineData("$.items[?@.name && (search(@.name, 'z') || match(@.name, '..'))]", true, 400, 0)]
    [InlineData("$.items[?match('😀', '.')]", false, 202, 0)]
    [InlineData("$.items[?match('é', '.')]", false, 201, 1)]
    [InlineData("$.items[?search('abc', 'b')]", false, 202, 0)]
    [InlineData("$.items[?search('abc', 'z')]", false, 201, 1)]
    [InlineData("$.items[?match('x', 'x')]", true, 400, 0)]
    [InlineData("$.items[?match(42, '42')]", false, 201, 1)]
    [InlineData("$.items[?!search(null, 'x')]", false, 202, 0)]
    [InlineData("$.items[?match(@.name, false)]", false, 201, 1)]
    [InlineData("$.items[?!match(@.name, false)]", true, 400, 0)]
    [InlineData("$.items[?match(value(@.*), '..')]", false, 202, 0)]
    [InlineData("$.items[?search(value(@.*), 'z')]", false, 201, 1)]
    [InlineData("$.items[?match(value(@.missing[*]), '..')]", false, 201, 1)]
    [InlineData("$.items[?match(value(@.*), '..')]", true, 400, 0)]
    [InlineData("$.items[?match(count(@.*), '1')]", false, 201, 1)]
    [InlineData("$.items[?!search(length(@.name), '2')]", false, 202, 0)]
    [InlineData("$.items[?match(@.name, count(@.*))]", false, 201, 1)]
    [InlineData("$.items[?!search(@.name, length($.label))]", true, 400, 0)]
    [InlineData("$.items[?match(@.name, '\\\\\\\\paaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|..')]", false, 202, 0)]
    [InlineData("$.items[?match(@.name, '\\\\\\\\paaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|z')]", false, 201, 1)]
    public async Task RegexFilterIsScalarCorrectAndBudgetedWithoutGlobalRegistration(string selector, bool large, int status, int calls)
    {
        Assert.True(Json.Path.FunctionRepository.TryGet("match", out var originalFunction));
        Assert.True(Json.Path.FunctionRepository.TryGet("search", out var originalSearch));
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"match","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"label\":\"e\\u0301\",\"items\":[" +
            string.Join(',', Enumerable.Repeat("{\"name\":\"e\\u0301\"}", large ? 80 : 1)) + "]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(calls, upstream.Requests.Count);
        if (calls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
        Assert.True(Json.Path.FunctionRepository.TryGet("match", out var currentFunction));
        Assert.Same(originalFunction, currentFunction);
        Assert.True(Json.Path.FunctionRepository.TryGet("search", out var currentSearch));
        Assert.Same(originalSearch, currentSearch);
    }

    [Theory]
    [InlineData("$.items[?@.nested[?length(@.name) == 2]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?length(@.name) == 1]]", false, 201, 1)]
    [InlineData("$.items[?@.nested[?match(@.name, '..')]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?search(@.name, 'z')]]", false, 201, 1)]
    [InlineData("$.items[?@.nested[?match(@.name, '..')]]", true, 400, 0)]
    [InlineData("$.items[?@.nested[?@.name || match(@.name, '..')]]", true, 400, 0)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == 1]", false, 202, 0)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 2]) == count(@.nested[*])]", false, 202, 0)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == count(@.nested[*])]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[*]) > count(@.nested[?match(@.name, 'z')])]", false, 202, 0)]
    [InlineData("$.items[?count(@.nested[*]) < count(@.nested[?match(@.name, 'z')])]", false, 201, 1)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 2].name) == value(@.nested[*].name)]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[*].name) == value(@.nested[?length(@.name) == 1].name)]", false, 201, 1)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].name) == value(@.missing[*])]", false, 202, 0)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == value(@.nested[*].name)]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == count(@.nested[*])]", true, 400, 0)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) > 0]", false, 201, 1)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 2].name) == 'é']", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].name) == 'other']", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) >= 1]", true, 400, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].name) == 'é']", true, 400, 0)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == @.nested[0].name]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) != @.missing]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 2].name) == @.nested[0].name]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].name) == @.name]", false, 201, 1)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].missing) == @.missing]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].missing) == @.name]", false, 201, 1)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].name) == @.nested[0].name]", true, 400, 0)]
    [InlineData("$.items[?1 == count(@.nested[?match(@.name, '..')])]", false, 202, 0)]
    [InlineData("$.items[?2 <= count(@.nested[?match(@.name, '..')])]", false, 201, 1)]
    [InlineData("$.items[?@.nested[0].name == value(@.nested[?match(@.name, '..')].name)]", false, 202, 0)]
    [InlineData("$.items[?@.name == value(@.nested[?match(@.name, '..')].name)]", false, 201, 1)]
    [InlineData("$.items[?@.missing == value(@.nested[?match(@.name, '..')].missing)]", false, 202, 0)]
    [InlineData("$.items[?1 <= count(@.nested[?match(@.name, '..')])]", true, 400, 0)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == '1']", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) != '1']", false, 202, 0)]
    [InlineData("$.items[?null != count(@.nested[?match(@.name, '..')])]", false, 202, 0)]
    [InlineData("$.items[?true == count(@.nested[?match(@.name, '..')])]", false, 201, 1)]
    [InlineData("$.items[?@.nested[?2 == length(@.name)]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?1 == length(@.name)]]", false, 201, 1)]
    [InlineData("$.items[?@.nested[?2 == length(@.name)]]", true, 400, 0)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == length(@.nested)]", false, 202, 0)]
    [InlineData("$.items[?length(@.nested) == count(@.nested[?match(@.name, '..')])]", false, 202, 0)]
    [InlineData("$.items[?length(@.name) == count(@.nested[?match(@.name, '..')])]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == length(@.name)]", false, 201, 1)]
    [InlineData("$.items[?length(@.missing) == value(@.nested[?match(@.name, '..')].missing)]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].missing) == length(@.missing)]", false, 202, 0)]
    [InlineData("$.items[?length(@.nested) == count(@.nested[?match(@.name, '..')])]", true, 400, 0)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == length(@.nested)]", true, 400, 0)]
    [InlineData("$.items[?@.nested[?length(value(@.*)) == 2]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?length(value(@.*)) == 1]]", false, 201, 1)]
    [InlineData("$.items[?@.nested[?match(value(@.*), '..')]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?search(value(@.*), 'z')]]", false, 201, 1)]
    [InlineData("$.items[?length(value(@.nested[*].name)) == count(@.nested[?match(@.name, '..')])]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) < length(value(@.nested[*].name))]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?match(value(@.*), '..')]]", true, 400, 0)]
    [InlineData("$.items[?length(value(@.nested[*].name)) == count(@.nested[?match(@.name, '..')])]", true, 400, 0)]
    [InlineData("$.items[?@.nested[?match($.label, '..')]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?search($.label, 'z')]]", false, 201, 1)]
    [InlineData("$.items[?@.nested[?length(@.name) == length($.label)]]", false, 202, 0)]
    [InlineData("$.items[?count(@.nested[?match($.label, '..')]) == 1]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?search($.label, 'z')].name) == 'é']", false, 201, 1)]
    [InlineData("$.items[?@.nested[?match($.label, '..')]]", true, 400, 0)]
    [InlineData("$.items[?@.nested[?length(@.name) == length($.label)]]", true, 400, 0)]
    [InlineData("$.items[?@.nested[?@.name == $.label]]", false, 202, 0)]
    [InlineData("$.items[?@.nested[?@.name != $.label]]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?@.name == $.label]) == 1]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?@.name == $.label].name) == 'é']", false, 202, 0)]
    [InlineData("$.items[?@.nested[?@.name == $.label]]", true, 400, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].name) == $.label]", false, 202, 0)]
    [InlineData("$.items[?$.label != value(@.nested[?match(@.name, '..')].name)]", false, 201, 1)]
    [InlineData("$.items[?count(@.nested[?match(@.name, '..')]) == $.label]", false, 201, 1)]
    [InlineData("$.items[?$.missing == value(@.nested[?match(@.name, '..')].missing)]", false, 202, 0)]
    [InlineData("$.items[?value(@.nested[?match(@.name, '..')].name) == $.label]", true, 400, 0)]
    public async Task NestedPrivateFilterUsesCandidateScopeAndFailsClosed(string selector, bool large, int status, int calls)
    {
        Assert.True(Json.Path.FunctionRepository.TryGet("length", out var length));
        Assert.True(Json.Path.FunctionRepository.TryGet("match", out var match));
        Assert.True(Json.Path.FunctionRepository.TryGet("search", out var search));
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"nested-private","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"label\":\"e\u0301\",\"items\":[{\"name\":\"outer\",\"nested\":[" +
            string.Join(',', Enumerable.Repeat("{\"name\":\"e\\u0301\"}", large ? 80 : 1)) + "]}]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(calls, upstream.Requests.Count);
        if (calls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
        Assert.True(Json.Path.FunctionRepository.TryGet("length", out var afterLength));
        Assert.True(Json.Path.FunctionRepository.TryGet("match", out var afterMatch));
        Assert.True(Json.Path.FunctionRepository.TryGet("search", out var afterSearch));
        Assert.Same(length, afterLength);
        Assert.Same(match, afterMatch);
        Assert.Same(search, afterSearch);
    }

    [Theory]
    [InlineData("$.items[?$.nested[?@ == true]]", "{\"items\":[0],\"nested\":[true]}", 202, 0, false)]
    [InlineData("$.items[?$.nested[?@ == true]]", "{\"items\":[{\"nested\":[true]}],\"nested\":[false]}", 201, 1, false)]
    [InlineData("$.items[?count($.nested[?$.value]) == 1]", "{\"items\":[0],\"nested\":[0],\"value\":null}", 202, 0, false)]
    [InlineData("$.items[?count($.nested[?$.value]) == 1]", "{\"items\":[0],\"nested\":[0]}", 201, 1, false)]
    [InlineData("$.items[?count($.nested[?match(@, 'x')]) == 1]", "{\"items\":[0],\"nested\":[\"x\"]}", 202, 0, false)]
    [InlineData("$.items[?count($.nested[?match(@, 'x')]) == 1]", "{\"items\":[0],\"nested\":[\"z\"]}", 201, 1, false)]
    [InlineData("$.items[?$.nested[?@ == true]]", "{}", 400, 0, true)]
    [InlineData("$.items[?count($.nested[?match(@, 'x')]) == 1]", "{}", 400, 0, true)]
    [InlineData("$.items[?count($.nested[?@ == true]) == length($.nested)]", "{\"items\":[{\"nested\":[]}],\"nested\":[true]}", 202, 0, false)]
    [InlineData("$.items[?count($.nested[?@ == true]) == length($.nested)]", "{\"items\":[{\"nested\":[true]}],\"nested\":[true,false]}", 201, 1, false)]
    [InlineData("$.items[?length(@.name) == count($.nested[?match(@, 'x')])]", "{\"items\":[{\"name\":\"😀\"}],\"nested\":[\"x\"]}", 202, 0, false)]
    [InlineData("$.items[?length(value($.labels[*])) == count($.nested[?@ == true])]", "{\"items\":[0],\"labels\":[\"a\",\"b\"],\"nested\":[true]}", 201, 1, false)]
    [InlineData("$.items[?count($.nested[?@ == true]) == length($.nested)]", "{}", 400, 0, true)]
    [InlineData("$.items[?count(@.nested[?@ == true && $.enabled]) == count($.expected[*])]", "{\"enabled\":true,\"expected\":[0],\"items\":[{\"nested\":[true,false]}]}", 202, 0, false)]
    [InlineData("$.items[?count($.expected[*]) < count(@.nested[?@ == true && $.enabled])]", "{\"enabled\":true,\"expected\":[0],\"items\":[{\"nested\":[true,false]}]}", 201, 1, false)]
    [InlineData("$.items[?value($.expected[*]) == value(@.nested[?@.active && $.enabled].flag)]", "{\"enabled\":true,\"expected\":[null],\"items\":[{\"nested\":[{\"active\":true,\"flag\":null}]}]}", 202, 0, false)]
    [InlineData("$.items[?value(@.nested[?@.active && $.enabled].flag) == value($.expected[*])]", "{\"enabled\":true,\"expected\":[null],\"items\":[{\"nested\":[{\"active\":true}]}]}", 201, 1, false)]
    [InlineData("$.items[?count($.expected[*]) == count($.nested[?@ == true])]", "{\"items\":[0],\"nested\":[true,false],\"expected\":[0]}", 202, 0, false)]
    [InlineData("$.items[?count($.expected[*]) == count($.nested[?@ == true])]", "{\"items\":[0],\"nested\":[true,false],\"expected\":[]}", 201, 1, false)]
    [InlineData("$.items[?count($.expected[*]) == count($.nested[?@ == true])]", "{}", 400, 0, true)]
    [InlineData("$.items[?count(@.nested[?match(@, 'x')]) == length($.label)]", "{\"label\":\"😀\",\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 202, 0, false)]
    [InlineData("$.items[?count(@.nested[?match(@, 'x')]) == length($.label)]", "{\"label\":\"ab\",\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 201, 1, false)]
    [InlineData("$.items[?length(value($.labels[*])) == count(@.nested[?match(@, 'x')])]", "{\"labels\":[\"😀\"],\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 202, 0, false)]
    [InlineData("$.items[?length(value($.labels[*])) == count(@.nested[?match(@, 'x')])]", "{\"labels\":[\"a\",\"b\"],\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 201, 1, false)]
    [InlineData("$.items[?value(@.nested[?@ == null && $.enabled]) != length($.missing)]", "{\"enabled\":true,\"items\":[{\"nested\":[null],\"value\":42}]}", 202, 0, false)]
    [InlineData("$.items[?value(@.nested[?@ == null && $.enabled]) == length($.missing)]", "{\"enabled\":true,\"items\":[{\"nested\":[false],\"value\":42}]}", 202, 0, false)]
    [InlineData("$.items[?count(@.nested[?match(@, 'x')]) == length($.label)]", "{}", 400, 0, true)]
    public async Task RootDrivenNestedFilterPreservesScopeAndBoundsWorkBeforeForwarding(
        string selector, string payload, int status, int calls, bool large)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"root-nested","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        if (large)
            payload = "{\"items\":[" + string.Join(',', Enumerable.Repeat("0", 80)) + "],\"nested\":[true,\"x\"]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(calls, upstream.Requests.Count);
        if (calls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
    }

    [Fact]
    public async Task NestedRelativeFilterSelectsMockOrRealWithoutExtraOutboundCalls()
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"nested","priority":1,"conditions":{"source":"body","key":"$.items[?@.nested[?@.active == true]]","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var ct = TestContext.Current.CancellationToken;
        using var matched = new StringContent("{\"items\":[{\"nested\":[{\"active\":true}]}]}", Encoding.UTF8, "application/json");
        using var mock = await client.PostAsync("/mock-proxy/a/check", matched, ct);
        Assert.Equal(HttpStatusCode.Accepted, mock.StatusCode);
        Assert.Empty(upstream.Requests);
        const string unmatched = "{ \"items\" : [{ \"nested\" : [{ \"active\" : false }] }] }";
        using var realBody = new StringContent(unmatched, Encoding.UTF8, "application/json");
        using var real = await client.PostAsync("/mock-proxy/a/check", realBody, ct);
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
        Assert.Equal(unmatched, Assert.Single(upstream.Requests).Body);
    }

    [Theory]
    [InlineData("$.items[?@.score >= 10]", 201, 1)]
    [InlineData("$.items[?@.score >= $.min]", 400, 0)]
    [InlineData("$.items[?count($.items[*]) == 1]", 400, 0)]
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

    [Theory]
    [InlineData("$.items[?count(@['score']) == 2]", 201, 1)]
    [InlineData("$.items[?count(@['score','score','score','score']) == 2]", 400, 0)]
    public async Task PredicateUnionMultiplicityIsChargedBeforeForwarding(string selector, int expectedStatus, int expectedCalls)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"predicate-union","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"items\":[" + string.Join(',', Enumerable.Repeat("{\"score\":9}", 400)) + "]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.Equal(expectedCalls, upstream.Requests.Count);
        if (expectedCalls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
    }

    [Theory]
    [InlineData("$.items[?@.score >= 10]", 201, 1)]
    [InlineData("$..items[?@.score >= 10]", 400, 0)]
    [InlineData("$.items[?@..score]", 400, 0)]
    [InlineData("$.items[?count(@..score) == 2]", 400, 0)]
    [InlineData("$.items[?@.nested[?@.score >= 10]]", 400, 0)]
    public async Task DescentFilterOverlapIsChargedBeforeForwarding(string selector, int expectedStatus, int expectedCalls)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"descent-filter","priority":1,"conditions":{"source":"body","key":"$.items","operator":"exists"},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        rules["services"]!["a"]!["rules"]![0]!["conditions"]!["key"] = selector;
        await using var gateway = await StartGatewayAsync(rules, upstream);
        using var client = gateway.GetTestClient();
        var payload = "{\"items\":[" + string.Join(',', Enumerable.Repeat("{\"score\":9}", 50)) + "]}";
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/mock-proxy/a/check", body, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.Equal(expectedCalls, upstream.Requests.Count);
        if (expectedCalls == 1)
            Assert.Equal(payload, Assert.Single(upstream.Requests).Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalNamedPolicyProtectsRealForwardingAndPreservesSeparateHostPolicy(bool mutateHostOptions)
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

        if (mutateHostOptions)
        {
            // Changing the host-owned options after activation must neither
            // remove the policy nor replace the approved destination authority.
            var mutable = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
            mutable.AuthorizationPolicy = null;
            mutable.AllowedDestinationAuthorities[0] = "unapproved.invalid";
            mutable.AllowLoopbackForTests = false;
        }

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

    // A host can buffer and inspect a prefix before the gateway. Matching must
    // inspect the same complete representation that the gateway would forward.
    [Theory]
    [InlineData("x{\"number\":41}", 400, 0)]
    [InlineData("{\"number\":42}", 202, 0)]
    [InlineData("{\"number\":41}", 201, 1)]
    public async Task BufferedHostBodyInspectionUsesTheSameBytesAsForwarding(string payload, int status, int calls)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"inspect","priority":1,"conditions":{"source":"body","key":"$.number","operator":"equals","value":42},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        await using var gateway = await StartGatewayAsync(rules, upstream, beforeGateway: app =>
        {
            app.Use(async (context, next) =>
            {
                context.Request.EnableBuffering();
                var prefix = new byte[1];
                await context.Request.Body.ReadExactlyAsync(prefix, context.RequestAborted);
                Assert.Equal(1L, context.Request.Body.Position);
                await next(context);
            });
        });
        using var client = gateway.GetTestClient();
        using var request = CreateRequest("POST", "/mock-proxy/a/check", payload);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(calls, upstream.Requests.Count);
        if (calls == 1)
            Assert.Equal(Encoding.UTF8.GetBytes(payload), Assert.Single(upstream.Requests).BodyBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConsumedUnbufferedBodyCannotSelectAnActionFromAValidSuffix(bool matches)
    {
        await using var upstream = await Upstream.StartAsync();
        var rules = JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{"real":{"baseUrl":"http://127.0.0.1/"}},
             "services":{"a":{"default":{"mode":"proxy","destination":"real"},"rules":[
               {"id":"inspect","priority":1,"conditions":{"source":"body","key":"$.number","operator":"equals","value":42},
                "action":{"mode":"mock","response":{"statusCode":202}}}
             ]}}}
            """)!;
        rules["destinations"]!["real"]!["baseUrl"] = upstream.BaseUrl;
        using var capture = new CaptureProvider();
        await using var gateway = await StartGatewayAsync(rules, upstream, realServer: true, capture: capture, beforeGateway: app =>
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Consume-Prefix"))
                {
                    Assert.False(context.Request.Body.CanSeek);
                    var prefix = new byte[1];
                    await context.Request.Body.ReadExactlyAsync(prefix, context.RequestAborted);
                }
                await next(context);
            });
        });
        using var client = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        var ct = TestContext.Current.CancellationToken;
        var valid = matches ? "{\"number\":42}" : "{\"number\":41}";
        using var truncated = CreateRequest("POST", "/mock-proxy/a/check", "x" + valid);
        truncated.Headers.Add("X-Consume-Prefix", "true");
        using var rejected = await client.SendAsync(truncated, ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Empty(upstream.Requests);
        await WaitForGatewayLogAsync(capture, "category=body-transport", "status=400", ct);

        using var ordinary = CreateRequest("POST", "/mock-proxy/a/check", valid);
        using var accepted = await client.SendAsync(ordinary, ct);
        Assert.Equal(matches ? HttpStatusCode.Accepted : HttpStatusCode.Created, accepted.StatusCode);
        if (matches)
            Assert.Empty(upstream.Requests);
        else
            Assert.Equal(Encoding.UTF8.GetBytes(valid), Assert.Single(upstream.Requests).BodyBytes);
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
        Func<System.Text.Json.JsonElement>? provider = null, ILoggerProvider? capture = null, Action<GatewayOptions>? configure = null,
        long? hostBodyLimit = null, Action<WebApplication>? beforeGateway = null)
    {
        var builder = TestHosting.CreateBuilder();
        if (capture is not null)
        {
            builder.Logging.AddProvider(capture);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
        }
        if (realServer)
        {
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            if (hostBodyLimit.HasValue)
                builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = hostBodyLimit.Value);
        }
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
            beforeGateway?.Invoke(app);
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
        string PrivateToken, string ForwardedFor, string Host, IReadOnlyDictionary<string, string> Headers, string[] MultipleValues, byte[] BodyBytes);

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
                using var bytes = new MemoryStream();
                await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
                var bodyBytes = bytes.ToArray();
                bytes.Position = 0;
                using var reader = new StreamReader(bytes, Encoding.UTF8, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                requests.Enqueue(new Captured(context.Request.Method, context.Features.Get<IHttpRequestFeature>()!.RawTarget,
                    body, context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString(),
                    context.Request.Headers["X-Test-Scenario"].ToString(),
                    context.Request.Headers["X-Private-Token"].ToString(), context.Request.Headers["X-Forwarded-For"].ToString(),
                    context.Request.Host.Value ?? "",
                    context.Request.Headers.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    context.Request.Headers.GetCommaSeparatedValues("X-Multiple"), bodyBytes));
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
