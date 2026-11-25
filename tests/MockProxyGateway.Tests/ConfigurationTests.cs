using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MockProxyGateway.Tests;

public sealed class ConfigurationTests
{
    private const string Rules = """
        {"schemaVersion":1,"destinations":{},"services":{"demo":{"endpoints":[
          {"method":"POST","path":"/check","default":{"mode":"mock","response":{"statusCode":404}},"rules":[
            {"id":"typed","priority":100,"conditions":{"all":[
               {"source":"body","key":"$.value","operator":"equals","value":42},
               {"source":"body","key":"$.flag","operator":"equals","value":true},
               {"source":"body","key":"$.nullable","operator":"isNull"},
               {"source":"body","key":"$.missing","operator":"notExists"}]},
             "action":{"mode":"mock","response":{"statusCode":201,"body":{"number":42,"boolean":true,"null":null,"list":[1,false,null]}}}}
          ]}
        ]}}}
        """;

    [Theory]
    [InlineData("File")]
    [InlineData("Inline")]
    [InlineData("Configuration")]
    [InlineData("ExternalConfiguration")]
    [InlineData("Programmatic")]
    public async Task AllSourcesPreserveTypedValuesAndShareValidation(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mock-proxy-source-").FullName;
        WebApplication? app = null;
        try
        {
            var settings = new JsonObject { ["MockProxyGateway"] = new JsonObject { ["RuleSource"] = source } };
            if (source == "Inline")
                settings["MockProxyGateway"]!["Rules"] = JsonNode.Parse(Rules);
            if (source == "Configuration")
                settings["MockProxyGateway"]!["RulesJson"] = Rules;
            await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), settings.ToJsonString(), ct);
            if (source == "File")
                await File.WriteAllTextAsync(Path.Combine(directory, "mock-proxy-gateway.json"), Rules, ct);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory });
            builder.WebHost.UseTestServer();
            if (source == "ExternalConfiguration")
            {
                var external = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RulesJson"] = Rules }).Build();
                builder.Services.AddMockProxyGateway(external);
            }
            else if (source == "Programmatic")
                builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(Rules));
            else
                builder.Services.AddMockProxyGateway();
            app = builder.Build();
            app.UseMockProxyGateway();
            await app.StartAsync(ct);
            using var client = app.GetTestClient();
            using var body = new StringContent("""{"value":42,"flag":true,"nullable":null}""", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/mock-proxy/demo/check", body, ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(42, document.RootElement.GetProperty("number").GetInt32());
            Assert.True(document.RootElement.GetProperty("boolean").GetBoolean());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("null").ValueKind);
            Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("list")[1].ValueKind);
            using var wrongType = new StringContent("""{"value":"42","flag":true,"nullable":null}""", Encoding.UTF8, "application/json");
            using var unmatched = await client.PostAsync("/mock-proxy/demo/check", wrongType, ct);
            Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileFixtureIsReadOnlyFromTrustedRoot()
    {
        var directory = Directory.CreateTempSubdirectory("mock-proxy-fixture-").FullName;
        WebApplication? app = null;
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(directory, "body.txt"), "fixture-response", ct);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory });
            builder.WebHost.UseTestServer();
            builder.Services.AddMockProxyGateway(o =>
            {
                o.FixtureRoot = directory;
                o.ProgrammaticRules = () => JsonElement.Parse("""
                    {"schemaVersion":1,"destinations":{},"services":{"fixture":{"default":{"mode":"mock","response":{"statusCode":200,"bodyFile":"body.txt","headers":{"Content-Type":"text/plain"}}}}}}
                    """);
            });
            app = builder.Build();
            app.UseMockProxyGateway();
            await app.StartAsync(ct);
            using var client = app.GetTestClient();
            Assert.Equal("fixture-response", await client.GetStringAsync("/mock-proxy/fixture/", ct));
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../secret.txt")]
    public async Task FixtureTraversalIsRejectedAtActivation(string path)
    {
        var directory = Directory.CreateTempSubdirectory("mock-proxy-fixture-").FullName;
        try
        {
            var rules = JsonNode.Parse("""
                {"schemaVersion":1,"destinations":{},"services":{"fixture":{"default":{"mode":"mock","response":{"statusCode":200,"bodyFile":"placeholder"}}}}}
                """)!;
            rules["services"]!["fixture"]!["default"]!["response"]!["bodyFile"] = path;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory });
            builder.Services.AddMockProxyGateway(o =>
            {
                o.FixtureRoot = directory;
                o.ProgrammaticRules = () => JsonSerializer.SerializeToElement(rules);
            });
            await using var app = builder.Build();
            Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NamedPolicyIsOptionalAndDeniedBeforeRuleEvaluation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy("gateway-deny", policy => policy.RequireAssertion(_ => false)));
        builder.Services.AddMockProxyGateway(o =>
        {
            o.AuthorizationPolicy = "gateway-deny";
            o.ProgrammaticRules = () => JsonElement.Parse(Rules);
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        app.MapGet("/api/health", () => "normal");
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var invalidBody = new StringContent("{", Encoding.UTF8, "application/json");
        using var denied = await client.PostAsync("/mock-proxy/demo/check", invalidBody, ct);
        Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal("normal", await client.GetStringAsync("/api/health", ct));
    }
}
