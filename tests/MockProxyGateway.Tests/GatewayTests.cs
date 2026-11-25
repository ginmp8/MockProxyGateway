using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MockProxyGateway.Tests;

public sealed class GatewayTests
{
    private const string Rules = """
        {"schemaVersion":1,"destinations":{},"services":{"compliance":{"endpoints":[
          {"method":"POST","path":"/analysis","rules":[
            {"id":"unavailable","priority":900,"conditions":{"source":"query","key":"simulate","operator":"equals","value":"unavailable"},"action":{"mode":"mock","response":{"statusCode":503,"body":{"status":"Unavailable"}}}},
            {"id":"rejected","priority":600,"conditions":{"source":"body","key":"$.customer.riskScore","operator":"greaterThanOrEqual","value":80},"action":{"mode":"mock","response":{"statusCode":422,"body":{"status":"Rejected"}}}},
            {"id":"success","priority":400,"conditions":{"source":"header","key":"X-Test-Scenario","operator":"equals","value":"success"},"action":{"mode":"mock","response":{"statusCode":200,"body":{"status":"Approved"}}}}
          ]},
          {"method":"GET","path":"/analysis/{id}","rules":[{"id":"pending","priority":100,"conditions":{"source":"route","key":"id","operator":"equals","value":"test-1001"},"action":{"mode":"mock","response":{"statusCode":202}}}]},
          {"method":"GET","path":"/health","default":{"mode":"mock","response":{"statusCode":200,"text":"healthy"}}}
        ]}}}
        """;

    [Theory]
    [InlineData(10, "", 200)]
    [InlineData(80, "", 422)]
    [InlineData(90, "?simulate=unavailable", 503)]
    public async Task HighestMatchingPriorityWins(int risk, string query, int status)
    {
        await using var fixture = await Fixture.StartAsync(Rules);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis" + query)
        {
            Content = JsonContent.Create(new { customer = new { riskScore = risk } })
        };
        request.Headers.Add("X-Test-Scenario", "success");
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
    }

    [Fact]
    public async Task CustomPrefixAndUnrelatedRoutesRemainIndependent()
    {
        await using var fixture = await Fixture.StartAsync(Rules, prefix: "/integration-gateway");
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("healthy", await fixture.Client.GetStringAsync("/integration-gateway/compliance/health", ct));
        Assert.Equal("normal", await fixture.Client.GetStringAsync("/api/health", ct));
        Assert.Equal("normal", await fixture.Client.GetStringAsync("/mock-proxy/compliance/health", ct));
        Assert.Equal("normal", await fixture.Client.GetStringAsync("/integration-gateway-other", ct));
    }

    [Fact]
    public async Task RouteParameterAndUnknownServiceWorkWithoutAuthRegistration()
    {
        await using var fixture = await Fixture.StartAsync(Rules);
        var ct = TestContext.Current.CancellationToken;
        using var pending = await fixture.Client.GetAsync("/mock-proxy/compliance/analysis/test-1001", ct);
        using var absent = await fixture.Client.GetAsync("/mock-proxy/unknown/health", ct);
        Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("hello", "text/plain", 415)]
    public async Task RequiredBodyInspectionErrorsFailClosed(string body, string mediaType, int status)
    {
        await using var fixture = await Fixture.StartAsync(Rules);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis")
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
        request.Headers.Add("X-Test-Scenario", "success");
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
    }

