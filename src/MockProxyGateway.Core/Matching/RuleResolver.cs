using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using MockProxyGateway.Configuration;

namespace MockProxyGateway.Matching;

internal sealed class RequestFailure(int statusCode, string category = "evaluation", string? firstRule = null, string? secondRule = null) : Exception
{
    internal int StatusCode { get; } = statusCode;
    internal string Category { get; } = category;
    internal string? FirstRule { get; } = firstRule;
    internal string? SecondRule { get; } = secondRule;
}

internal sealed class RuleResolver(HttpContext context, GatewayOptions options, DateTimeOffset now)
{
    private JsonNode? _body;
    private bool _bodyLoaded;
    private int _budget = 65536;
    private int _bodyNodes;
    private long _selectionBytes;
    private int _regexRemaining = 64;
    private readonly Dictionary<string, JsonElement[]> _bodySelections = new(StringComparer.Ordinal);

    internal async ValueTask<Selection> ResolveAsync(ServiceRules service, string path)
    {
        EndpointRules? selected = null;
        Dictionary<string, string> route = new(StringComparer.Ordinal);
        var ambiguous = false;
        foreach (var endpoint in service.Endpoints)
        {
            var exact = string.Equals(endpoint.Method, context.Request.Method, StringComparison.OrdinalIgnoreCase);
            if (!exact && endpoint.Method != "*")
                continue;
            var matched = endpoint.Path.Match(path);
            if (matched is null)
                continue;
            var comparison = selected is null ? 1 : exact.CompareTo(selected.Method != "*");
            if (comparison == 0)
                comparison = endpoint.Path.CompareSpecificity(selected!.Path);
            if (comparison > 0)
            {
                selected = endpoint;
                route = matched;
                ambiguous = false;
            }
            else if (comparison == 0)
                ambiguous = true;
        }
        if (ambiguous)
            throw new RequestFailure(500, "endpoint-conflict");
        if (selected is not null)
        {
            var match = await SelectAsync(selected.Rules, route, path).ConfigureAwait(false);
            if (match is not null)
                return match;
            if (selected.Default is not null)
                return new Selection(selected.Default, "endpoint-default");
        }
        return await SelectAsync(service.Rules, route, path).ConfigureAwait(false) ?? new Selection(service.Default, "service-default");
    }

