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
