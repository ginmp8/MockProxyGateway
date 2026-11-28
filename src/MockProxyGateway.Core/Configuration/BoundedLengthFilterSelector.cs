using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;

namespace MockProxyGateway.Configuration;

// A filter-local adapter over the library's normal wildcard selector. It
// preserves the exact original candidate nodes and their locations, including
// duplicates from selector unions, without modifying any host function registry.
internal sealed class BoundedLengthFilterSelector(JsonPath? argument, int? literalLength, JsonPath? expectedQuery, JsonElement expectedLiteral, string comparison, string expression,
    (JsonPath? Query, int? Literal)? expectedLength = null, bool expectedCount = false) : ISelector
{
    private static readonly ISelector Children = JsonPath.Parse("$[*]").Segments[0].Selectors[0];

    public IEnumerable<Node> Evaluate(Node match, JsonNode? rootNode)
    {
        foreach (var candidate in Children.Evaluate(match, rootNode))
        {
            if (MatchesCandidate(rootNode, candidate.Value))
                yield return candidate;
        }
    }

    internal bool MatchesCandidate(JsonNode? rootNode, JsonNode? candidate)
    {
        var length = GetLength(argument, literalLength, rootNode, candidate);
        if (expectedLength is { } other)
        {
            var right = GetLength(other.Query, other.Literal, rootNode, candidate);
            if (length is null || right is null)
                return length == right ? comparison is "==" or "<=" or ">=" : comparison == "!=";
            return CompareNumbers(length.Value, right.Value);
        }

        var expected = expectedLiteral;
        if (expectedQuery is not null)
        {
            var matches = expectedQuery.Evaluate(expectedQuery.Scope == PathScope.Global ? rootNode : candidate).Matches;
            // count() always returns a number, even for an empty nodelist;
            // value() returns Nothing unless exactly one node is selected.
            if (expectedCount)
                return length is { } countValue ? CompareNumbers(countValue, matches.Count) : comparison == "!=";
            if (matches.Count != 1)
                return length is null ? comparison is "==" or "<=" or ">=" : comparison == "!=";
            // A selected null or container is a value, not Nothing. No need
            // to serialize containers that cannot equal a length number.
            if (matches[0].Value is not JsonValue scalar)
                return comparison == "!=";
            expected = JsonSerializer.SerializeToElement(scalar);
        }

        if (length is not { } count || expected.ValueKind != JsonValueKind.Number)
            return comparison == "!=";
        return CompareNumbers(count, RuleCompiler.Number(expected));
    }

    internal static int? GetLength(JsonPath? query, int? literal, JsonNode? rootNode, JsonNode? candidate)
    {
        if (query is null)
            return literal;
        var matches = query.Evaluate(query.Scope == PathScope.Global ? rootNode : candidate).Matches;
        if (matches.Count != 1)
            return null;
        return matches[0].Value switch
        {
            JsonObject obj => obj.Count,
            JsonArray arr => arr.Count,
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => ScalarCount(text),
            _ => null
        };
    }

    private bool CompareNumbers(decimal count, decimal number) => comparison switch
    {
        "==" => count == number,
        "!=" => count != number,
        "<" => count < number,
        ">" => count > number,
        "<=" => count <= number,
        ">=" => count >= number,
        _ => throw new InvalidOperationException("Invalid length comparison.")
    };

    internal static int ScalarCount(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
            count++;
        return count;
    }

    public void BuildString(StringBuilder builder) => builder.Append('?').Append(expression);
}
