using System.Text;
using System.Text.Json.Nodes;
using Json.Path;

namespace MockProxyGateway.Configuration;

// A filter-local adapter over the library's normal wildcard selector. It
// preserves the exact original candidate nodes and their locations, including
// duplicates from selector unions, without modifying any host function registry.
internal sealed class BoundedLengthFilterSelector(JsonPath? argument, int? literalLength, decimal expected, string comparison, string expression) : ISelector
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
        if (argument is null)
            return Matches(literalLength);
        var value = argument.Evaluate(argument.Scope == PathScope.Global ? rootNode : candidate)
            .Matches.FirstOrDefault()?.Value;
        int? length = value switch
        {
            JsonObject obj => obj.Count,
            JsonArray arr => arr.Count,
            JsonValue scalar when scalar.TryGetValue<string>(out var text) => ScalarCount(text),
            _ => null
        };
        return Matches(length);
    }

    private bool Matches(int? length)
    {
        // Nothing is unequal to a numeric literal and unordered with respect to it.
        if (length is not { } count)
            return comparison == "!=";
        return comparison switch
        {
            "==" => count == expected,
            "!=" => count != expected,
            "<" => count < expected,
            ">" => count > expected,
            "<=" => count <= expected,
            ">=" => count >= expected,
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
