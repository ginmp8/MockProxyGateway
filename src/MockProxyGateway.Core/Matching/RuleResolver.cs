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
    private int _bodyByteLength;
    private bool _filterNumbersValidated;
    private long _selectionBytes;
    private int _regexRemaining = 64;
    private readonly Dictionary<string, JsonElement[]> _bodySelections = new(StringComparer.Ordinal);

    internal async ValueTask<Selection> ResolveAsync(ServiceRules service, string path)
    {
        EndpointRules? selected = null;
        Dictionary<string, string> route = new(StringComparer.Ordinal);
        var ambiguous = false;
        string[]? parts = null;
        Action<long> charge = Charge;
        foreach (var endpoint in service.Endpoints)
        {
            // The configured wildcard is never an exact method, even when the incoming
            // extension method is the literal HTTP token "*". Otherwise matching groups
            // are compared against an inconsistent rank and file order can win.
            var exact = endpoint.Method != "*" &&
                string.Equals(endpoint.Method, context.Request.Method, StringComparison.OrdinalIgnoreCase);
            if (!exact && endpoint.Method != "*")
                continue;
            if (parts is null)
            {
                // Split the request path once, and reserve its materialization cost.
                Charge(1L + path.Length / 64L);
                parts = RoutePattern.Split(path);
                Charge(parts.Length);
            }
            var matched = endpoint.Path.Match(parts, charge);
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
                Charge(_bodyNodes * (long)condition.JsonPathWorkMultiplier);
                // Repeated descent, root queries and filters can revisit
                // overlapping subtrees. Bound the worst case before evaluation:
                // every candidate traverses the entire input.
                // At most 16,384 nodes and 16 MiB of input are admitted.
                // The bounded union, path length and segment multipliers keep
                // this conservative cubic product within Int64 range.
                if (condition.HasJsonPathCubicTraversal)
                    Charge((long)_bodyNodes * _bodyNodes * (_bodyNodes + _bodyByteLength / 64L + 1) * condition.JsonPathWorkMultiplier);
                else if (condition.HasJsonPathRepeatedTraversal)
                    Charge((long)_bodyNodes * (_bodyNodes + _bodyByteLength / 64L + 1) * condition.JsonPathWorkMultiplier);
                if (condition.HasJsonPathFilter)
                {
                    Charge((_bodyByteLength / 64L + 1) * condition.JsonPathWorkMultiplier);
                    if (!_filterNumbersValidated)
                    {
                        ValidateFilterNumbers(_body);
                        _filterNumbersValidated = true;
                    }
                }
                // Reserve repeated inner regex visits for root-driven nested filters.
                var regexCandidates = (long)_bodyNodes * condition.JsonPathRegexCandidateMultiplier *
                    (condition.HasJsonPathRootDrivenNestedFilter ? _bodyNodes : 1);
                if (regexCandidates > _regexRemaining)
                    throw new RequestFailure(400, "regex-budget");
                _regexRemaining -= (int)regexCandidates;
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
                // Charge the entire inspected query, including unrelated keys.
                // A missing selector must not make repeated full scans free.
                Charge(1L + (request.QueryString.Value?.Length ?? 0) / 64L);
                var query = request.Query;
                Charge(query.Count);
                strings = query.Where(x => string.Equals(x.Key, condition.Key, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(x => x.Value).Select(x => x ?? "").ToArray();
                break;
            case "cookie":
                // IRequestCookieCollection collapses names using OrdinalIgnoreCase.
                // Parse the wire header without modifying it or losing repeated/case-distinct names.
                // The permissive parser silently skips invalid values. A required
                // inspection failure must not become a no-match and select proxy.
                try
                {
                    foreach (var header in request.Headers.Cookie)
                        Charge(1L + (header?.Length ?? 0) / 64L);
                    var cookies = CookieHeaderValue.ParseStrictList(request.Headers.Cookie.Select(x => x ?? "").ToArray());
                    Charge(cookies.Count);
                    strings = cookies.Where(x => string.Equals(x.Name.Value, condition.Key, StringComparison.Ordinal))
                        .Select(x => Uri.UnescapeDataString(x.Value.Value ?? "")).ToArray();
                }
                catch (FormatException)
                {
                    throw new RequestFailure(400, "cookie-syntax");
                }
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
                if (request.ContentType is null)
                    strings = [];
                else
                {
                    // Matching ignores parameters, but parsing them still costs work.
                    // Charge every inspected header before parsing, even on a no-match.
                    Charge(1L + request.ContentType.Length / 64L);
                    if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type))
                        throw new RequestFailure(400, "content-type-syntax");
                    Charge(type.Parameters.Count);
                    strings = [type.MediaType.Value!];
                }
                break;
            default:
                throw new RequestFailure(500);
        }
        // Even presence checks materialize string values. Reserve their work
        // before serialization, rather than charging only successful comparisons.
        foreach (var value in strings)
            Charge(1L + value.Length / 64L);
        return strings.Select(x => JsonSerializer.SerializeToElement(x)).ToArray();
    }

    private async Task LoadBodyAsync()
    {
        if (_bodyLoaded)
            return;
        var request = context.Request;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) ||
            !(string.Equals(type.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase) ||
                options.AllowedJsonMediaTypes.Contains(type.MediaType.Value!, StringComparer.OrdinalIgnoreCase)))
            throw new RequestFailure(415, "body-media-type");
        if (request.ContentLength > options.MaxBodySizeBytes)
            throw new RequestFailure(413, "body-size");
        request.EnableBuffering(bufferThreshold: 32_768, bufferLimit: (long)options.MaxBodySizeBytes + 1);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            // A preceding host middleware may already have enabled buffering and
            // read a prefix. Inspect exactly the bytes that will be rewound for
            // forwarding, never a suffix that could hide malformed JSON or a rule.
            request.Body.Position = 0;
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
            // Buffering cannot recover bytes already consumed from a non-seekable
            // host body. When framing supplies a length, reject incomplete (or
            // inconsistent) representations before JSON parsing or action selection.
            if (request.ContentLength is long declaredLength && buffer.Length != declaredLength)
                throw new RequestFailure(400, "body-transport");
            _bodyByteLength = (int)buffer.Length;
            _body = JsonNode.Parse(buffer.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            // Materialize once to validate duplicate keys, Unicode strings and
            // node count before JSONPath evaluation (even for unselected fields).
            try
            {
                VisitBody(_body);
            }
            catch (InvalidOperationException)
            {
                // System.Text.Json decodes string values lazily and can throw
                // here for malformed Unicode rather than at JsonNode.Parse.
                throw new RequestFailure(400, "body-json");
            }
            _bodyLoaded = true;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode is >= 400 and < 500)
        {
            // A stricter host limit or invalid HTTP framing can fail on the first
            // body read. Preserve the sanitized client status rather than turning
            // this IOException into a generic gateway action error (500).
            throw new RequestFailure(ex.StatusCode, ex.StatusCode == StatusCodes.Status413PayloadTooLarge
                ? "body-size" : "body-transport");
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
        context.RequestAborted.ThrowIfCancellationRequested();
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
        else if (node is JsonValue scalar && scalar.TryGetValue<JsonElement>(out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            // Force decoding so invalid UTF-8 and unpaired surrogate escapes
            // cannot be treated as a non-match or an unexpected action error.
            _ = element.GetString();
        }
    }

    private void ValidateFilterNumbers(JsonNode? node)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        // The pinned JsonPath.Net comparison operators read decimals from JsonElement.
        // Reject unrepresentable or rounded values before any predicate is evaluated.
        if (node is JsonValue value && value.TryGetValue<JsonElement>(out var element) &&
            element.ValueKind == JsonValueKind.Number)
        {
            try { _ = RuleCompiler.Number(element); }
            catch (InvalidOperationException) { throw new RequestFailure(400, "body-number"); }
        }
        else if (node is JsonObject obj)
        {
            foreach (var property in obj)
                ValidateFilterNumbers(property.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                ValidateFilterNumbers(item);
        }
    }

    private void Charge(long count)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        if (count > _budget)
            throw new RequestFailure(400, "evaluation-budget");
        _budget -= (int)count;
    }

    private bool Compare(Condition condition, JsonElement actual)
    {
        var operand = condition.Value;
        // Bound membership fan-out and text, numeric-token and structural comparison work, not only leaf count.
        var length = actual.ValueKind == JsonValueKind.String ? actual.GetString()!.Length :
            actual.ValueKind is JsonValueKind.Number or JsonValueKind.Array or JsonValueKind.Object ? actual.GetRawText().Length : 1;
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