    [Fact]
    public async Task EqualWinningPriorityReturnsConfigurationError()
    {
        var rules = Rules.Replace("\"priority\":600", "\"priority\":400", StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis")
        {
            Content = JsonContent.Create(new { customer = new { riskScore = 90 } })
        };
        request.Headers.Add("X-Test-Scenario", "success");
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task DisabledGatewayDoesNotRequireFile()
    {
        await using var fixture = await Fixture.StartAsync(null, enabled: false);
        Assert.Equal("normal", await fixture.Client.GetStringAsync("/mock-proxy/compliance/health", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProductionActivationFailsEvenWhenAllowlisted()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var fixture = await Fixture.StartAsync(Rules, environment: "Production");
        });
    }

    [Fact]
    public async Task InvalidReloadRetainsLastGoodAndValidReloadChangesNewRequests()
    {
        await using var fixture = await Fixture.StartAsync(Rules);
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(fixture.Path, "{", ct);
        await Task.Delay(1500, ct);
        Assert.Equal("healthy", await fixture.Client.GetStringAsync("/mock-proxy/compliance/health", ct));
        await File.WriteAllTextAsync(fixture.Path, Rules.Replace("\"healthy\"", "\"updated\"", StringComparison.Ordinal), ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await fixture.Client.GetStringAsync("/mock-proxy/compliance/health", ct) == "updated")
                return;
            await Task.Delay(100, ct);
        }
        Assert.Equal("updated", await fixture.Client.GetStringAsync("/mock-proxy/compliance/health", ct));
    }

    [Theory]
    [InlineData("equals", "42", "{\"value\":42}", true)]
    [InlineData("equals", "42", "{\"value\":\"42\"}", false)]
    [InlineData("notEquals", "\"a\"", "{}", false)]
    [InlineData("contains", "\"bc\"", "{\"value\":\"abcd\"}", true)]
    [InlineData("startsWith", "\"ab\"", "{\"value\":\"abcd\"}", true)]
    [InlineData("endsWith", "\"cd\"", "{\"value\":\"abcd\"}", true)]
    [InlineData("in", "[1,2]", "{\"value\":2}", true)]
    [InlineData("notIn", "[1,2]", "{\"value\":3}", true)]
    [InlineData("regex", "\"^a[0-9]+$\"", "{\"value\":\"a42\"}", true)]
    [InlineData("greaterThan", "4", "{\"value\":5}", true)]
    [InlineData("greaterThanOrEqual", "5", "{\"value\":5}", true)]
    [InlineData("lessThan", "6", "{\"value\":5}", true)]
    [InlineData("lessThanOrEqual", "5", "{\"value\":5}", true)]
    [InlineData("between", "[4,6]", "{\"value\":5}", true)]
    [InlineData("jsonEquals", "{\"b\":2,\"a\":1}", "{\"value\":{\"a\":1,\"b\":2}}", true)]
    public async Task TypedOperatorBehavior(string operation, string operand, string body, bool matches)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"operator","priority":1,
               "conditions":{"source":"body","key":"$.value","operator":"OPERATOR","value":OPERAND},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """.Replace("OPERATOR", operation, StringComparison.Ordinal).Replace("OPERAND", operand, StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(matches ? HttpStatusCode.Created : HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("any", "a", true)]
    [InlineData("all", "a", false)]
    [InlineData("none", "z", true)]
    public async Task MultiValueQueryQuantifiers(string quantifier, string value, bool matches)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"multi","priority":1,
               "conditions":{"source":"query","key":"v","operator":"equals","quantifier":"QUANTIFIER","value":"VALUE"},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """.Replace("QUANTIFIER", quantifier, StringComparison.Ordinal).Replace("VALUE", value, StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var response = await fixture.Client.GetAsync("/mock-proxy/test/check?v=a&v=b", TestContext.Current.CancellationToken);
        Assert.Equal(matches ? HttpStatusCode.Created : HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OversizedInspectedBodyIsRejectedBeforeAnyAction()
    {
        await using var fixture = await Fixture.StartAsync(Rules);
        using var body = new StringContent(new string('x', 1_048_577), Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/compliance/analysis", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"source\":\"body\",\"key\":\"$.value\",\"operator\":\"between\",\"value\":[1]}")]
    [InlineData("{\"source\":\"header\",\"key\":\"x\",\"operator\":\"greaterThan\",\"value\":2}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.value\",\"operator\":\"in\",\"value\":[]}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$..value\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"header\",\"key\":\"x\",\"operator\":\"regex\",\"value\":\"(\"}")]
    [InlineData("{\"all\":[]}")]
    public async Task InvalidConditionFailsActivation(string condition)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"invalid","priority":1,"conditions":CONDITION,
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ]}}}
            """.Replace("CONDITION", condition, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var fixture = await Fixture.StartAsync(rules);
        });
    }

    [Fact]
    public async Task ExactMethodAndRouteSpecificityOverrideWildcardAndServiceDefault()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"default":{"mode":"mock","response":{"statusCode":203}},
            "endpoints":[
              {"method":"*","path":"/x/{**rest}","default":{"mode":"mock","response":{"statusCode":202}}},
              {"method":"GET","path":"/x/{id}","default":{"mode":"mock","response":{"statusCode":201}}},
              {"method":"GET","path":"/x/exact","default":{"mode":"mock","response":{"statusCode":200}}}
            ]}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        var ct = TestContext.Current.CancellationToken;
        using var exact = await fixture.Client.GetAsync("/mock-proxy/test/x/exact", ct);
        using var template = await fixture.Client.GetAsync("/mock-proxy/test/x/id", ct);
        using var wildcard = await fixture.Client.GetAsync("/mock-proxy/test/x/a/b", ct);
        using var other = await fixture.Client.GetAsync("/mock-proxy/test/unmatched", ct);
        using var post = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/test/x/exact");
        using var wildcardMethod = await fixture.Client.SendAsync(post, ct);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(HttpStatusCode.Created, template.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, wildcard.StatusCode);
        Assert.Equal(HttpStatusCode.NonAuthoritativeInformation, other.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, wildcardMethod.StatusCode);
    }

    [Theory]
    [InlineData("$['with.dot']", "{\"with.dot\":42}")]
    [InlineData("$.items[-1]", "{\"items\":[0,42]}")]
    [InlineData("$.items.*", "{\"items\":{\"one\":42}}")]
    public async Task RfcChildSelectorsSupportQuotedNamesNegativeIndexAndObjectWildcard(string selector, string payload)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"selector","priority":1,"conditions":{"source":"body","key":SELECTOR,"operator":"equals","value":42},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """.Replace("SELECTOR", System.Text.Json.JsonSerializer.Serialize(selector), StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/check", "application/problem+json", 201)]
    [InlineData("post", "/check", "application/json", 201)]
    [InlineData("GET", "/check", "application/json", 404)]
    [InlineData("POST", "/Check", "application/json", 404)]
    [InlineData("POST", "/check", "text/plain", 404)]
    public async Task MethodPathAndContentTypeConditionsRespectCaseAndShortCircuit(string method, string path, string type, int expected)
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"match","priority":1,"conditions":{"all":[
                 {"source":"method","operator":"equals","value":"POST"},
                 {"source":"path","operator":"equals","value":"/check"},
                 {"any":[{"source":"contentType","operator":"equals","value":"application/json"},
                         {"source":"contentType","operator":"equals","value":"application/problem+json"}]},
                 {"not":{"source":"header","key":"X-Disabled","operator":"exists"}}]},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/mock-proxy/test" + path)
        {
            Content = new StringContent("not inspected", Encoding.UTF8, type)
        };
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Content-Length", "5")]
    [InlineData("Connection", "close")]
    [InlineData("X-Synthetic", "value\r\ninjected: yes")]
    [InlineData("Content-Type", "invalid media type")]
    public async Task InvalidMockHeadersCannotActivate(string name, string value)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"default":{"mode":"mock",
             "response":{"statusCode":200,"headers":HEADERS}}}}}
            """.Replace("HEADERS", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { [name] = value }), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var fixture = await Fixture.StartAsync(rules);
        });
    }

    [Fact]
    public async Task HeadAndEmptyStatusesDoNotWriteResponseBodies()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"endpoints":[
              {"method":"HEAD","path":"/head","default":{"mode":"mock","response":{"statusCode":200,"text":"synthetic"}}},
              {"method":"GET","path":"/empty","default":{"mode":"mock","response":{"statusCode":204}}}
            ]}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        var ct = TestContext.Current.CancellationToken;
        using var head = new HttpRequestMessage(HttpMethod.Head, "/mock-proxy/test/head");
        using var response = await fixture.Client.SendAsync(head, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(9, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
        using var empty = await fixture.Client.GetAsync("/mock-proxy/test/empty", ct);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        Assert.Empty(await empty.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task ExcessiveBodyNodesFailClosed()
    {
        await using var fixture = await Fixture.StartAsync(Rules);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mock-proxy/compliance/analysis")
        {
            Content = new StringContent("{\"customer\":{\"riskScore\":90},\"array\":[" +
                string.Join(',', Enumerable.Repeat("0", 16384)) + "]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Test-Scenario", "success");
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegexEvaluationBudgetRejectsExcessiveFanoutWithoutDefaultAction()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"default":{"mode":"mock","response":{"statusCode":200}},
              "rules":[{"id":"regex","priority":1,"conditions":{"source":"body","key":"$.values[*]","operator":"regex","value":"^match$"},
              "action":{"mode":"mock","response":{"statusCode":201}}}]}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = JsonContent.Create(new { values = Enumerable.Repeat("miss", 65).ToArray() });
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DelayedMockReturnsConfiguredBodyAfterDelay()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"default":{"mode":"mock","response":{"statusCode":202,"delayMs":100,"text":"delayed"}}}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var response = await fixture.Client.GetAsync("/mock-proxy/test/check", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("delayed", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(75));
    }

    [Theory]
    [InlineData("header", "X-Flag", "all", "lower", 201)]
    [InlineData("header", "X-Flag", "any", "mixed", 201)]
    [InlineData("header", "X-Flag", "all", "mixed", 404)]
    [InlineData("query", "Flag", "all", "lower", 201)]
    [InlineData("cookie", "Flag", "any", "lower", 404)]
    [InlineData("cookie", "flag", "any", "lower", 201)]
    public async Task SourceNameCasingAndRepeatedValueQuantifiers(string source, string key, string quantifier, string input, int expected)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"default":{"mode":"mock","response":{"statusCode":404}},
             "rules":[{"id":"match","priority":1,"conditions":{"source":"SOURCE","key":"KEY","operator":"equals","value":"yes","quantifier":"QUANTIFIER"},
             "action":{"mode":"mock","response":{"statusCode":201}}}]}}}
            """.Replace("SOURCE", source, StringComparison.Ordinal).Replace("KEY", key, StringComparison.Ordinal)
            .Replace("QUANTIFIER", quantifier, StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/test/check?flag=yes&FLAG=yes");
        request.Headers.TryAddWithoutValidation("x-flag", input == "mixed" ? new[] { "yes", "no" } : new[] { "yes", "yes" });
        request.Headers.TryAddWithoutValidation("Cookie", "flag=yes");
        using var response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"Value\":42}", 404)]
    [InlineData("{\"value\":42}", 201)]
    public async Task JsonPropertyNamesAreCaseSensitive(string json, int expected)
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"default":{"mode":"mock","response":{"statusCode":404}},
             "rules":[{"id":"match","priority":1,"conditions":{"source":"body","key":"$.value","operator":"equals","value":42},
             "action":{"mode":"mock","response":{"statusCode":201}}}]}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
    }

    private sealed class Fixture(WebApplication app, HttpClient client, string directory) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public string Path => System.IO.Path.Combine(directory, "mock-proxy-gateway.json");

        public static async Task<Fixture> StartAsync(string? rules, string? prefix = null, bool enabled = true, string environment = "Development")
        {
            var directory = Directory.CreateTempSubdirectory("mock-proxy-tests-").FullName;
            WebApplication? app = null;
            try
            {
                var ct = TestContext.Current.CancellationToken;
                if (rules is not null)
                    await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "mock-proxy-gateway.json"), rules, ct);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = directory });
                builder.WebHost.UseTestServer();
                builder.Services.AddMockProxyGateway(options =>
                {
                    options.Enabled = enabled;
                    options.AllowedEnvironments = [environment];
                });
                app = builder.Build();
                app.UseMockProxyGateway(prefix);
                app.MapFallback(() => "normal");
                await app.StartAsync(ct);
                return new Fixture(app, app.GetTestClient(), directory);
            }
            catch
            {
                if (app is not null)
                    await app.DisposeAsync();
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}
