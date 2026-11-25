using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Path;

namespace MockProxyGateway.Configuration;

internal sealed record RuleSnapshot(long Version, IReadOnlyDictionary<string, Destination> Destinations, IReadOnlyDictionary<string, ServiceRules> Services);
internal sealed record Destination(Uri BaseUri, string[] RemoveQuery);
internal sealed record ServiceRules(bool Enabled, RuleAction? Default, Rule[] Rules, EndpointRules[] Endpoints);
internal sealed record EndpointRules(string Method, RoutePattern Path, RuleAction? Default, Rule[] Rules);
internal sealed record Rule(string Id, bool Enabled, int Priority, DateTimeOffset? ExpiresAt, Condition Condition, RuleAction Action);
internal sealed record RuleAction(string Mode, string? Destination, string? PathRewrite, MockResponse? Response);
internal sealed record MockResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[]? Body, string? ContentType, int DelayMs);
internal sealed record Condition(string Kind, Condition[] Children, string? Source = null, string? Key = null, string? Operator = null,
    JsonElement Value = default, string Quantifier = "any", bool IgnoreCase = false, Regex? Regex = null, JsonPath? JsonPath = null,
    int OperandCost = 0);
internal sealed record Selection(RuleAction? Action, string RuleId);
