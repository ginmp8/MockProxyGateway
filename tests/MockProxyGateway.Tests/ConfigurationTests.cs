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
using Microsoft.Extensions.Options;
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
            // Keep preceding leaves eligible so the final two conditions are reached.
            // The same negative witnesses must hold for every supported rule source.
            await AssertNoMatchAsync("""{"value":"42","flag":true,"nullable":null}""");
            await AssertNoMatchAsync("""{"value":42,"flag":"true","nullable":null}""");
            await AssertNoMatchAsync("""{"value":42,"flag":true}""");
            await AssertNoMatchAsync("""{"value":42,"flag":true,"nullable":false}""");
            await AssertNoMatchAsync("""{"value":42,"flag":true,"nullable":null,"missing":null}""");
            await AssertNoMatchAsync("""{"value":42,"flag":true,"nullable":null,"missing":false}""");

            async Task AssertNoMatchAsync(string payload)
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var unmatched = await client.PostAsync("/mock-proxy/demo/check", content, ct);
                Assert.Equal(HttpStatusCode.NotFound, unmatched.StatusCode);
                Assert.Equal("", await unmatched.Content.ReadAsStringAsync(ct));
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
        var builder = TestHosting.CreateBuilder();
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
        var builder = TestHosting.CreateBuilder("Production");
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

    [Theory]
    [InlineData("application/*+json")]
    [InlineData("text/plain")]
    [InlineData("application/problem+json; charset=utf-8")]
    [InlineData("invalid+json")]
    [InlineData(" application/problem+json")]
    public async Task InvalidAdditionalJsonMediaTypeRejectsActivation(string mediaType)
    {
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["MockProxyGateway:AllowedJsonMediaTypes:0"] = mediaType;
        builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(Rules));
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
    }

    [Fact]
    public async Task ConfiguredAdditionalJsonMediaTypeUsesHostConfiguration()
    {
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["MockProxyGateway:AllowedJsonMediaTypes:0"] = "application/problem+json";
        builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(Rules));
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var body = new StringContent("""{"value":42,"flag":true,"nullable":null}""", Encoding.UTF8, "application/problem+json");
        using var result = await client.PostAsync("/mock-proxy/demo/check", body, ct);
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
    }

    [Fact]
    public async Task StartupOptionsRemainFixedWhileSelectedProviderReloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var published = Rules;
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(o =>
        {
            o.MaxBodySizeBytes = 128;
            o.ProgrammaticRules = () => JsonElement.Parse(Volatile.Read(ref published));
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        var mutable = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        mutable.MaxBodySizeBytes = 1;
        mutable.ProgrammaticRules = () => throw new InvalidOperationException("unexpected replacement provider");
        mutable.ReloadOnChange = false;

        const string matching = """{"value":42,"flag":true,"nullable":null}""";
        using var body = new StringContent(matching, Encoding.UTF8, "application/json");
        using var initial = await client.PostAsync("/mock-proxy/demo/check", body, ct);
        Assert.Equal(HttpStatusCode.Created, initial.StatusCode);
        Volatile.Write(ref published, Rules.Replace("\"statusCode\":201", "\"statusCode\":202", StringComparison.Ordinal));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var nextBody = new StringContent(matching, Encoding.UTF8, "application/json");
            using var next = await client.PostAsync("/mock-proxy/demo/check", nextBody, ct);
            if (next.StatusCode == HttpStatusCode.Accepted)
                return;
            Assert.Equal(HttpStatusCode.Created, next.StatusCode);
            await Task.Delay(100, ct);
        }
        Assert.Fail("The originally selected provider did not reload its valid rule document.");
    }

    [Theory]
    [InlineData("Configuration", false)]
    [InlineData("Configuration", true)]
    [InlineData("Explicit", false)]
    [InlineData("Explicit", true)]
    [InlineData("External", false)]
    [InlineData("External", true)]
    public async Task SerializedRuleSourcesRejectInvalidUnicodeAndOversizedDocuments(string source, bool oversized)
    {
        const string valid = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"VALUE"}}}}}
            """;
        var invalid = oversized
            ? new string(' ', 4_194_304) + valid
            : valid.Replace("VALUE", new string('\uD800', 1), StringComparison.Ordinal);
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (source == "External")
        {
            var external = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["RulesJson"] = invalid }).Build();
            builder.Services.AddMockProxyGateway(external);
        }
        else if (source == "Explicit")
            builder.Services.AddMockProxyGateway(o => { o.RuleSource = "Configuration"; o.RulesJson = invalid; });
        else
        {
            builder.Configuration["MockProxyGateway:RuleSource"] = "Configuration";
            builder.Configuration["MockProxyGateway:RulesJson"] = invalid;
            builder.Services.AddMockProxyGateway();
        }
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
    }

    [Fact]
    public async Task ExplicitSerializedRulesRemainAuthoritativeEvenWhenInitialValueEqualsHostValue()
    {
        const string original = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"original"}}}}}
            """;
        var builder = TestHosting.CreateBuilder();
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
        var builder = TestHosting.CreateBuilder();
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
        var builder = TestHosting.CreateBuilder();
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
        var builder = TestHosting.CreateBuilder();
        builder.Configuration["MockProxyGateway:RulesJson"] = Rules;
        builder.Configuration["MockProxyGateway:Rules:schemaVersion"] = "1";
        builder.Services.AddMockProxyGateway(o => o.RuleSource = source);
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
    }


    [Theory]
    [InlineData("File")]
    [InlineData("Inline")]
    [InlineData("Configuration")]
    public async Task FlattenedSerializedRuleChildrenRejectActivation(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("mock-proxy-source-conflict-").FullName;
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
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ContentRootPath = directory
            });
            builder.WebHost.UseTestServer();
            // A different provider contributes children under the serialized document.
            // Do not silently ignore them even when the selected source is otherwise valid.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MockProxyGateway:RulesJson:unexpected"] = "synthetic"
            });
            builder.Services.AddMockProxyGateway();
            await using var app = builder.Build();
            Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitScalarSectionWithNullChildRejectsActivation()
    {
        var builder = TestHosting.CreateBuilder();
        var external = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["GatewayRules"] = Rules,
                ["GatewayRules:RulesJson"] = null
            }).Build();
        builder.Services.AddMockProxyGateway(external.GetSection("GatewayRules"));
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingExplicitRuleSourcesAreRejected(bool externalConfiguration)
    {
        var builder = TestHosting.CreateBuilder();
        if (externalConfiguration)
        {
            var external = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["RulesJson"] = Rules }).Build();
            builder.Services.AddMockProxyGateway(external, o => o.RulesJson = Rules);
        }
        else
        {
            builder.Services.AddMockProxyGateway(o =>
            {
                o.RulesJson = Rules;
                o.ProgrammaticRules = () => JsonElement.Parse(Rules);
            });
        }
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());
    }

    [Theory]
    [InlineData("2030-01-01T00:00:00Z")]
    [InlineData("2030-01-01T00:00:00+00:00")]
    [InlineData("2030-01-01T00:00:00.0000000Z")]
    public async Task ExpiryUsesHostTimeProviderAtTheExactBoundaryWithoutReload(string expiry)
    {
        var boundary = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(boundary.AddSeconds(-1));
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"default"}},
              "rules":[{"id":"temporary","priority":10,"expiresAt":"EXPIRY",
               "conditions":{"source":"header","key":"X-Scenario","operator":"exists"},
               "action":{"mode":"mock","response":{"statusCode":201,"text":"temporary"}}}]}}}
            """.Replace("EXPIRY", expiry, StringComparison.Ordinal);
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddMockProxyGateway(o =>
        {
            o.ReloadOnChange = false;
            o.ProgrammaticRules = () => JsonElement.Parse(rules);
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Scenario", "synthetic");
        using var before = await client.GetAsync("/mock-proxy/demo/check", ct);
        Assert.Equal(HttpStatusCode.Created, before.StatusCode);
        clock.Set(boundary);
        Assert.Equal("default", await client.GetStringAsync("/mock-proxy/demo/check", ct));
    }

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }

    [Fact]
    public async Task AddingAndRemovingRulesKeepsConcurrentReadersOnWholeSnapshots()
    {
        var current = """
            {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"old"}}}}}
            """;
        var initial = current;
        var candidate = JsonNode.Parse(initial)!;
        candidate["services"]!["demo"]!["rules"] = JsonNode.Parse("""
            [{"id":"added","priority":1,"conditions":{"source":"header","key":"X-Scenario","operator":"exists"},
              "action":{"mode":"mock","response":{"statusCode":201,"text":"new"}}}]
            """);
        var changed = candidate.ToJsonString();
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(Volatile.Read(ref current)));
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Scenario", "synthetic");
        Assert.Equal("old", await client.GetStringAsync("/mock-proxy/demo/check", ct));
        using var readersStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var readers = Enumerable.Range(0, 4).Select(async _ =>
        {
            while (!readersStop.IsCancellationRequested)
            {
                using var response = await client.GetAsync("/mock-proxy/demo/check", ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                Assert.True((response.StatusCode == HttpStatusCode.OK && text == "old") ||
                    (response.StatusCode == HttpStatusCode.Created && text == "new"));
                seen.TryAdd(text, 0);
                await Task.Delay(20, ct);
            }
        }).ToArray();
        try
        {
            Volatile.Write(ref current, changed);
            await WaitForAsync("new");
            Volatile.Write(ref current, initial);
            await WaitForAsync("old");
        }
        finally
        {
            await readersStop.CancelAsync();
            await Task.WhenAll(readers);
        }
        Assert.Contains("old", seen.Keys);
        Assert.Contains("new", seen.Keys);

        async Task WaitForAsync(string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await client.GetStringAsync("/mock-proxy/demo/check", ct) == expected)
                    return;
                await Task.Delay(50, ct);
            }
            Assert.Fail("Rule update was not activated.");
        }
    }


    [Fact]
    public async Task ConcurrencyLimitFailsClosedAndReleasesAfterCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = TestHosting.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization(o => o.AddPolicy("gate", p => p.RequireAssertion(async context =>
        {
            if (context.Resource is HttpContext request && request.Request.Headers.ContainsKey("X-Hold"))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, request.RequestAborted);
            }
            return true;
        })));
        builder.Services.AddMockProxyGateway(o =>
        {
            o.MaxConcurrentRequests = 1;
            o.AuthorizationPolicy = "gate";
            o.ProgrammaticRules = () => JsonElement.Parse("""
                {"schemaVersion":1,"destinations":{},"services":{"demo":{"default":{"mode":"mock","response":{"statusCode":200,"text":"ready"}}}}}
                """);
        });
        await using var app = builder.Build();
        app.UseMockProxyGateway();
        var ct = TestContext.Current.CancellationToken;
        await app.StartAsync(ct);
        using var client = app.GetTestClient();
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mock-proxy/demo/check");
        request.Headers.Add("X-Hold", "yes");
        var pending = client.SendAsync(request, firstCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var rejected = await client.GetAsync("/mock-proxy/demo/check", ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        }
        finally
        {
            await firstCancellation.CancelAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var recovered = await client.GetAsync("/mock-proxy/demo/check", ct);
            if (recovered.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("ready", await recovered.Content.ReadAsStringAsync(ct));
                return;
            }
            Assert.Equal(HttpStatusCode.ServiceUnavailable, recovered.StatusCode);
            await Task.Delay(50, ct);
        }
        Assert.Fail("Cancelled request did not release its concurrency slot.");
    }

    [Fact]
    public async Task MissingSelectedFileAndDefaultEnvironmentGateRejectActivation()
    {
        var directory = Directory.CreateTempSubdirectory("mock-proxy-missing-").FullName;
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = directory });
            builder.Services.AddMockProxyGateway();
            await using var app = builder.Build();
            Assert.Throws<InvalidOperationException>(() => app.UseMockProxyGateway());

            var other = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Staging", ContentRootPath = directory });
            other.Services.AddMockProxyGateway(o => o.ProgrammaticRules = () => JsonElement.Parse(Rules));
            await using var staging = other.Build();
            Assert.Throws<InvalidOperationException>(() => staging.UseMockProxyGateway());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