    private async ValueTask<Selection?> SelectAsync(Rule[] rules, Dictionary<string, string> route, string path)
    {
        Rule? winner = null;
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.ExpiresAt <= now)
                continue;
            if (winner is not null && rule.Priority < winner.Priority)
                break;
            if (!await MatchesAsync(rule.Condition, route, path).ConfigureAwait(false))
                continue;
            if (winner is not null)
                throw new RequestFailure(500, "rule-conflict", winner.Id, rule.Id);
            winner = rule;
        }
        return winner is null ? null : new Selection(winner.Action, winner.Id);
    }

    private async ValueTask<bool> MatchesAsync(Condition condition, Dictionary<string, string> route, string path)
    {
        Charge(1);
        if (condition.Kind == "not")
            return !await MatchesAsync(condition.Children[0], route, path).ConfigureAwait(false);
        if (condition.Kind is "all" or "any")
        {
            foreach (var child in condition.Children)
            {
                var matched = await MatchesAsync(child, route, path).ConfigureAwait(false);
                if (condition.Kind == "all" && !matched)
                    return false;
                if (condition.Kind == "any" && matched)
                    return true;
            }
            return condition.Kind == "all";
        }
        var values = await ValuesAsync(condition, route, path).ConfigureAwait(false);
        Charge(values.Length);
        if (condition.Operator == "exists")
            return values.Length != 0;
        if (condition.Operator == "notExists")
            return values.Length == 0;
        if (values.Length == 0)
            return false;
        return condition.Quantifier switch
        {
            "all" => values.All(x => Compare(condition, x)),
            "none" => !values.Any(x => Compare(condition, x)),
            _ => values.Any(x => Compare(condition, x))
        };
    }

    private async ValueTask<JsonElement[]> ValuesAsync(Condition condition, Dictionary<string, string> route, string path)
    {
        var request = context.Request;
        string[] strings;
        switch (condition.Source)
        {
            case "body":
                await LoadBodyAsync().ConfigureAwait(false);
                if (_bodySelections.TryGetValue(condition.Key!, out var cached))
                    return cached;
                Charge(_bodyNodes * condition.JsonPathWorkMultiplier);
                var matches = condition.JsonPath!.Evaluate(_body).Matches;
                var results = new List<JsonElement>();
                foreach (var match in matches)
                {
                    Charge(1);
                    var element = JsonSerializer.SerializeToElement(match.Value);
                    _selectionBytes += Encoding.UTF8.GetByteCount(element.GetRawText());
                    if (_selectionBytes > 2L * options.MaxBodySizeBytes)
                        throw new RequestFailure(400, "selection-budget");
                    results.Add(element);
                }
                var selected = results.ToArray();
                _bodySelections.Add(condition.Key!, selected);
                return selected;
            case "header":
                strings = request.Headers.TryGetValue(condition.Key!, out var headers) ? headers.Select(x => x ?? "").ToArray() : [];
                break;
            case "query":
                strings = request.Query.Where(x => string.Equals(x.Key, condition.Key, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(x => x.Value).Select(x => x ?? "").ToArray();
                break;
            case "cookie":
                // IRequestCookieCollection collapses names using OrdinalIgnoreCase.
                // Parse the wire header without modifying it or losing repeated/case-distinct names.
                strings = CookieHeaderValue.ParseList(request.Headers.Cookie.Select(x => x ?? "").ToArray())
                    .Where(x => string.Equals(x.Name.Value, condition.Key, StringComparison.Ordinal))
                    .Select(x => Uri.UnescapeDataString(x.Value.Value ?? "")).ToArray();
                break;
            case "route":
                strings = route.TryGetValue(condition.Key!, out var captured) ? [captured] : [];
                break;
            case "method":
                strings = [request.Method];
                break;
            case "path":
                strings = [path];
                break;
            case "contentType":
                strings = MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ? [type.MediaType.Value!] : [];
                break;
            default:
                throw new RequestFailure(500);
        }
        return strings.Select(x => JsonSerializer.SerializeToElement(x)).ToArray();
    }

    private async Task LoadBodyAsync()
    {
        if (_bodyLoaded)
            return;
        var request = context.Request;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !(string.Equals(type.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase) ||
                (type.MediaType.Value?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false)))
            throw new RequestFailure(415, "body-media-type");
        if (request.ContentLength > options.MaxBodySizeBytes)
            throw new RequestFailure(413, "body-size");
        request.EnableBuffering(bufferThreshold: 32_768, bufferLimit: (long)options.MaxBodySizeBytes + 1);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            while (true)
            {
                var remaining = options.MaxBodySizeBytes + 1 - (int)buffer.Length;
                if (remaining <= 0)
                    throw new RequestFailure(413, "body-size");
                var count = await request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(remaining, chunk.Length)), context.RequestAborted).ConfigureAwait(false);
                if (count == 0)
                    break;
                buffer.Write(chunk, 0, count);
                if (buffer.Length > options.MaxBodySizeBytes)
                    throw new RequestFailure(413, "body-size");
            }
            _body = JsonNode.Parse(buffer.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            // Materialize once to validate duplicate keys and bound node count before JSONPath evaluation.
            VisitBody(_body);
            _bodyLoaded = true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new RequestFailure(400, "body-json");
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private void VisitBody(JsonNode? node)
    {
        if (++_bodyNodes > 16384)
            throw new RequestFailure(400, "body-complexity");
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
                VisitBody(property.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
                VisitBody(value);
        }
    }

    private void Charge(int count)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        _budget -= count;
        if (_budget < 0)
            throw new RequestFailure(400, "evaluation-budget");
    }

    private bool Compare(Condition condition, JsonElement actual)
    {
        var operand = condition.Value;
        // Bound membership fan-out and text/structural comparison work, not only leaf count.
        var length = actual.ValueKind == JsonValueKind.String ? actual.GetString()!.Length :
            actual.ValueKind is JsonValueKind.Array or JsonValueKind.Object ? actual.GetRawText().Length : 1;
        var cost = 1L + (length / 64L);
        if (condition.Operator is "in" or "notIn")
            cost *= operand.GetArrayLength();
        cost += condition.OperandCost;
        if (cost > int.MaxValue)
            throw new RequestFailure(400, "evaluation-budget");
        Charge((int)cost);
        var comparison = condition.IgnoreCase || condition.Source is "method" or "contentType"
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool Equal(JsonElement value) => actual.ValueKind == value.ValueKind &&
            (actual.ValueKind == JsonValueKind.String
                ? string.Equals(actual.GetString(), value.GetString(), comparison)
                : JsonElement.DeepEquals(actual, value));
        switch (condition.Operator)
        {
            case "isNull": return actual.ValueKind == JsonValueKind.Null;
            case "equals": return Equal(operand);
            case "notEquals": return !Equal(operand);
            case "jsonEquals": return JsonElement.DeepEquals(actual, operand);
            case "in": return operand.EnumerateArray().Any(Equal);
            case "notIn": return !operand.EnumerateArray().Any(Equal);
            case "contains": return actual.ValueKind == JsonValueKind.String && actual.GetString()!.Contains(operand.GetString()!, comparison);
            case "startsWith": return actual.ValueKind == JsonValueKind.String && actual.GetString()!.StartsWith(operand.GetString()!, comparison);
            case "endsWith": return actual.ValueKind == JsonValueKind.String && actual.GetString()!.EndsWith(operand.GetString()!, comparison);
            case "regex":
                if (actual.ValueKind != JsonValueKind.String)
                    return false;
                if (--_regexRemaining < 0)
                    throw new RequestFailure(400, "regex-budget");
                return MatchRegex(condition.Regex!, actual.GetString()!);
            default:
                if (actual.ValueKind != JsonValueKind.Number)
                    return false;
                decimal number;
                try { number = RuleCompiler.Number(actual); }
                catch (InvalidOperationException) { throw new RequestFailure(400); }
                return condition.Operator switch
                {
                    "greaterThan" => number > RuleCompiler.Number(operand),
                    "greaterThanOrEqual" => number >= RuleCompiler.Number(operand),
                    "lessThan" => number < RuleCompiler.Number(operand),
                    "lessThanOrEqual" => number <= RuleCompiler.Number(operand),
                    "between" => number >= RuleCompiler.Number(operand[0]) && number <= RuleCompiler.Number(operand[1]),
                    _ => throw new RequestFailure(500)
                };
        }
    }
    // Kept as one boundary so timeout classification can be exercised with the real regex engine.
    internal static bool MatchRegex(Regex regex, string value)
    {
        try { return regex.IsMatch(value); }
        catch (RegexMatchTimeoutException) { throw new RequestFailure(400, "regex-timeout"); }
    }
}
