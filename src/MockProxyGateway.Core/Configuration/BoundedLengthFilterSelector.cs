using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;

namespace MockProxyGateway.Configuration;

// A filter-local adapter over the library's normal wildcard selector. It
// preserves the exact original candidate nodes and their locations, including
// duplicates from selector unions, without modifying any host function registry.
internal sealed class BoundedLengthFilterSelector(JsonPath? argument, int? literalLength, JsonPath? expectedQuery, JsonElement expectedLiteral, string comparison, string expression) : ISelector
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
        var length = literalLength;
        if (argument is not null)
        {
            var value = argument.Evaluate(argument.Scope == PathScope.Global ? rootNode : candidate)
                .Matches.FirstOrDefault()?.Value;
            length = value switch
            {
                JsonObject obj => obj.Count,
                JsonArray arr => arr.Count,
                JsonValue scalar when scalar.TryGetValue<string>(out var text) => ScalarCount(text),
                _ => null
            };
        }

        var expected = expectedLiteral;
        if (expectedQuery is not null)
        {
            var matches = expectedQuery.Evaluate(expectedQuery.Scope == PathScope.Global ? rootNode : candidate).Matches;
            if (matches.Count == 0)
                return length is null ? comparison is "==" or "<=" or ">=" : comparison == "!=";
            // A selected null or container is a value, not Nothing. No need
            // to serialize containers that cannot equal a length number.
            if (matches[0].Value is not JsonValue scalar)
                return comparison == "!=";
            expected = JsonSerializer.SerializeToElement(scalar);
        }

        if (length is not { } count || expected.ValueKind != JsonValueKind.Number)
            return comparison == "!=";
        var number = RuleCompiler.Number(expected);
        return comparison switch
        {
            "==" => count == number,
            "!=" => count != number,
            "<" => count < number,
            ">" => count > number,
            "<=" => count <= number,
            ">=" => count >= number,
            _ => throw new InvalidOperationException("Invalid length comparison.")
        };
    }

    internal static int ScalarCount(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
            count++;
        return count;
    }

    public void BuildString(StringBuilder builder) => builder.Append('?').Append(expression);
}
