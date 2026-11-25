using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public async Task RegistrationWithoutMiddlewareIsInertEvenInProduction()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => throw new InvalidOperationException("must-not-read"));
        await using var app = builder.Build();
        app.MapGet("/mock-proxy/demo/check", () => "host-owned");
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        Assert.Equal("host-owned", await client.GetStringAsync("/mock-proxy/demo/check", ct));
    }

    [Fact]
    public async Task EnvironmentAndProgrammaticOptionsAndExplicitPrefixHavePrecedence()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mock-proxy-precedence-").FullName;
        try
        {
            var file = Path.Combine(directory, "mounted.json");
            await File.WriteAllTextAsync(file, """
                {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"selected"}}}}}
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), """
                {"MockProxyGateway":{"Enabled":false,"Prefix":"/base","ConfigurationFile":"missing.json"}}
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.Staging.json"), """
                {"MockProxyGateway":{"Enabled":true,"Prefix":"/environment","AllowedEnvironments":["Staging"]}}
                """, ct);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Staging", ContentRootPath = directory });
            builder.WebHost.UseTestServer();
            builder.Services.AddMockProxyGateway(o =>
            {
                o.ConfigurationFile = file;
                o.Prefix = "/programmatic";
            });
            await using var app = builder.Build();
            app.UseMockProxyGateway("/explicit");
            app.MapFallback(() => "normal");
            await app.StartAsync(ct);
            using var client = app.GetTestClient();
            Assert.Equal("selected", await client.GetStringAsync("/explicit/demo/check", ct));
            Assert.Equal("normal", await client.GetStringAsync("/base/demo/check", ct));
            Assert.Equal("normal", await client.GetStringAsync("/environment/demo/check", ct));
            Assert.Equal("normal", await client.GetStringAsync("/programmatic/demo/check", ct));
            Assert.Equal("normal", await client.GetStringAsync("/mock-proxy/demo/check", ct));
            Assert.Equal("normal", await client.GetStringAsync("/explicit-other/demo/check", ct));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("File")]
    [InlineData("Inline")]
    [InlineData("Configuration")]
    [InlineData("ExternalConfiguration")]
    [InlineData("Programmatic")]
    public async Task SelectedSourcesReloadValidUpdatesAndRetainLastGood(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mock-proxy-reload-").FullName;
        WebApplication? app = null;
        const string original = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"old"}}}}}
            """;
        const string invalid = """{"schemaVersion":2,"destinations":{},"services":{}}""";
        var changed = original.Replace("\"old\"", "\"new\"", StringComparison.Ordinal);
        var published = original;
        var external = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RulesJson"] = original }).Build();
        try
        {
            await WriteAsync(original);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory });
            builder.WebHost.UseTestServer();
            if (source == "ExternalConfiguration")
                builder.Services.AddMockProxyGateway(external);
            else if (source == "Programmatic")
                builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(Volatile.Read(ref published)));
            else
                builder.Services.AddMockProxyGateway(o => o.RuleSource = source);
            app = builder.Build();
            app.UseMockProxyGateway();
            await app.StartAsync(ct);
            using var client = app.GetTestClient();
            Assert.Equal("old", await client.GetStringAsync("/mock-proxy/demo/check", ct));
            await WriteAsync(invalid);
            await Task.Delay(1400, ct);
            Assert.Equal("old", await client.GetStringAsync("/mock-proxy/demo/check", ct));
            await WriteAsync(changed);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await client.GetStringAsync("/mock-proxy/demo/check", ct) == "new")
                    return;
                await Task.Delay(100, ct);
            }
            Assert.Fail("Selected source did not reload its valid document.");

            async Task WriteAsync(string json)
            {
                if (source == "Programmatic")
                    Volatile.Write(ref published, json);
                else if (source == "ExternalConfiguration")
                    external["RulesJson"] = json;
                else
                {
                    var path = Path.Combine(directory, source == "File" ? "mock-proxy-gateway.json" : "appsettings.json");
                    var content = source switch
                    {
                        "Inline" => new JsonObject { ["MockProxyGateway"] = new JsonObject { ["Rules"] = JsonNode.Parse(json) } }.ToJsonString(),
                        "Configuration" => new JsonObject { ["MockProxyGateway"] = new JsonObject { ["RulesJson"] = json } }.ToJsonString(),
                        _ => json
                    };
                    var candidate = path + ".next";
                    await File.WriteAllTextAsync(candidate, content, ct);
                    File.Move(candidate, path, overwrite: true);
                }
            }
        }
        finally
        {
            if (app is not null)
                await app.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitSerializedRulesRemainAuthoritativeEvenWhenInitialValueEqualsHostValue()
    {
        const string original = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"original"}}}}}
            """;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration["MockProxyGateway:RulesJson"] = original;
        builder.Services.AddMockProxyGateway(o =>
        {
            o.RuleSource = "Configuration";
            o.RulesJson = original;
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        builder.Configuration["MockProxyGateway:RulesJson"] = original.Replace("original", "wrong", StringComparison.Ordinal);
        await Task.Delay(1400, ct);
        Assert.Equal("original", await client.GetStringAsync("/mock-proxy/demo/check", ct));
    }

    [Fact]
    public async Task InFlightRequestRetainsOriginalSnapshotAcrossReload()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"old"}}}}}
            """;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization(o => o.AddPolicy("gate", p => p.RequireAssertion(async context =>
        {
            if (context.Resource is HttpContext request && request.Request.Headers.ContainsKey("X-Hold"))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(request.RequestAborted);
            }
            return true;
        })));
        builder.Services.AddMockProxyGateway(o =>
        {
            o.AuthorizationPolicy = "gate";
            o.ProgrammaticRules = () => JsonElement.Parse(Volatile.Read(ref current));
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/demo/check");
        request.Headers.Add("X-Hold", "yes");
        var pending = client.SendAsync(request, ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Volatile.Write(ref current, current.Replace("\"old\"", "\"new\"", StringComparison.Ordinal));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var updated = false;
            while (DateTime.UtcNow < deadline)
            {
                if (await client.GetStringAsync("/mock-proxy/demo/check", ct) == "new")
                {
                    updated = true;
                    break;
                }
                await Task.Delay(100, ct);
            }
            Assert.True(updated);
        }
        finally
        {
            release.TrySetResult();
        }
        using var old = await pending;
        Assert.Equal("old", await old.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task MissingConfiguredPolicyDeniesGatewayWithoutAffectingHostRoutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(o =>
        {
            o.AuthorizationPolicy = "not-registered";
            o.ProgrammaticRules = () => JsonElement.Parse(Rules);
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        app.MapGet("/api/health", () => "normal");
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");
        using var denied = await client.PostAsync("/mock-proxy/demo/check", content, ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("normal", await client.GetStringAsync("/api/health", ct));
    }

    [Fact]
    public async Task SymlinkFixtureOutsideTrustedRootIsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("mock-proxy-link-").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(directory, "fixtures")).FullName;
            var outside = Path.Combine(directory, "outside.txt");
            await File.WriteAllTextAsync(outside, "synthetic", TestContext.Current.CancellationToken);
            File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outside);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory });
            builder.Services.AddMockProxyGateway(o =>
            {
                o.FixtureRoot = root;
                o.ProgrammaticRules = () => JsonElement.Parse("""
                    {"schemaVersion":1,"destinations":{},"services":{"fixture":{"default":{"mode":"mock","response":{"statusCode":200,"bodyFile":"link.txt"}}}}}
                    """);
            });
            await using var app = builder.Build();
            Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("File")]
    [InlineData("Configuration")]
    [InlineData("Inline")]
    public async Task ConflictingConfiguredRuleSourcesAreRejected(string source)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration["MockProxyGateway:RulesJson"] = Rules;
        builder.Configuration["MockProxyGateway:Rules:schemaVersion"] = "1";
        builder.Services.AddMockProxyGateway(o => o.RuleSource = source);
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
    }

}
