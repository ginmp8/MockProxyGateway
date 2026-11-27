using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Path;
using Microsoft.Net.Http.Headers;

namespace MockProxyGateway.Configuration;

internal sealed class RuleCompiler(GatewayOptions options, string contentRoot)
{
    private int _nodes;
    private int _rules;
    private long _mockBytes;
    private const int MaximumSnapshotBodyBytes = 16_777_216;
    private static readonly HashSet<string> ForbiddenResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Transfer-Encoding", "Content-Length", "Keep-Alive", "Upgrade", "Trailer",
        "Proxy-Authenticate", "Proxy-Authorization", "TE", "Host"
    };

    internal RuleSnapshot Compile(byte[] bytes, long version)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        ValidateJsonTree(root);
        Only(root, "schemaVersion", "destinations", "services");
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw Invalid("Unsupported schema version.");
        var destinations = new Dictionary<string, Destination>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("destinations").EnumerateObject())
        {
            Id(entry.Name);
            Only(entry.Value, "baseUrl", "removeQuery");
            if (!Uri.TryCreate(Text(entry.Value, "baseUrl"), UriKind.Absolute, out var uri) ||
                !ProxyDestinationPolicy.IsValid(uri, options))
                throw Invalid("Invalid or unapproved destination.");
            destinations.Add(entry.Name, new Destination(uri, Strings(entry.Value, "removeQuery").ToFrozenSet(StringComparer.OrdinalIgnoreCase)));
        }
        var services = new Dictionary<string, ServiceRules>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("services").EnumerateObject())
        {
            Id(entry.Name);
            var service = entry.Value;
            Only(service, "enabled", "default", "rules", "endpoints");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var shapes = new HashSet<string>(StringComparer.Ordinal);
            var endpoints = new List<EndpointRules>();
            foreach (var endpoint in Items(service, "endpoints"))
            {
                Only(endpoint, "method", "path", "default", "rules");
                var method = Text(endpoint, "method");
                if (!GatewayPaths.HttpToken().IsMatch(method))
                    throw Invalid("Invalid endpoint method.");
                method = method.ToUpperInvariant();
                var pattern = new RoutePattern(Text(endpoint, "path"));
                if (!shapes.Add(method + " " + pattern.Shape))
                    throw Invalid("Duplicate or ambiguous endpoint.");
                endpoints.Add(new EndpointRules(method, pattern, OptionalAction(endpoint, destinations), Rules(endpoint, ids, destinations)));
            }
            if (endpoints.Count > 1024)
                throw Invalid("Too many endpoints.");
            services.Add(entry.Name, new ServiceRules(Bool(service, "enabled", true), OptionalAction(service, destinations),
                Rules(service, ids, destinations), [.. endpoints]));
        }
        if (services.Count > 256 || destinations.Count > 256)
            throw Invalid("Too many services or destinations.");
        return new RuleSnapshot(version, destinations, services);
    }

    private Rule[] Rules(JsonElement parent, HashSet<string> ids, Dictionary<string, Destination> destinations)
    {
        var result = new List<Rule>();
        foreach (var item in Items(parent, "rules"))
        {
            if (++_rules > 4096)
                throw Invalid("Too many rules.");
            Only(item, "id", "enabled", "priority", "expiresAt", "conditions", "action");
            var id = Text(item, "id");
            Id(id);
            if (!ids.Add(id))
                throw Invalid("Duplicate rule ID.");
            DateTimeOffset? expires = null;
            if (item.TryGetProperty("expiresAt", out var expiry))
            {
                var value = expiry.GetString();
                if (value is null || value.Length < 20 || value[10] != 'T' || value[16] != ':' ||
                    (!value.EndsWith('Z') && !value.EndsWith("+00:00", StringComparison.Ordinal)) ||
                    !expiry.TryGetDateTimeOffset(out var time) || time.Offset != TimeSpan.Zero)
                    throw Invalid("Expiry must be a UTC timestamp.");
                expires = time;
            }
            result.Add(new Rule(id, Bool(item, "enabled", true), item.GetProperty("priority").GetInt32(), expires,
                CompileCondition(item.GetProperty("conditions"), 0), Action(item.GetProperty("action"), destinations)));
        }
        return [.. result.OrderByDescending(x => x.Priority)];
    }

    private Condition CompileCondition(JsonElement item, int depth)
    {
        if (depth > 12 || ++_nodes > 16384)
            throw Invalid("Condition complexity limit exceeded.");
        foreach (var kind in new[] { "all", "any", "not" })
        {
            if (!item.TryGetProperty(kind, out var value))
                continue;
            Only(item, kind);
            var children = kind == "not" ? new[] { CompileCondition(value, depth + 1) } :
                value.EnumerateArray().Select(x => CompileCondition(x, depth + 1)).ToArray();
            if (children.Length == 0)
                throw Invalid("Empty condition group.");
            return new Condition(kind, children);
        }
        Only(item, "source", "key", "operator", "value", "quantifier", "ignoreCase");
        var source = Text(item, "source");
        var op = Text(item, "operator");
        var key = OptionalText(item, "key");
        var quantifier = OptionalText(item, "quantifier") ?? "any";
        var ignoreCase = Bool(item, "ignoreCase", false);
        if (source is not ("header" or "query" or "body" or "route" or "cookie" or "contentType" or "method" or "path") ||
            quantifier is not ("any" or "all" or "none") ||
            ((source is "contentType" or "method" or "path") ? key is not null : string.IsNullOrWhiteSpace(key)))
            throw Invalid("Invalid condition source, key or quantifier.");
        // Reject impossible selector names at activation, rather than silently
        // treating a malformed selector as a non-match that could select proxy.
        if ((source is "header" or "cookie") && !GatewayPaths.HttpToken().IsMatch(key!) ||
            source == "route" && !GatewayPaths.Identifier().IsMatch(key!))
            throw Invalid("Invalid condition selector name.");
        var hasValue = item.TryGetProperty("value", out var operand);
        if (op is "exists" or "notExists" or "isNull")
        {
            if (hasValue || (op == "isNull" && source != "body"))
                throw Invalid("Invalid existence or null condition.");
        }
        else if (!hasValue)
            throw Invalid("Condition operand required.");
        if (op is not ("equals" or "notEquals" or "exists" or "notExists" or "isNull" or "contains" or "startsWith" or "endsWith" or
            "in" or "notIn" or "regex" or "greaterThan" or "greaterThanOrEqual" or "lessThan" or "lessThanOrEqual" or "between" or "jsonEquals"))
            throw Invalid("Unsupported operator.");
        if (op is "contains" or "startsWith" or "endsWith" or "regex")
        {
            if (operand.ValueKind != JsonValueKind.String)
                throw Invalid("Text operator requires string operand.");
        }
        if (op is "in" or "notIn")
        {
            if (operand.ValueKind != JsonValueKind.Array || operand.GetArrayLength() is < 1 or > 128 ||
                operand.EnumerateArray().Any(x => x.ValueKind is JsonValueKind.Array or JsonValueKind.Object ||
                    (source != "body" && x.ValueKind != JsonValueKind.String)))
                throw Invalid("Membership requires nonempty scalar array.");
        }
        if (op is "greaterThan" or "greaterThanOrEqual" or "lessThan" or "lessThanOrEqual" or "between")
        {
            if (source != "body")
                throw Invalid("Numeric operators require body values.");
            if (op == "between")
            {
                if (operand.ValueKind != JsonValueKind.Array || operand.GetArrayLength() != 2 || Number(operand[0]) > Number(operand[1]))
                    throw Invalid("Invalid numeric range.");
            }
            else
                _ = Number(operand);
        }
        if (op == "jsonEquals" && source != "body")
            throw Invalid("Structural comparison requires a body selector.");
        if (op is "equals" or "notEquals")
        {
            if (operand.ValueKind is JsonValueKind.Array or JsonValueKind.Object ||
                (source != "body" && operand.ValueKind != JsonValueKind.String))
                throw Invalid("Invalid scalar operand.");
        }
        if (ignoreCase && (op is not ("equals" or "notEquals" or "contains" or "startsWith" or "endsWith" or "in" or "notIn" or "regex") ||
            (operand.ValueKind != JsonValueKind.String &&
             !(operand.ValueKind == JsonValueKind.Array && operand.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)))))
            throw Invalid("ignoreCase applies only to text operands.");
        Regex? regex = null;
        if (op == "regex")
        {
            var pattern = operand.GetString()!;
            if (pattern.Length > 256)
                throw Invalid("Regular expression too long.");
            regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking |
                (ignoreCase || source is "method" or "contentType" ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(25));
        }
        JsonPath? jsonPath = null;
        var pathMultiplier = 1;
        var hasFilter = false;
        var hasRepeatedTraversal = false;
        var hasCubicTraversal = false;
        var hasRootDrivenNestedFilter = false;
        var regexCandidateMultiplier = 0;
        if (source == "body")
        {
            // Parse RFC 9535 syntax with the pinned library, not a second approximate grammar.
            if (key!.Length > 256 || !JsonPath.TryParse(key, out jsonPath))
                throw Invalid("Invalid or overly complex JSONPath.");
            if (jsonPath.Segments.Length > 16)
                throw Invalid("JSONPath segment limit exceeded.");
            var recursiveCount = 0;
            var filterCount = 0;
            var recursiveSuffix = 0;
            foreach (var segment in jsonPath.Segments)
            {
                // Before the first descent all candidate nodes are at the same depth.
                // Their subtrees are disjoint except for duplicates introduced by unions.
                // Two descents may revisit overlapping subtrees. Charge the
                // whole-input traversal per candidate before evaluating them.
                if ((segment.IsRecursive && ++recursiveCount > 2) || segment.Selectors.Length == 0 ||
                    segment.Selectors.Any(x => x is not (NameSelector or IndexSelector or WildcardSelector or SliceSelector or FilterSelector)))
                    throw Invalid("Unsupported or overly complex JSONPath selector.");
                var filters = segment.Selectors.Count(x => x is FilterSelector);
                if (filters != 0)
                {
                    filterCount += filters;
                    if (filterCount > 2)
                        throw Invalid("At most two outer JSONPath filters are supported.");
                    hasFilter = true;
                }
                if (recursiveCount > (segment.IsRecursive ? 1 : 0))
                    recursiveSuffix++;
                if (segment.Selectors.Length > 4 / pathMultiplier)
                    throw Invalid("JSONPath union complexity limit exceeded.");
                pathMultiplier *= segment.Selectors.Length;
            }
            var recursive = recursiveCount != 0;
            hasRepeatedTraversal = recursiveCount == 2;
            if (hasFilter)
            {
                var normalized = BoundedJsonPathFilter.Normalize(key, out var predicateRepeatedTraversal,
                    out var predicateUnionProduct, out var hasNestedFilter, out var hasRepeatedPredicateDescent, out var usesPrivateValueQuery, out hasRootDrivenNestedFilter, out var localFilters);
                // Two outer descents can repeat filter candidates; two
                // predicate descents can repeat traversal per candidate.
                // Either requires a cubic whole-input bound. Combining both
                // would require a fourth-order bound, so reject that shape.
                if (recursiveCount == 2 && hasRepeatedPredicateDescent)
                    throw Invalid("Repeated outer and predicate JSONPath descents cannot be combined.");
                hasCubicTraversal = recursiveCount == 2 || hasRepeatedPredicateDescent || hasRootDrivenNestedFilter;
                // Descent may make filter candidate subtrees overlap. Charge worst-case
                // repeated traversal even for a relative predicate in that case.
                // Nested predicates remain non-recursive and union-free. Relative
                // enclosing queries use the quadratic bound; root-driven enclosing
                // queries use cubic work and quadratic regex precharge.
                if (hasNestedFilter && (predicateRepeatedTraversal || recursive || predicateUnionProduct != 1))
                    throw Invalid("Nested JSONPath filters require bounded non-recursive predicates without predicate unions.");
                // Retain the same quadratic charge for private value conversions.
                hasRepeatedTraversal = predicateRepeatedTraversal || recursive || hasNestedFilter || usesPrivateValueQuery;
                if (predicateUnionProduct > 4 / pathMultiplier)
                    throw Invalid("JSONPath predicate union complexity limit exceeded.");
                pathMultiplier *= predicateUnionProduct;
                var regexFilters = localFilters.Values.OfType<BoundedCompoundFilterSelector>().Sum(x => x.RegexCount);
                if (regexFilters != 0)
                {
                    if (recursive)
                        throw Invalid("JSONPath regex filters cannot be combined with outer descent.");
                    // Reserve candidate visits before invoking any regex.
                    regexCandidateMultiplier = regexFilters * pathMultiplier;
                }
                if (!JsonPath.TryParse(normalized, out jsonPath))
                    throw Invalid("Invalid bounded JSONPath filter.");
                // Replace only selectors on this newly compiled path. The
                // library's selector arrays are per path, never process-global.
                var filterIndex = 0;
                foreach (var segment in jsonPath.Segments)
                {
                    for (var i = 0; i < segment.Selectors.Length; i++)
                    {
                        if (segment.Selectors[i] is not FilterSelector)
                            continue;
                        if (localFilters.TryGetValue(filterIndex++, out var local))
                            segment.Selectors[i] = local;
                    }
                }
                if (filterIndex != filterCount)
                    throw Invalid("JSONPath filter count changed during compilation.");
                // Charge predicate operations per node and input byte. The resolver
                // separately charges repeated traversal when candidate subtrees may overlap.
                pathMultiplier *= key.Length + 1;
            }
            // Keep the union cap independent of the descent work multiplier.
            // Charge descent traversal and child selection, plus each following segment.
            if (recursive)
                pathMultiplier *= 2 + recursiveSuffix;
        }
        return new Condition("leaf", [], source, key, op, hasValue ? operand.Clone() : default,
            quantifier, ignoreCase, regex, jsonPath, hasValue ? 1 + operand.GetRawText().Length / 64 : 0, pathMultiplier, hasFilter, hasRepeatedTraversal, hasCubicTraversal, regexCandidateMultiplier, hasRootDrivenNestedFilter);
    }

    internal static decimal Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number) ||
            !JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number)))
            throw Invalid("Unsupported numeric value.");
        return number;
    }

    private RuleAction? OptionalAction(JsonElement parent, Dictionary<string, Destination> destinations) =>
        parent.TryGetProperty("default", out var value) ? Action(value, destinations) : null;

    private RuleAction Action(JsonElement item, Dictionary<string, Destination> destinations)
    {
        var mode = Text(item, "mode");
        if (mode == "proxy")
        {
            Only(item, "mode", "destination", "pathRewrite");
            var destination = Text(item, "destination");
            var rewrite = OptionalText(item, "pathRewrite");
            if (!destinations.ContainsKey(destination) || (rewrite is not null &&
                (!GatewayPaths.IsSafe(rewrite) || !GatewayPaths.IsEscapedTargetPart(rewrite, query: false))))
                throw Invalid("Invalid proxy destination or rewrite.");
            return new RuleAction(mode, destination, rewrite, null);
        }
        if (mode != "mock")
            throw Invalid("Invalid action mode.");
        Only(item, "mode", "response");
        var response = item.GetProperty("response");
        Only(response, "statusCode", "headers", "body", "text", "bodyFile", "delayMs");
        var status = response.GetProperty("statusCode").GetInt32();
        var delay = response.TryGetProperty("delayMs", out var delayValue) ? delayValue.GetInt32() : 0;
        if (status is < 200 or > 599 || delay is < 0 or > 60_000)
            throw Invalid("Invalid mock status or delay.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.TryGetProperty("headers", out var headerObject))
        {
            foreach (var header in headerObject.EnumerateObject())
            {
                var value = header.Value.GetString() ?? throw Invalid("Invalid header value.");
                if (!GatewayPaths.HttpToken().IsMatch(header.Name) || ForbiddenResponseHeaders.Contains(header.Name) ||
                    value.Any(c => c != '\t' && (c < ' ' || c > '~')) || value.Length > 8192 ||
                    (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase) &&
                        !MediaTypeHeaderValue.TryParse(value, out _)))
                    throw Invalid("Unsafe or invalid mock response header.");
                headers.Add(header.Name, value);
            }
        }
        var hasJson = response.TryGetProperty("body", out var json);
        var hasText = response.TryGetProperty("text", out var text);
        var hasFile = response.TryGetProperty("bodyFile", out var file);
        var bodyCount = (hasJson ? 1 : 0) + (hasText ? 1 : 0) + (hasFile ? 1 : 0);
        if (bodyCount > 1 || (bodyCount > 0 && status is 204 or 205 or 304))
            throw Invalid("Invalid mock body combination.");
        var body = hasJson ? Encoding.UTF8.GetBytes(json.GetRawText()) :
            hasText ? Encoding.UTF8.GetBytes(text.GetString() ?? throw Invalid("Invalid text body.")) :
            hasFile ? ReadFixture(file.GetString() ?? throw Invalid("Invalid fixture reference.")) : null;
        if (body is not null)
        {
            _mockBytes += body.Length;
            if (body.Length > 4_194_304 || _mockBytes > MaximumSnapshotBodyBytes)
                throw Invalid("Mock body exceeds its individual or aggregate size limit.");
        }
        return new RuleAction(mode, null, null, new MockResponse(status, headers, body,
            hasJson ? "application/json; charset=utf-8" : hasText ? "text/plain; charset=utf-8" :
            hasFile ? "application/octet-stream" : null, delay));

    }

    private byte[] ReadFixture(string relative)
    {
        if (string.IsNullOrWhiteSpace(options.FixtureRoot) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || relative.Split(['/', '\\']).Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')))
            throw Invalid("Invalid fixture location.");
        var root = Path.GetFullPath(options.FixtureRoot, contentRoot);
        var file = new FileInfo(Path.GetFullPath(relative, root));
        if (!file.Exists)
            throw new FileNotFoundException("Fixture file is not available.");
        if (file.Length > 4_194_304 || _mockBytes + file.Length > MaximumSnapshotBodyBytes ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw Invalid("Invalid fixture file.");
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw Invalid("Fixture paths must not contain links.");
        }
        // Only the host/operator may modify this trusted directory. Capture bytes during validation.
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4_194_304 || _mockBytes + stream.Length > MaximumSnapshotBodyBytes)
            throw Invalid("Fixture exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new IOException("Fixture changed during loading.");
        return bytes;
    }

    internal static void ValidateJsonTree(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw Invalid("Duplicate JSON property.");
                ValidateJsonTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray())
                ValidateJsonTree(value);
        else if (element.ValueKind == JsonValueKind.String)
        {
            // JsonDocument may defer Unicode decoding until a string is read.
            // Validate even nested operands and raw mock response bodies before
            // publishing the snapshot, without changing their original JSON.
            _ = element.GetString();
        }
    }

    private static JsonElement[] Items(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var value) ? value.EnumerateArray().ToArray() : [];
    private static string[] Strings(JsonElement parent, string key) =>
        Items(parent, key).Select(x => x.GetString() ?? throw Invalid("Invalid string array.")).ToArray();
    private static string Text(JsonElement parent, string key) => parent.GetProperty(key).GetString() ?? throw Invalid("Required string missing.");
    private static string? OptionalText(JsonElement parent, string key) => parent.TryGetProperty(key, out _) ? Text(parent, key) : null;
    private static bool Bool(JsonElement parent, string key, bool fallback) => parent.TryGetProperty(key, out var value) ? value.GetBoolean() : fallback;
    private static void Id(string value)
    {
        if (!GatewayPaths.Identifier().IsMatch(value))
            throw Invalid("Invalid identifier.");
    }
    private static void Only(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)))
            throw Invalid("Unknown or invalid configuration property.");
    }
    private static InvalidOperationException Invalid(string message) => new(message);
}
