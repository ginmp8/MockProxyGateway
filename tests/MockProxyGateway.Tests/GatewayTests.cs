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
    private static readonly string[] MixedFlags = ["yes", "no"];
    private static readonly string[] EqualFlags = ["yes", "yes"];
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
    [InlineData("notEquals", "\"a\"", "{\"value\":\"b\"}", true)]
    [InlineData("notEquals", "\"a\"", "{\"value\":\"a\"}", false)]
    [InlineData("equals", "null", "{\"value\":null}", true)]
    [InlineData("equals", "null", "{}", false)]
    [InlineData("equals", "null", "{\"value\":\"null\"}", false)]
    [InlineData("equals", "true", "{\"value\":true}", true)]
    [InlineData("equals", "true", "{\"value\":\"true\"}", false)]
    [InlineData("notEquals", "null", "{\"value\":null}", false)]
    [InlineData("notEquals", "null", "{\"value\":false}", true)]
    [InlineData("notEquals", "null", "{}", false)]
    [InlineData("contains", "\"bc\"", "{\"value\":\"ad\"}", false)]
    [InlineData("startsWith", "\"ab\"", "{\"value\":\"zab\"}", false)]
    [InlineData("endsWith", "\"cd\"", "{\"value\":\"cdz\"}", false)]
    [InlineData("in", "[1,2]", "{\"value\":\"2\"}", false)]
    [InlineData("notIn", "[1,2]", "{\"value\":2}", false)]
    [InlineData("notIn", "[1,2]", "{}", false)]
    [InlineData("regex", "\"^a[0-9]+$\"", "{\"value\":\"a42z\"}", false)]
    [InlineData("greaterThan", "5", "{\"value\":5}", false)]
    [InlineData("greaterThanOrEqual", "5", "{\"value\":4}", false)]
    [InlineData("lessThan", "5", "{\"value\":5}", false)]
    [InlineData("lessThanOrEqual", "5", "{\"value\":6}", false)]
    [InlineData("between", "[4,6]", "{\"value\":4}", true)]
    [InlineData("between", "[4,6]", "{\"value\":6}", true)]
    [InlineData("between", "[4,6]", "{\"value\":7}", false)]
    [InlineData("between", "[4,6]", "{\"value\":3}", false)]
    [InlineData("jsonEquals", "{\"b\":2,\"a\":1}", "{\"value\":{\"a\":1,\"b\":3}}", false)]
    [InlineData("jsonEquals", "[1,false,null]", "{\"value\":[1,false,null]}", true)]
    [InlineData("jsonEquals", "[1,false,null]", "{\"value\":[false,1,null]}", false)]
    public async Task TypedOperatorBehavior(string operation, string operand, string body, bool matches)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"operator","priority":1,
               "conditions":{"source":"body","key":"$.value","operator":"OPERATOR","value":OPERAND},
               "action":{"mode":"mock","response":{"statusCode":201,"text":"operator-match"}}}
            ],"default":{"mode":"mock","response":{"statusCode":404,"text":"operator-miss"}}}}}
            """.Replace("OPERATOR", operation, StringComparison.Ordinal).Replace("OPERAND", operand, StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(matches ? HttpStatusCode.Created : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(matches ? "operator-match" : "operator-miss",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
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
    [InlineData("{\"source\":\"body\",\"key\":\"$..items..value..tag\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.value == $.expected[*]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count($.items[*])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.tags[0:1] == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.tags[0,1] == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.tags[0,1,2,3,4]) == 5]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.tags[0,1,2][0,1]) == 6]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$['items','items'][?count(@.tags[0,1,2]) == 3]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?$.values[0,1]]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?$..value]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.nested) ]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[*] == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@..tag == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@..nested..tag..leaf) == 2]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@..nested..tag == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[*] == @.value]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@.other[?@.value]]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@..value]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@.value] && @.other[?@.value]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@['value','value']]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$..items[?@.nested[?@.value]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@.value] == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$..items[?@.value]..value..tag\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$..items[?@..nested..tag]..value\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.value][?@.value][?@.value]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.value == 1e1000]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$[0,1,2][0,1]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.a.b.c.d.e.f.g.h.i.j.k.l.m.n.o.p.q\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"header\",\"key\":\"x\",\"operator\":\"regex\",\"value\":\"(\"}")]
    [InlineData("{\"all\":[]}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[0,1,2,3,?@.active]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.active,?@.override,?@.other]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, @.pattern)]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.names[*], 'x')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '(?=x)')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?search(@.name, '[^]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '[😃-😀]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '[a--z]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x{65}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x{3,2}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x{,2}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x{2')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '(x')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x)')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x*?')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '(((((((((x)))))))))')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$..items[?match(@.name, 'x')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[0,1,2,3,?@.nested[?@.active]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[0,?@.nested[?@.active]][0,1,2]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@.active],?@.other[?@.value]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(@.names[*]) == 2]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(@.name)]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(@.name) == @.limits[*]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?length(@.name) == count($..limit)]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '[A-B-C]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '[Z-A]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '((x{64}){64}){64}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x{32}y{32}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '\\\\\\\\p{Cs}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '\\\\\\\\p{IsBasicLatin}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '\\\\\\\\p{l}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '\\\\\\\\p{}')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '\\\\\\\\pL')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '[\\\\\\\\p{L}-z]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, '[a-\\\\\\\\p{L}]')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x') || length(@.names[*]) == 2]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(@.name) == 2 && 4]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(@.name, 'x') && search(@.name, @.pattern)]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match($.name, 'x') && @..flag]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match(@.name, @.pattern)]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?@.other[?match(@.name, 'x')]]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.nested[?match(@.name, 'x')]) == match(@.name, 'x')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?value(@.nested[?match(@.name, 'x')]) == @.flags[*]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?value(@.nested[?match(@.name, 'x')]) == $.flags[*]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.flags[*] == value(@.nested[?match(@.name, 'x')])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?$.flags[*] == value(@.nested[?match(@.name, 'x')])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.nested[?match(@.name, 'x')]) == value(@.other[?match(@.name, 'x')])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match(@.name, 'x')] == 42]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match(@.name, 'x')] && $.flag]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match(@.name, 'x')] && @.other[?@.flag]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$..items[?@.nested[?match(@.name, 'x')]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match(@.name, 'x')][0,0]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"method\",\"key\":null,\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.value\",\"operator\":\"equals\",\"value\":42,\"quantifier\":null}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.value\",\"operator\":\"jsonEquals\",\"value\":{\"nested\":[\"\\ud800\"]}}")]
    [InlineData("{\"source\":\"header\",\"key\":\"X-Scenario\\n\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"header\",\"key\":\"X Scenario\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"cookie\",\"key\":\"test=session\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"route\",\"key\":\"invalid route\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?length(value($.names[0,1])) == 1]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?match(value(@..name), 'x')]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?length(value(@.names[0,1])) == 1]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(value($.names[0,1])) == count(@.nested[?match(@.name, 'x')])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.nested[?match(@.name, 'x')]) == length(value($..names))]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(value($.names[?@])) == count(@.nested[?match(@.name, 'x')])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(count(1)) == 0]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(count(@.names[0,1])) == 0]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(length(length(@.name))) == 0]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?@.nested[?length(count($..names[*])) == 0]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?match(count(@.*), '[')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count($..nested[?@ == true]) == 1]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?$.nested[?@['name','name']]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?$.nested[?@.other[?@ == true]]]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count($.nested[?@ == true]) == length(value($.nested[0,1]))]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count(@.nested[?match(@.name, 'x')]) == count($.expected[0,1])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?value($..expected) == value(@.nested[?match(@.name, 'x')].flag)]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?count($.expected[?@]) == count(@.nested[?match(@.name, 'x')])]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?\\u00a0match(@.name, 'x')]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[\\u000b0]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$ \\u00a0 .name\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[?length(@.name)\\u000c==1]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\" $.value\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.value \",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"\\t$.value\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.value\\n\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"@.value\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$ \",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[١]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[1١]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[١:2]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[:٢]\",\"operator\":\"exists\"}")]
    [InlineData("{\"source\":\"body\",\"key\":\"$.items[::٢]\",\"operator\":\"exists\"}")]
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
    [InlineData("$['with.dot']", "{\"with.dot\":42}", 201)]
    [InlineData("$.items[-1]", "{\"items\":[0,42]}", 201)]
    [InlineData("$.items.*", "{\"items\":{\"one\":42}}", 201)]
    [InlineData("$.items[0:4:2]", "{\"items\":[0,1,42,3]}", 201)]
    [InlineData("$.items[0:4:2]", "{\"items\":[0,42,2,3]}", 404)]
    [InlineData("$.items[1,3]", "{\"items\":[0,1,2,42]}", 201)]
    [InlineData("$.items[1,3]", "{\"items\":[42,1,2,3]}", 404)]
    [InlineData("$[ 'with.dot' ]", "{\"with.dot\":42}", 201)]
    [InlineData("$.items[::-1]", "{\"items\":[0,42]}", 201)]
    [InlineData("$..value", "{\"nested\":{\"items\":[{\"value\":42}]}}", 201)]
    [InlineData("$..value", "{\"nested\":{\"items\":[{\"other\":42}]}}", 404)]
    [InlineData("$.items[?@.active == true].value", "{\"items\":[{\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.active == true].value", "{\"items\":[{\"active\":\"true\",\"value\":42}]}", 404)]
    [InlineData("$.items[?@.active == true].value", "{\"items\":[{\"active\":false,\"value\":42},{\"active\":true,\"value\":1}]}", 404)]
    [InlineData("$.items[?@.score >= 10].value", "{\"items\":[{\"score\":12,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.score >= 10].value", "{\"items\":[{\"score\":9,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.score >= 10].value", "{\"items\":[{\"score\":\"12\",\"value\":42}]}", 404)]
    [InlineData("$.items[?@.score == 0.1].value", "{\"items\":[{\"score\":0.1,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.score == 0.1].value", "{\"items\":[{\"score\":0.10000000000000000000000000001,\"value\":42}]}", 400)]
    [InlineData("$.items[?@.score == 1].value", "{\"items\":[{\"score\":1e1000,\"value\":42}]}", 400)]
    [InlineData("$.items[?@.flag].value", "{\"items\":[{\"flag\":null,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.flag].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?!@.flag].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?!@.flag && @.active].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?!@.flag && @.active].value", "{\"items\":[{\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?!@.flag && @.active].value", "{\"items\":[{\"flag\":false,\"active\":true,\"value\":42}]}", 404)]
    [InlineData("$.items[?!@.flag || @.active].value", "{\"items\":[{\"flag\":true,\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@['with.dot'] == 'yes'].value", "{\"items\":[{\"with.dot\":\"yes\",\"value\":42}]}", 201)]
    [InlineData("$.items[?(@.active == true && @.score >= 10) || @.override == true].value", "{\"items\":[{\"active\":true,\"score\":1,\"override\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?(@.active == true && @.score >= 10) || @.override == true].value", "{\"items\":[{\"active\":true,\"score\":1,\"override\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.score == @.target].value", "{\"items\":[{\"score\":12,\"target\":12,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.score >= $.min].value", "{\"min\":10,\"items\":[{\"score\":12,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.score >= $.min].value", "{\"min\":13,\"items\":[{\"score\":12,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.score >= $.min].value", "{\"items\":[{\"score\":12,\"value\":42}]}", 404)]
    [InlineData("$.items[?$.flag].value", "{\"flag\":null,\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?$.flag].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?@.tags[*]].value", "{\"items\":[{\"tags\":[null],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.tags[*]].value", "{\"items\":[{\"tags\":[],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.tags[*]) == 2].value", "{\"items\":[{\"tags\":[null,\"x\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.tags[*]) == 2].value", "{\"items\":[{\"tags\":[\"x\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.tags.*) == 0].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.tags[*]) == 'x'].value", "{\"items\":[{\"tags\":[\"x\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.tags[*]) == 'x'].value", "{\"items\":[{\"tags\":[\"x\",\"x\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.tags[*]) == null].value", "{\"items\":[{\"tags\":[null],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.tags[*]) == null].value", "{\"items\":[{\"tags\":[],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.tags[1:3]].value", "{\"items\":[{\"tags\":[0,null,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.tags[1:3]].value", "{\"items\":[{\"tags\":[0],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.tags[0:4:2]) == 2].value", "{\"items\":[{\"tags\":[0,1,2,3],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.tags[0:4:2]) == 2].value", "{\"items\":[{\"tags\":[0,1],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.tags[-1:]) == 'x'].value", "{\"items\":[{\"tags\":[\"y\",\"x\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.tags[-1:]) == 'x'].value", "{\"items\":[{\"tags\":[\"x\",\"y\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.tags[::-1]) == 2].value", "{\"items\":[{\"tags\":[1,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.tags[::0]) == 0].value", "{\"items\":[{\"tags\":[1,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?$.flags[*]].value", "{\"flags\":[null],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?$.flags[*]].value", "{\"flags\":[],\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?count($.flags[*]) == 2].value", "{\"flags\":[1,2],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?count($.flags[*]) == 2].value", "{\"flags\":[1],\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?value($.flags[1:2]) == null].value", "{\"flags\":[0,null],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?value($.flags[1:2]) == null].value", "{\"flags\":[0],\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.tags[0,1]) == 2].value", "{\"items\":[{\"tags\":[0,null],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.tags[0,1]) == 2].value", "{\"items\":[{\"tags\":[0],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.tags[0,0]) == 2].value", "{\"items\":[{\"tags\":[0],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.tags[0,0]) == 'x'].value", "{\"items\":[{\"tags\":[\"x\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@['a','b'][0,1]) == 4].value", "{\"items\":[{\"a\":[0,1],\"b\":[2,3],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.tags[0:1,1:2]) == 2].value", "{\"items\":[{\"tags\":[0,1],\"value\":42}]}", 201)]
    [InlineData("$.items[?count($.flags[0,0]) == 2].value", "{\"flags\":[null],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?count($.flags[0,0]) == 2].value", "{\"flags\":[],\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$..items[?@.active == true].value", "{\"nested\":{\"items\":[{\"active\":true,\"value\":42}]}}", 201)]
    [InlineData("$..items[?@.active == true].value", "{\"nested\":{\"items\":[{\"active\":false,\"value\":42}]}}", 404)]
    [InlineData("$.items[?@.active == true]..value", "{\"items\":[{\"active\":true,\"nested\":{\"value\":42}}]}", 201)]
    [InlineData("$.items[?@.active == true]..value", "{\"items\":[{\"active\":false,\"nested\":{\"value\":42}}]}", 404)]
    [InlineData("$..[?@.active == true].value", "{\"nested\":{\"items\":[{\"active\":true,\"value\":42}]}}", 201)]
    [InlineData("$..[?@.active == true].value", "{\"nested\":{\"items\":[{\"active\":false,\"value\":42}]}}", 404)]
    [InlineData("$..items[?count(@.tags[0,0]) == 2].value", "{\"items\":[{\"tags\":[1],\"value\":42}]}", 201)]
    [InlineData("$..items[?count(@.tags[0,0]) == 2].value", "{\"items\":[{\"tags\":[],\"value\":42}]}", 404)]
    [InlineData("$.items[?@..tag].value", "{\"items\":[{\"nested\":{\"tag\":null},\"value\":42}]}", 201)]
    [InlineData("$.items[?@..tag].value", "{\"items\":[{\"nested\":{\"other\":null},\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@..tag) == 2].value", "{\"items\":[{\"tag\":1,\"nested\":{\"tag\":2},\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@..tag) == 2].value", "{\"items\":[{\"nested\":{\"tag\":1},\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@..['tag']) == null].value", "{\"items\":[{\"nested\":{\"tag\":null},\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@..['tag']) == null].value", "{\"items\":[{\"nested\":{\"other\":null},\"value\":42}]}", 404)]
    [InlineData("$.items[?count($..flag) == 1].value", "{\"flags\":{\"flag\":true},\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?count($..flag) == 1].value", "{\"flags\":{\"other\":true},\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$..items[?@..tag].value", "{\"nested\":{\"items\":[{\"tag\":true,\"value\":42}]}}", 201)]
    [InlineData("$..items[?@..tag].value", "{\"nested\":{\"items\":[{\"other\":true,\"value\":42}]}}", 404)]
    [InlineData("$.items[?@.nested[?@.active == true]].value", "{\"items\":[{\"nested\":[{\"active\":true}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?@.active == true]].value", "{\"items\":[{\"nested\":[{\"active\":false}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?@.active]].value", "{\"items\":[{\"nested\":[{\"active\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?@.active]].value", "{\"items\":[{\"nested\":[{}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?@.active]) == 1].value", "{\"items\":[{\"nested\":[{\"active\":null},{}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?@.active]) == 1].value", "{\"items\":[{\"nested\":[{\"active\":true},{\"active\":false}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?@.active].tag) == 'x'].value", "{\"items\":[{\"nested\":[{\"active\":true,\"tag\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?@.active].tag) == 'x'].value", "{\"items\":[{\"nested\":[{\"active\":true,\"tag\":\"y\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?!@.disabled && @.active]].value", "{\"items\":[{\"nested\":[{\"active\":true}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?!@.disabled && @.active]].value", "{\"items\":[{\"nested\":[{}],\"value\":42}]}", 404)]
    [InlineData("$..items..value", "{\"items\":[{\"nested\":{\"value\":42}}]}", 201)]
    [InlineData("$..items..value", "{\"items\":[{\"nested\":{\"value\":41}}]}", 404)]
    [InlineData("$..items..['value']", "{\"root\":{\"items\":[{\"items\":[{\"value\":42}]}]}}", 201)]
    [InlineData("$..items..['value']", "{\"root\":{\"items\":[{\"items\":[{\"value\":41}]}]}}", 404)]
    [InlineData("$.groups[?@[*].active][?@.active == true].value", "{\"groups\":[[{\"active\":true,\"value\":42}]]}", 201)]
    [InlineData("$.groups[?@[*].active][?@.active == true].value", "{\"groups\":[[{\"active\":false,\"value\":42}]]}", 404)]
    [InlineData("$.groups[?@[*].active][?@.score >= $.min].value", "{\"min\":10,\"groups\":[[{\"active\":true,\"score\":12,\"value\":42}]]}", 201)]
    [InlineData("$.groups[?@[*].active][?@.score >= $.min].value", "{\"min\":13,\"groups\":[[{\"active\":true,\"score\":12,\"value\":42}]]}", 404)]
    [InlineData("$.groups[?!@[*].disabled][?!@.disabled && @.active].value", "{\"groups\":[[{\"active\":true,\"value\":42}]]}", 201)]
    [InlineData("$.groups[?!@[*].disabled][?!@.disabled && @.active].value", "{\"groups\":[[{\"active\":true,\"disabled\":false,\"value\":42}]]}", 404)]
    [InlineData("$.items[?@..nested..tag].value", "{\"items\":[{\"nested\":{\"tag\":null},\"value\":42}]}", 201)]
    [InlineData("$.items[?@..nested..tag].value", "{\"items\":[{\"nested\":{\"other\":null},\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@..nested..tag) == 1].value", "{\"items\":[{\"nested\":{\"tag\":1},\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@..nested..tag) == 1].value", "{\"items\":[{\"nested\":{\"other\":1},\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@..nested..tag) == null].value", "{\"items\":[{\"nested\":{\"tag\":null},\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@..nested..tag) == null].value", "{\"items\":[{\"nested\":{\"other\":null},\"value\":42}]}", 404)]
    [InlineData("$.items[?count($..nested..tag) == 1].value", "{\"nested\":{\"tag\":1},\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?count($..nested..tag) == 1].value", "{\"nested\":{\"other\":1},\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$..items[?@.active == true]..value", "{\"items\":[{\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$..items[?@.active == true]..value", "{\"items\":[{\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[0,?@.active == true].value", "{\"items\":[{\"active\":false,\"value\":1},{\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[0,?@.active == true].value", "{\"items\":[{\"active\":false,\"value\":1},{\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.active == true,0].value", "{\"items\":[{\"active\":false,\"value\":42},{\"active\":false,\"value\":1}]}", 201)]
    [InlineData("$.items[?@.active == true,0].value", "{\"items\":[{\"active\":false,\"value\":1},{\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[0,?!@.disabled && @.active].value", "{\"items\":[{\"value\":1},{\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[0,?!@.disabled && @.active].value", "{\"items\":[{\"value\":1},{\"value\":42}]}", 404)]
    [InlineData("$.items[?@.active == true,?@.override == true].value", "{\"items\":[{\"active\":false,\"override\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.active == true,?@.override == true].value", "{\"items\":[{\"active\":false,\"override\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.active == true,?@.override == true].value", "{\"items\":[{\"active\":true,\"override\":false,\"value\":42}]}", 201)]
    [InlineData("$.items[0,?@.nested[?@.active]].value", "{\"items\":[{\"value\":1},{\"nested\":[{\"active\":true}],\"value\":42}]}", 201)]
    [InlineData("$.items[0,?@.nested[?@.active]].value", "{\"items\":[{\"value\":1},{\"nested\":[{}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?@.active],0].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?@.active],?@.override].value", "{\"items\":[{\"override\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?@.active],?@.override].value", "{\"items\":[{\"nested\":[{}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?@.active]][?@.value].value", "{\"items\":[{\"nested\":[{\"active\":true}],\"child\":{\"value\":42}}]}", 201)]
    [InlineData("$.items[?@.nested[?@.active]][?@.value].value", "{\"items\":[{\"nested\":[{}],\"child\":{\"value\":42}}]}", 404)]
    [InlineData("$.items[?length(@.name) == 2].value", "{\"items\":[{\"name\":\"e\\u0301\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == 1].value", "{\"items\":[{\"name\":\"e\\u0301\",\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == 1].value", "{\"items\":[{\"name\":\"\\ud83d\\ude00\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == 0].value", "{\"items\":[{\"name\":\"\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) >= 2].value", "{\"items\":[{\"name\":[null,true],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@['name']) <= 1].value", "{\"items\":[{\"name\":{\"key\":null},\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) < 2].value", "{\"items\":[{\"name\":[],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) > 0].value", "{\"items\":[{\"name\":true,\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == 0].value", "{\"items\":[{\"name\":null,\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) != 0].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length($.label) == 2].value", "{\"label\":\"e\\u0301\",\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[0,?length(@.name) == 2].value", "{\"items\":[{\"value\":1},{\"name\":\"e\\u0301\",\"value\":42}]}", 201)]
    [InlineData("$.items[?@.override,?length(@.name) == 2].value", "{\"items\":[{\"name\":\"e\\u0301\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'test-.*')].value", "{\"items\":[{\"name\":\"test-alpha\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'test-.*')].value", "{\"items\":[{\"name\":\"pre-test-alpha\",\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, 'test-.*')].value", "{\"items\":[{\"name\":\"pre-test-alpha\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '.')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '.')].value", "{\"items\":[{\"name\":\"é\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '.')].value", "{\"items\":[{\"name\":\"\\r\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '.')].value", "{\"items\":[{\"name\":\"\\n\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[A-Z]+[0-9]?')].value", "{\"items\":[{\"name\":\"ABC7\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[A-Z]+[0-9]?')].value", "{\"items\":[{\"name\":\"ABC77\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '^x$')].value", "{\"items\":[{\"name\":\"^x$\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '^x$')].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '😀+')].value", "{\"items\":[{\"name\":\"😀😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?search(@.name, 'x')].value", "{\"items\":[{\"name\":null,\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, 'x')].value", "{\"items\":[{\"name\":42,\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, 'x')].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[0,?match(@.name, 'x')].value", "{\"items\":[{\"value\":0},{\"name\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$.items[0,?match(@.name, 'x')].value", "{\"items\":[{\"value\":0},{\"name\":\"y\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, \"x\")].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'a\\\\.b')].value", "{\"items\":[{\"name\":\"a.b\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x')].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x')].value", "{\"items\":[{\"name\":\"xx\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x{2}')].value", "{\"items\":[{\"name\":\"xx\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x{2}')].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x{2,4}')].value", "{\"items\":[{\"name\":\"xxxx\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x{2,4}')].value", "{\"items\":[{\"name\":\"xxxxx\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x{2,}')].value", "{\"items\":[{\"name\":\"xxxx\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x{2,}')].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '(AB|CD){2}')].value", "{\"items\":[{\"name\":\"ABCD\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '(AB|CD){2}')].value", "{\"items\":[{\"name\":\"AB\",\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, '(AB|CD)+')].value", "{\"items\":[{\"name\":\"pre-CDAB-post\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '(😀|é){2}')].value", "{\"items\":[{\"name\":\"😀é\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '(😀|é){2}')].value", "{\"items\":[{\"name\":\"😀😀😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'a|')].value", "{\"items\":[{\"name\":\"\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '(a|b)c')].value", "{\"items\":[{\"name\":\"bc\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '(a|b)c')].value", "{\"items\":[{\"name\":\"acx\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '(x{2}){3}')].value", "{\"items\":[{\"name\":\"xxxxxx\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '(x{2}){3}')].value", "{\"items\":[{\"name\":\"xxxxx\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[^x]')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^😀]')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, '[^😀]')].value", "{\"items\":[{\"name\":\"😀😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, '[^😀]')].value", "{\"items\":[{\"name\":\"😀x\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[😀-🙏]')].value", "{\"items\":[{\"name\":\"😃\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[😀-🙏]')].value", "{\"items\":[{\"name\":\"🦊\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[À-Ö]+')].value", "{\"items\":[{\"name\":\"ÉÖ\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[À-Ö]+')].value", "{\"items\":[{\"name\":\"é\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[😀é]+')].value", "{\"items\":[{\"name\":\"😀é\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[é-😀]')].value", "{\"items\":[{\"name\":\"𐐀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^é-😀]')].value", "{\"items\":[{\"name\":\"𐐀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[^x]')].value", "{\"items\":[{\"name\":\"\\n\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[-a]')].value", "{\"items\":[{\"name\":\"-\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[a-]')].value", "{\"items\":[{\"name\":\"-\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[ .]')].value", "{\"items\":[{\"name\":\" \",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[ .]')].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[\\\\]-] ')].value", "{\"items\":[{\"name\":\"] \",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^😀]{2}')].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^😀]{2}')].value", "{\"items\":[{\"name\":\"a😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}+')].value", "{\"items\":[{\"name\":\"é𐐀A\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}+')].value", "{\"items\":[{\"name\":\"A1\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '\\\\p{Lu}')].value", "{\"items\":[{\"name\":\"𐐀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{Lu}')].value", "{\"items\":[{\"name\":\"𐐨\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '\\\\p{Ll}')].value", "{\"items\":[{\"name\":\"𐐨\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{Nd}+')].value", "{\"items\":[{\"name\":\"١𝟡\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{N}+')].value", "{\"items\":[{\"name\":\"Ⅳ²\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{M}')].value", "{\"items\":[{\"name\":\"́\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{So}')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{S}')].value", "{\"items\":[{\"name\":\"$\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{Z}')].value", "{\"items\":[{\"name\":\"\u2028\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{P}')].value", "{\"items\":[{\"name\":\"—\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{C}')].value", "{\"items\":[{\"name\":\"\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{Cn}')].value", "{\"items\":[{\"name\":\"͸\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\P{L}')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\P{L}')].value", "{\"items\":[{\"name\":\"𐐀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, '\\\\P{L}')].value", "{\"items\":[{\"name\":\"𐐀𐐨\",\"value\":42}]}", 404)]
    [InlineData("$.items[?search(@.name, '\\\\P{L}')].value", "{\"items\":[{\"name\":\"𐐀😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[\\\\p{L}0-9😀]+')].value", "{\"items\":[{\"name\":\"𐐀2😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[\\\\p{L}0-9😀]+')].value", "{\"items\":[{\"name\":\"𐐀-😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[^\\\\p{L}0-9]')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^\\\\p{L}0-9]')].value", "{\"items\":[{\"name\":\"𐐀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '[\\\\P{L}]')].value", "{\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^\\\\P{L}]')].value", "{\"items\":[{\"name\":\"𐐀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '[^\\\\p{L}\\\\P{L}]')].value", "{\"items\":[{\"name\":\"a\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '(\\\\p{Lu}|\\\\p{Nd}){2}')].value", "{\"items\":[{\"name\":\"𐐀١\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}.')].value", "{\"items\":[{\"name\":\"𐐀😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}.')].value", "{\"items\":[{\"name\":\"𐐀\\n\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, '\\\\p{L}\\\\p{M}')].value", "{\"items\":[{\"name\":\"é\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x') || @.active].value", "{\"label\":\"é\",\"items\":[{\"name\":\"y\",\"active\":false,\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, 'x') || @.active].value", "{\"label\":\"é\",\"items\":[{\"name\":\"y\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x') && @.active == true].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x') && @.active == true].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.active == true && match(@.name, 'x')].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == 2 && @.active].value", "{\"label\":\"é\",\"items\":[{\"name\":\"é\",\"active\":false,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == 2 && @.active].value", "{\"label\":\"é\",\"items\":[{\"name\":\"😀\",\"active\":true,\"value\":42}]}", 404)]
    [InlineData("$.items[?!match(@.name, 'x') && @.active == true].value", "{\"label\":\"é\",\"items\":[{\"name\":\"y\",\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?!match(@.name, 'x') && @.active == true].value", "{\"label\":\"é\",\"items\":[{\"name\":\"y\",\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x') || @.active == true && length(@.name) == 2].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"active\":false,\"value\":42}]}", 201)]
    [InlineData("$.items[?(match(@.name, 'x') || @.active == true) && length(@.name) == 2].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?!(match(@.name, 'x') || @.active == true)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"y\",\"active\":false,\"value\":42}]}", 201)]
    [InlineData("$.items[?!(match(@.name, 'x') || @.active == true)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"y\",\"active\":true,\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.missing) != 2 && search(@.name, 'x')].value", "{\"label\":\"é\",\"items\":[{\"name\":\"xx\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.missing) == 2 || search(@.name, 'x')].value", "{\"label\":\"é\",\"items\":[{\"name\":\"yy\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x') && count(@.tags[*]) == 2].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"tags\":[1,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == 1 && @.nested[?@.active == true]].value", "{\"label\":\"é\",\"items\":[{\"name\":\"😀\",\"nested\":[{\"active\":true}],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == 1 && @.nested[?@.active == true]].value", "{\"label\":\"é\",\"items\":[{\"name\":\"😀\",\"nested\":[{\"active\":false}],\"value\":42}]}", 404)]
    [InlineData("$.items[?match(@.name, 'x') && length($.label) == 2].value", "{\"label\":\"é\",\"items\":[{\"name\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(@.name) == 2]].value", "{\"items\":[{\"nested\":[{\"name\":\"é\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(@.name) == 1]].value", "{\"items\":[{\"nested\":[{\"name\":\"é\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(@.name, '..')]].value", "{\"items\":[{\"nested\":[{\"name\":\"é\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?search(@.name, 'z')]].value", "{\"items\":[{\"nested\":[{\"name\":\"é\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')]].value", "{\"items\":[{\"name\":\"x\",\"nested\":[{\"name\":\"y\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')]].value", "{\"items\":[{\"name\":\"y\",\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?!@.nested[?match(@.name, 'x')]].value", "{\"items\":[{\"nested\":[{\"name\":\"y\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?!@.nested[?match(@.name, 'x')]].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?length(@.name) == 2 && !search(@.name, 'z')]].value", "{\"items\":[{\"nested\":[{\"name\":\"é\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(@.name) == 2 && !search(@.name, 'z')]].value", "{\"items\":[{\"nested\":[{\"name\":\"zz\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')] && @.active == true].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')] && @.active == true].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')].flag].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')].flag].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(@.name, 'x')]].value", "{\"items\":[{\"nested\":[{\"name\":\"y\"}],\"value\":42},{\"nested\":[{\"name\":\"x\"}],\"value\":1}]}", 404)]
    [InlineData("$.items[?length('é') == 2].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length('é') == 1].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length(\"😀\") == 1].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length('') == 0].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length('a)b') == 3].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length('😀') == 1 && @.active == true].value", "{\"items\":[{\"value\":42,\"active\":false}]}", 404)]
    [InlineData("$.items[?@.nested[?length('é') == 2 && @.active == true]].value", "{\"items\":[{\"nested\":[{\"active\":true}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')]) == @.flag].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42,\"flag\":{\"name\":\"x\",\"flag\":5}}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')]) == @.flag].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42,\"flag\":5}]}", 404)]
    [InlineData("$.items[?1 == count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42}]}", 201)]
    [InlineData("$.items[?0 == count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.limit < count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42,\"limit\":0}]}", 201)]
    [InlineData("$.items[?@.limit > count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42,\"limit\":0}]}", 404)]
    [InlineData("$.items[?4 <= value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42}]}", 201)]
    [InlineData("$.items[?6 <= value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.expected == value(@.nested[?match(@.name, 'x')].missing)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.expected == value(@.nested[?match(@.name, 'x')].missing)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42,\"expected\":null}]}", 404)]
    [InlineData("$.items[?@.expected == value(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42,\"expected\":{\"flag\":5,\"name\":\"x\"}}]}", 201)]
    [InlineData("$.items[?'4' == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":4}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == 2].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"},{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) >= 2].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"},{\"name\":\"y\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 2]) == 0].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?search(@.name, 'x')]) > 0].value", "{\"items\":[{\"nested\":[{\"name\":\"ax\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == null].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == null].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) != null].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null},{\"name\":\"x\",\"flag\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == 4].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":\"4\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) >= 4].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) > '\\uE000'].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":\"😀\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == 'yes'].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":\"yes\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == true].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":true}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == null].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":[]}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == count(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[1],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == count(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[1,2],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.other[*]) > count(@.nested[?length(@.name) == 1])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[1,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.other[*]) < count(@.nested[?length(@.name) == 1])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[1,2],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == count(@.missing[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"xx\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == value(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[1],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == value(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[\"1\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.other[*]) < count(@.nested[?length(@.name) == 1])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[0],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == value(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null}],\"other\":[null],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == value(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null}],\"other\":[],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == value(@.other[*])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"other\":[1,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.other[*]) == value(@.nested[?length(@.name) == 1].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":{\"a\":1,\"b\":[true,null]}}],\"other\":[{\"b\":[true,null],\"a\":1}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.other[*]) == value(@.nested[?length(@.name) == 1].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":[1,2]}],\"other\":[[2,1]],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == @.limit].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == @.limit].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"limit\":2,\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == @.limit].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"limit\":\"1\",\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) != @.limit].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) <= @.limit].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null}],\"expected\":null,\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"expected\":null,\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) <= @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) < @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":{\"a\":1,\"b\":[true,null]}}],\"expected\":{\"b\":[true,null],\"a\":1},\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":[1,2]}],\"expected\":[2,1],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) >= @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":5}],\"expected\":4,\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) > @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":\"😀\"}],\"expected\":\"\",\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1},{\"name\":\"y\",\"flag\":1}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?length(@.name) == 1].flag) == @.expected].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1},{\"name\":\"y\",\"flag\":1}],\"expected\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == '1'].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) != '1'].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?null != count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?true == count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) > false].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?1 == length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?2 == length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?0 < length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?2 < length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?@.limit >= length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.missing == length(@.missing)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?null == length(@.missing)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 404)]
    [InlineData("$.items[?'1' != length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?match('😀', '.')].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?match('é', '.')].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?match('é', '..')].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?search('abc', 'b')].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?match('abc', 'b')].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?match('', '')].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?search('a,b', ',')].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?match('x', 'x') && @.active == true].value", "{\"items\":[{\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?!search('abc', 'z') && @.active == true].value", "{\"items\":[{\"active\":true,\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?match('x', 'x')]].value", "{\"items\":[{\"nested\":[{}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?search('abc', 'z')]].value", "{\"items\":[{\"nested\":[{}],\"value\":42}]}", 404)]
    [InlineData("$.items[?match('x', 'x')].value", "{\"items\":[]}", 404)]
    [InlineData("$.items[?length(42) == 0].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length(42) != 0].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(true) > 0].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length(false) != null].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(null) == null].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == @.limit].value", "{\"items\":[{\"name\":\"😀\",\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == @.limit].value", "{\"items\":[{\"name\":\"😀\",\"limit\":2,\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == @.limit].value", "{\"items\":[{\"name\":\"😀\",\"limit\":\"1\",\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) <= @.limit].value", "{\"items\":[{\"name\":\"é\",\"limit\":2,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == @.limit].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) <= @.limit].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) < @.limit].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == @.limit].value", "{\"items\":[{\"limit\":null,\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) != @.limit].value", "{\"items\":[{\"limit\":null,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == @.limit].value", "{\"items\":[{\"name\":[],\"limit\":0,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) != @.limit].value", "{\"items\":[{\"name\":\"x\",\"limit\":{},\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == '1'].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 404)]
    [InlineData("$.items[?match(42, '42')].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?!match(true, 'true')].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?search(null, 'null')].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?match('x', 1)].value", "{\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?!search(@.name, false)].value", "{\"items\":[{\"name\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length('😀') == @.limit].value", "{\"items\":[{\"limit\":1,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == @.limit && @.active == true].value", "{\"items\":[{\"name\":\"x\",\"limit\":1,\"active\":false,\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == $.limit].value", "{\"limit\":1,\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(@.name) == @.limit]].value", "{\"items\":[{\"nested\":[{\"name\":\"😀\",\"limit\":1}],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"é\",\"other\":[1,2],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"é\",\"other\":\"😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) < length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"😀\",\"other\":{\"a\":1,\"b\":2},\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) > length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"😀\",\"other\":{\"a\":1,\"b\":2},\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) <= length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) != length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"name\":null,\"other\":[],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"name\":null,\"other\":false,\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) < length(@.other)].value", "{\"label\":\"é\",\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length('😀') >= length(@.name)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"é\",\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == length('😀')].value", "{\"label\":\"é\",\"items\":[{\"name\":[null],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == length($.label)].value", "{\"label\":\"é\",\"items\":[{\"name\":\"é\",\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(@.name) == length(@.other)]].value", "{\"label\":\"é\",\"items\":[{\"nested\":[{\"name\":\"é\",\"other\":[1,2]}],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value(@.names[*])) == 2].value", "{\"items\":[{\"names\":[\"é\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value(@.names[*])) == 2].value", "{\"items\":[{\"names\":[\"é\",\"ab\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(value(@.names[*])) == @.missing].value", "{\"items\":[{\"names\":[],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value(@.names[*])) == null].value", "{\"items\":[{\"names\":[null],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(value(@.names[*])) == 2].value", "{\"items\":[{\"names\":[[1,2]],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value(@.names[*])) == 2].value", "{\"items\":[{\"names\":[{\"a\":1,\"b\":2}],\"value\":42}]}", 201)]
    [InlineData("$.items[?1 == length(value(@.names[*]))].value", "{\"items\":[{\"names\":[\"😀\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value(@.names[*])) == length(value(@.other[*]))].value", "{\"items\":[{\"names\":[\"😀\"],\"other\":[[1]],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value($.labels[*])) == 1].value", "{\"labels\":[\"😀\"],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?match(value(@.names[*]), '..')].value", "{\"items\":[{\"names\":[\"é\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?match(value(@.names[*]), '..')].value", "{\"items\":[{\"names\":[\"ab\",\"cd\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?search(value(@.names[*]), 'b')].value", "{\"items\":[{\"names\":[\"abc\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?!search(value(@.names[*]), 'b')].value", "{\"items\":[{\"names\":[null],\"value\":42}]}", 201)]
    [InlineData("$.items[?search(value($.labels[*]), '.')].value", "{\"labels\":[\"😀\"],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == count(@.other[*])].value", "{\"labels\":[1],\"items\":[{\"name\":\"é\",\"other\":[null,true],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == count(@.other[*])].value", "{\"labels\":[1],\"items\":[{\"name\":\"😀\",\"other\":[null,true],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.other[*]) < length(@.name)].value", "{\"labels\":[1],\"items\":[{\"name\":\"é\",\"other\":[1],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.other[*]) > length(@.name)].value", "{\"labels\":[1],\"items\":[{\"name\":\"é\",\"other\":[1],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == count(@.missing[*])].value", "{\"labels\":[1],\"items\":[{\"name\":\"\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.missing) == count(@.missing[*])].value", "{\"labels\":[1],\"items\":[{\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.missing) != count(@.missing[*])].value", "{\"labels\":[1],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == value(@.other[*])].value", "{\"labels\":[1],\"items\":[{\"name\":\"😀\",\"other\":[1],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.other[*]) >= length(@.name)].value", "{\"labels\":[1],\"items\":[{\"name\":\"😀\",\"other\":[1],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.other[*]) < length(@.name)].value", "{\"labels\":[1],\"items\":[{\"name\":\"😀\",\"other\":[1],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.name) == value(@.other[*])].value", "{\"labels\":[1],\"items\":[{\"name\":\"😀\",\"other\":[\"1\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.missing) == value(@.other[*])].value", "{\"labels\":[1],\"items\":[{\"other\":[1,1],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.other[*]) == length(@.missing)].value", "{\"labels\":[1],\"items\":[{\"other\":[null],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.other[*]) == length(@.missing)].value", "{\"labels\":[1],\"items\":[{\"other\":[],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == count($.labels[*])].value", "{\"labels\":[1],\"items\":[{\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == length(@.name)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) == count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name) < count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) < length('ab')].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length('ab') > value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) > length('ab')].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].missing) == length(@.missing)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.missing) == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@.nested) == count(@.nested[?length(@.name) == 1])].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?length(@.name) == 1]) == length(@.nested)].value", "{\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":1}],\"name\":\"😀\",\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(value(@.*)) == 2]].value", "{\"items\":[{\"nested\":[{\"name\":\"é\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(value(@.*)) == 2]].value", "{\"items\":[{\"nested\":[{\"name\":\"😀\",\"extra\":\"ab\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match(value(@.names[*]), '..')]].value", "{\"items\":[{\"nested\":[{\"names\":[\"é\"]}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?match(value(@.names[*]), '..')]].value", "{\"items\":[{\"nested\":[{\"names\":[\"ab\",\"cd\"]}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?search(value(@.names[*]), 'b')]].value", "{\"items\":[{\"nested\":[{\"names\":[\"abc\"]}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?search(value(@.names[*]), 'b')]].value", "{\"items\":[{\"nested\":[{\"names\":[null]}],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(value(@.names[*])) == count(@.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"names\":[\"😀\"],\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == length(value(@.names[*]))].value", "{\"items\":[{\"names\":[\"ab\"],\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(value(@.names[*])) == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"names\":[],\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == length(value(@.names[*]))].value", "{\"items\":[{\"names\":[\"ab\",\"cd\"],\"nested\":[{\"name\":\"x\",\"flag\":null}],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(count(@.*)) == @.missing].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(count(@.*)) == 0].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 404)]
    [InlineData("$.items[?@.missing == length(count(@.absent[*]))].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(length(@.name)) == length(null)].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(length(@.missing)) != null].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(count(@.*), '2')].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 404)]
    [InlineData("$.items[?!search(length(@.name), '2')].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?match(@.name, length(@.name))].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 404)]
    [InlineData("$.items[?!search(@.name, count(@.*))].value", "{\"items\":[{\"name\":\"ab\",\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(count(@.*)) == @.missing]].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?match(@.name, count(@.*))]].value", "{\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?length(@.name) == $.limit]].value", "{\"limit\":2,\"items\":[{\"limit\":1,\"nested\":[{\"name\":\"ab\",\"limit\":1}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(@.name) == $.limit]].value", "{\"limit\":1,\"items\":[{\"limit\":2,\"nested\":[{\"name\":\"ab\",\"limit\":2}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?match($.name, 'x')]].value", "{\"name\":\"x\",\"items\":[{\"name\":\"y\",\"nested\":[{\"name\":\"y\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?match($.name, 'x')]].value", "{\"name\":\"y\",\"items\":[{\"name\":\"x\",\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?length(value($.names[*])) == 1]].value", "{\"names\":[\"😀\"],\"items\":[{\"nested\":[{\"names\":[\"ab\"]}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?length(value($.names[*])) == 1]].value", "{\"names\":[\"a\",\"b\"],\"items\":[{\"nested\":[{\"names\":[\"a\"]}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?length(count($.names[*])) == 0]].value", "{\"names\":[],\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match($.name, 'x') && @.flag == true]) == 1].value", "{\"name\":\"x\",\"items\":[{\"nested\":[{\"flag\":true},{\"flag\":false}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match($.name, 'x')].flag) == null].value", "{\"name\":\"x\",\"items\":[{\"nested\":[{\"flag\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match($.name, 'x')].flag) == null].value", "{\"name\":\"y\",\"items\":[{\"nested\":[{\"flag\":null}],\"value\":42}]}", 404)]
    [InlineData("$.items[?@.nested[?@.value == $.limit]].value", "{\"limit\":2,\"items\":[{\"limit\":1,\"nested\":[{\"limit\":1,\"value\":2}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?@.value == $.limit]].value", "{\"limit\":1,\"items\":[{\"limit\":2,\"nested\":[{\"limit\":2,\"value\":2}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?@.value == $.limit]) == 1].value", "{\"limit\":2,\"items\":[{\"limit\":1,\"nested\":[{\"limit\":1,\"value\":2}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?@.value == $.limit]) == 1].value", "{\"limit\":1,\"items\":[{\"limit\":2,\"nested\":[{\"limit\":2,\"value\":2}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?@.value == $.limit].value) == 2].value", "{\"limit\":2,\"items\":[{\"limit\":1,\"nested\":[{\"limit\":1,\"value\":2}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?$.flag]].value", "{\"flag\":null,\"items\":[{\"nested\":[{}],\"value\":42}]}", 201)]
    [InlineData("$.items[?@.nested[?$.flag]].value", "{\"items\":[{\"flag\":true,\"nested\":[{\"flag\":true}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == $.limit].value", "{\"limit\":1,\"items\":[{\"limit\":0,\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == $.limit].value", "{\"limit\":2,\"items\":[{\"limit\":1,\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?$.limit < count(@.nested[?match(@.name, 'x')])].value", "{\"limit\":0,\"items\":[{\"limit\":2,\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?$.limit < count(@.nested[?match(@.name, 'x')])].value", "{\"limit\":\"0\",\"items\":[{\"limit\":0,\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == $.flag].value", "{\"flag\":null,\"items\":[{\"flag\":1,\"nested\":[{\"name\":\"x\",\"flag\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == $.flag].value", "{\"flag\":null,\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?$.flag == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"items\":[{\"flag\":null,\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?$.flag == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"flag\":{\"b\":[true,null],\"a\":1},\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":{\"a\":1,\"b\":[true,null]}}],\"value\":42}]}", 201)]
    [InlineData("$.items[?$.nested[?@ == true]].value", "{\"items\":[{\"value\":42}],\"nested\":[true]}", 201)]
    [InlineData("$.items[?$.nested[?@ == true]].value", "{\"items\":[{\"value\":42,\"nested\":[true]}],\"nested\":[false]}", 404)]
    [InlineData("$.items[?count($.nested[?$.value]) == 1].value", "{\"items\":[{\"value\":42}],\"nested\":[0],\"value\":null}", 201)]
    [InlineData("$.items[?count($.nested[?$.value]) == 1].value", "{\"items\":[{\"value\":42}],\"nested\":[0]}", 404)]
    [InlineData("$.items[?1 == count($.nested[?match(@.name, 'x')])].value", "{\"items\":[{\"value\":42}],\"nested\":[{\"name\":\"x\"}]}", 201)]
    [InlineData("$.items[?count($.nested[?match(@.name, 'x')]) == 1].value", "{\"items\":[{\"value\":42}],\"nested\":[{\"name\":\"y\"}]}", 404)]
    [InlineData("$.items[?value($.nested[?@ == null]) == null].value", "{\"items\":[{\"value\":42}],\"nested\":[null]}", 201)]
    [InlineData("$.items[?value($.nested[?@ == null]) == null].value", "{\"items\":[{\"value\":42}],\"nested\":[false]}", 404)]
    [InlineData("$.items[?count($.nested[?@ == true]) == length($.nested)].value", "{\"items\":[{\"value\":42,\"nested\":[]}],\"nested\":[true]}", 201)]
    [InlineData("$.items[?count($.nested[?@ == true]) == length($.nested)].value", "{\"items\":[{\"value\":42,\"nested\":[true]}],\"nested\":[true,false]}", 404)]
    [InlineData("$.items[?length(@.name) == count($.nested[?@ == true])].value", "{\"items\":[{\"value\":42,\"name\":\"😀\"}],\"nested\":[true]}", 201)]
    [InlineData("$.items[?length(value($.labels[*])) == count($.nested[?@ == true])].value", "{\"items\":[{\"value\":42}],\"labels\":[\"😀\"],\"nested\":[true]}", 201)]
    [InlineData("$.items[?length(value($.labels[*])) == count($.nested[?@ == true])].value", "{\"items\":[{\"value\":42}],\"labels\":[\"x\",\"y\"],\"nested\":[true]}", 404)]
    [InlineData("$.items[?value($.nested[?@ == null]) != length($.missing)].value", "{\"items\":[{\"value\":42}],\"nested\":[null]}", 201)]
    [InlineData("$.items[?value($.nested[?@ == null]) == length($.missing)].value", "{\"items\":[{\"value\":42}],\"nested\":[false]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == count($.expected[*])].value", "{\"expected\":[0],\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@.name, 'x')]) == count($.expected[*])].value", "{\"expected\":[0,1],\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?count($.expected[*]) < count(@.nested[?match(@.name, 'x')])].value", "{\"expected\":[],\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value($.expected[*]) == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"expected\":[{\"a\":1}],\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":{\"a\":1}}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == value($.expected[*])].value", "{\"expected\":[null],\"items\":[{\"nested\":[{\"name\":\"x\",\"flag\":null}],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?match(@.name, 'x')].flag) == value($.expected[*])].value", "{\"expected\":[null],\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 404)]
    [InlineData("$.items[?value($.expected[*]) == value(@.nested[?match(@.name, 'x')].flag)].value", "{\"expected\":[1,2],\"items\":[{\"nested\":[{\"name\":\"x\"}],\"value\":42}]}", 201)]
    [InlineData("$.items[?count($.expected[*]) == count($.nested[?@ == true])].value", "{\"expected\":[1],\"nested\":[true,false],\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@, 'x')]) == length($.label)].value", "{\"label\":\"😀\",\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?count(@.nested[?match(@, 'x')]) == length($.label)].value", "{\"label\":\"ab\",\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?length(value($.labels[*])) == count(@.nested[?match(@, 'x')])].value", "{\"labels\":[\"😀\"],\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 201)]
    [InlineData("$.items[?length(value($.labels[*])) == count(@.nested[?match(@, 'x')])].value", "{\"labels\":[\"a\",\"b\"],\"items\":[{\"nested\":[\"x\"],\"value\":42}]}", 404)]
    [InlineData("$.items[?value(@.nested[?@ == null && $.enabled]) != length($.missing)].value", "{\"enabled\":true,\"items\":[{\"nested\":[null],\"value\":42}]}", 201)]
    [InlineData("$.items[?value(@.nested[?@ == null && $.enabled]) == length($.missing)].value", "{\"enabled\":true,\"items\":[{\"nested\":[false],\"value\":42}]}", 201)]
    [InlineData("$.value\u00a0", "{\"value\":0,\"value\\u00a0\":42}", 201)]
    [InlineData("$.value\u00a0", "{\"value\":42,\"value\\u00a0\":0}", 404)]
    [InlineData("$.items[?length(@.name\u00a0) == 1].value", "{\"items\":[{\"name\":\"wrong\",\"name\\u00a0\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$.items[?length(@.name\u00a0) == 1].value", "{\"items\":[{\"name\":\"x\",\"name\\u00a0\":\"wrong\",\"value\":42}]}", 404)]
    [InlineData("$.items[?length(@['name\u00a0']) == 1].value", "{\"items\":[{\"name\":\"wrong\",\"name\\u00a0\":\"x\",\"value\":42}]}", 201)]
    [InlineData("$ \t.value", "{\"value\":42}", 201)]
    [InlineData("$ \n['value']", "{\"value\":42}", 201)]
    [InlineData("$.items[?@.value == 42 ].value", "{\"items\":[{\"value\":42}]}", 201)]
    [InlineData("$.١", "{\"١\":42}", 201)]
    [InlineData("$.items['١']", "{\"items\":{\"١\":42}}", 201)]
    [InlineData("$.items[1]", "{\"items\":[0,42]}", 201)]
    public async Task RfcSelectorsSupportBoundedPathsAndFilters(string selector, string payload, int expected)
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
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
    }

    [Fact]
    public async Task JsonPathFilterWorkIsChargedBeforeAnyAction()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"filter","priority":1,"conditions":{"source":"body","key":"$.items[?@.active == true].value","operator":"equals","value":42},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        var json = "{\"items\":[" + string.Join(',', Enumerable.Repeat("{\"active\":true,\"value\":42}", 2000)) + "]}";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task JsonPathUnionWorkIsChargedBeforeAnyAction()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"union","priority":1,"conditions":{"source":"body","key":"$[0,0,0,0]","operator":"equals","value":0},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        var json = "[" + string.Join(',', Enumerable.Repeat("0", 16383)) + "]";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    [InlineData("POST", "/check", "application/json", 201)]
    [InlineData("post", "/check", "application/json", 201)]
    [InlineData("POST", "/CHECK", "application/json", 404)]
    [InlineData("GET", "/check", "application/json", 404)]
    [InlineData("POST", "/check", "text/plain", 404)]
    public async Task RegexConditionsRespectSourceSpecificCase(string method, string path, string type, int expected)
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"match","priority":1,"conditions":{"all":[
                 {"source":"method","operator":"regex","value":"^post$"},
                 {"source":"path","operator":"regex","value":"^/check$"},
                 {"source":"contentType","operator":"regex","value":"^APPLICATION/JSON$"}]},
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
    [InlineData("X-Synthetic\n", "value")]
    [InlineData("Content-Length", "5")]
    [InlineData("Connection", "close")]
    [InlineData("X-Synthetic", "value\r\ninjected: yes")]
    [InlineData("Content-Type", "invalid media type")]
    [InlineData("Content-Type", "*/*")]
    [InlineData("Content-Type", "application/*")]
    [InlineData("Proxy-Connection", "keep-alive")]
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

    [Theory]
    [InlineData("rule", "rule\n")]
    [InlineData("service", "test\n")]
    [InlineData("method", "GET\n")]
    [InlineData("method", "PO\u017FT")]
    public async Task InvalidIdentifierOrMethodCannotActivate(string field, string value)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse("""
            {"schemaVersion":1,"destinations":{},"services":{"test":{"endpoints":[
              {"method":"GET","path":"/check","rules":[
                {"id":"rule","priority":1,"conditions":{"source":"header","key":"X-Scenario","operator":"exists"},
                 "action":{"mode":"mock","response":{"statusCode":201}}}
              ]}
            ]}}}
            """)!;
        var services = document["services"]!.AsObject();
        var service = services["test"]!;
        var endpoint = service["endpoints"]![0]!;
        if (field == "service")
        {
            services.Remove("test");
            services.Add(value, service);
        }
        else if (field == "method")
            endpoint["method"] = value;
        else
            endpoint["rules"]![0]!["id"] = value;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var fixture = await Fixture.StartAsync(document.ToJsonString());
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
    [InlineData("cookie", "Flag", "any", "case-distinct", 201)]
    [InlineData("cookie", "flag", "any", "case-distinct", 404)]
    [InlineData("cookie", "Flag", "any", "case-distinct-reversed", 201)]
    [InlineData("cookie", "flag", "any", "case-distinct-reversed", 404)]
    [InlineData("cookie", "flag", "any", "cookie-repeated", 201)]
    [InlineData("cookie", "flag", "all", "cookie-repeated", 404)]
    [InlineData("cookie", "flag", "none", "cookie-repeated", 404)]
    [InlineData("cookie", "flag", "any", "cookie-encoded", 201)]
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
        request.Headers.TryAddWithoutValidation("x-flag", input == "mixed" ? MixedFlags : EqualFlags);
        request.Headers.TryAddWithoutValidation("Cookie", input switch
        {
            "case-distinct" => "flag=no; Flag=yes",
            "case-distinct-reversed" => "Flag=yes; flag=no",
            "cookie-repeated" => "flag=yes; flag=no",
            "cookie-encoded" => "flag=%79es",
            _ => "flag=yes"
        });
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

    [Theory]
    [InlineData("exists", "{}", false)]
    [InlineData("exists", "{\"value\":null}", true)]
    [InlineData("exists", "{\"value\":false}", true)]
    [InlineData("notExists", "{}", true)]
    [InlineData("notExists", "{\"value\":null}", false)]
    [InlineData("notExists", "{\"value\":false}", false)]
    [InlineData("isNull", "{}", false)]
    [InlineData("isNull", "{\"value\":null}", true)]
    [InlineData("isNull", "{\"value\":false}", false)]
    public async Task PresenceOperatorsDistinguishMissingNullAndPresentValue(string operation, string body, bool matches)
    {
        // Each request evaluates exactly one leaf; no earlier all/any branch can hide it.
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"presence","priority":1,
               "conditions":{"source":"body","key":"$.value","operator":"OPERATOR"},
               "action":{"mode":"mock","response":{"statusCode":201,"text":"presence-match"}}}
            ],"default":{"mode":"mock","response":{"statusCode":404,"text":"presence-miss"}}}}}
            """.Replace("OPERATOR", operation, StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        var ct = TestContext.Current.CancellationToken;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, ct);
        Assert.Equal(matches ? HttpStatusCode.Created : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(matches ? "presence-match" : "presence-miss", await response.Content.ReadAsStringAsync(ct));
    }

    [Theory]
    [InlineData("any", "{\"values\":[1,2]}", true)]
    [InlineData("any", "{\"values\":[2,3]}", false)]
    [InlineData("all", "{\"values\":[1,1]}", true)]
    [InlineData("all", "{\"values\":[1,2]}", false)]
    [InlineData("none", "{\"values\":[2,3]}", true)]
    [InlineData("none", "{\"values\":[1,2]}", false)]
    [InlineData("any", "{}", false)]
    [InlineData("all", "{}", false)]
    [InlineData("none", "{}", false)]
    [InlineData("none", "{\"values\":[]}", false)]
    public async Task BodyResultQuantifiersRequireNonemptyResultsAndCorrectMatches(string quantifier, string body, bool matches)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"quantifier","priority":1,"conditions":{"source":"body","key":"$.values[*]","operator":"equals","value":1,"quantifier":"QUANTIFIER"},
               "action":{"mode":"mock","response":{"statusCode":201,"text":"quantifier-match"}}}
            ],"default":{"mode":"mock","response":{"statusCode":404,"text":"quantifier-miss"}}}}}
            """.Replace("QUANTIFIER", quantifier, StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        var ct = TestContext.Current.CancellationToken;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, ct);
        Assert.Equal(matches ? HttpStatusCode.Created : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(matches ? "quantifier-match" : "quantifier-miss", await response.Content.ReadAsStringAsync(ct));
    }

    [Theory]
    [InlineData("equals", "\"AbC\"", false, 404)]
    [InlineData("equals", "\"AbC\"", true, 201)]
    [InlineData("regex", "\"^AbC$\"", false, 404)]
    [InlineData("regex", "\"^AbC$\"", true, 201)]
    public async Task TextComparisonIsOrdinalUnlessExplicitlyIgnoringCase(string operation, string operand, bool ignoreCase, int expected)
    {
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"case","priority":1,"conditions":{"source":"body","key":"$.value","operator":"OPERATOR","value":OPERAND,"ignoreCase":IGNORE_CASE},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """.Replace("OPERATOR", operation, StringComparison.Ordinal)
            .Replace("OPERAND", operand, StringComparison.Ordinal)
            .Replace("IGNORE_CASE", ignoreCase ? "true" : "false", StringComparison.Ordinal);
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = new StringContent("""{"value":"abc"}""", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedNumericPrecisionIsAnInspectionErrorNotAFalseMatch()
    {
        const string rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"numeric","priority":1,"conditions":{"source":"body","key":"$.value","operator":"greaterThan","value":1},
               "action":{"mode":"mock","response":{"statusCode":201}}}
            ],"default":{"mode":"mock","response":{"statusCode":404}}}}}
            """;
        await using var fixture = await Fixture.StartAsync(rules);
        using var content = new StringContent("""{"value":1e100}""", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/mock-proxy/test/check", content, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExcessiveConditionDepthFailsActivation()
    {
        var condition = """{"source":"body","key":"$.value","operator":"exists"}""";
        for (var depth = 0; depth < 14; depth++)
            condition = "{\"not\":" + condition + "}";
        var rules = """
            {"schemaVersion":1,"destinations":{},"services":{"test":{"rules":[
              {"id":"too-deep","priority":1,"conditions":CONDITION,"action":{"mode":"mock","response":{"statusCode":201}}}
            ]}}}
            """.Replace("CONDITION", condition, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var fixture = await Fixture.StartAsync(rules);
        });
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
