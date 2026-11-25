using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Path;
using MockProxyGateway.Matching;

namespace MockProxyGateway.Configuration;

// Bounded I-Regexp subset. Never invoke or replace the library's global regex functions.
internal sealed class BoundedRegexFilterSelector(JsonPath argument, Regex regex, string expression) : ISelector
{
    private static readonly ISelector Children = JsonPath.Parse("$[*]").Segments[0].Selectors[0];

    public IEnumerable<Node> Evaluate(Node match, JsonNode? rootNode)
    {
        foreach (var candidate in Children.Evaluate(match, rootNode))
        {
            var value = argument.Evaluate(argument.Scope == PathScope.Global ? rootNode : candidate.Value)
                .Matches.FirstOrDefault()?.Value;
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) &&
                RuleResolver.MatchRegex(regex, text))
                yield return candidate;
        }
    }

    internal static Regex Compile(string pattern, bool fullMatch)
    {
        var mapped = new StringBuilder();
        var index = 0;
        var depth = 0;
        var repetitionBudget = 256;
        while (index < pattern.Length)
        {
            var current = pattern[index];
            if (current == '(')
            {
                if (++depth > 8)
                    throw Invalid();
                mapped.Append("(?:");
                index++;
                continue;
            }
            if (current == '|')
            {
                mapped.Append('|');
                index++;
                continue;
            }
            if (current == ')')
            {
                if (depth == 0)
                    throw Invalid();
                depth--;
                mapped.Append(')');
                index++;
            }
            else if (current == '.')
            {
                // Dot matches one Unicode scalar except CR and LF.
                mapped.Append(@"(?:[^\uD800-\uDFFF\r\n]|[\uD800-\uDBFF][\uDC00-\uDFFF])");
                index++;
            }
            else if (current == '[')
            {
                var start = ++index;
                while (index < pattern.Length && (char.IsAsciiLetterOrDigit(pattern[index]) || pattern[index] == '-'))
                    index++;
                if (index == start || index == pattern.Length || pattern[index] != ']')
                    throw Invalid();
                var members = pattern.AsSpan(start, index - start);
                var member = members[0] == '-' ? 1 : 0;
                while (member < members.Length)
                {
                    if (members[member] == '-' && member == members.Length - 1)
                        break;
                    if (!char.IsAsciiLetterOrDigit(members[member++]))
                        throw Invalid();
                    if (member + 1 < members.Length && members[member] == '-')
                    {
                        member++;
                        if (!char.IsAsciiLetterOrDigit(members[member++]) || members[member - 3] > members[member - 1])
                            throw Invalid();
                    }
                }
                mapped.Append('[').Append(members).Append(']');
                index++;
            }
            else if (current == '\\')
            {
                if (++index == pattern.Length)
                    throw Invalid();
                var escaped = pattern[index++];
                var literal = escaped switch
                {
                    'n' => "\n",
                    'r' => "\r",
                    't' => "\t",
                    '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}' => escaped.ToString(),
                    _ => throw Invalid()
                };
                mapped.Append(Regex.Escape(literal));
            }
            else
            {
                if (current is '*' or '+' or '?' or '{' or '}' or ']' ||
                    !Rune.TryGetRuneAt(pattern, index, out var rune))
                    throw Invalid();
                var escaped = Regex.Escape(rune.ToString());
                if (rune.Utf16SequenceLength == 2)
                    mapped.Append("(?:").Append(escaped).Append(')');
                else
                    mapped.Append(escaped);
                index += rune.Utf16SequenceLength;
            }
            if (index < pattern.Length && pattern[index] is '*' or '+' or '?')
                mapped.Append(pattern[index++]);
            else if (index < pattern.Length && pattern[index] == '{')
                ReadRangeQuantifier(pattern, ref index, mapped, ref repetitionBudget);
        }
        if (depth != 0)
            throw Invalid();

        var source = fullMatch ? @"\A(?:" + mapped + @")\z" : mapped.ToString();
        return new Regex(source, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(25));
    }

    private static void ReadRangeQuantifier(string pattern, ref int index, StringBuilder mapped, ref int repetitionBudget)
    {
        var start = index++;
        var minimum = ReadRepeatBound(pattern, ref index);
        var expansion = minimum;
        if (index < pattern.Length && pattern[index] == ',')
        {
            index++;
            if (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
            {
                expansion = ReadRepeatBound(pattern, ref index);
                if (expansion < minimum)
                    throw Invalid();
            }
            else
                expansion++;
        }
        if (index >= pattern.Length || pattern[index++] != '}')
            throw Invalid();

        // Conservatively bound the product of all explicit repetitions before
        // handing the translated pattern to the symbolic regex compiler.
        var factor = Math.Max(1, expansion);
        if (factor > repetitionBudget)
            throw Invalid();
        repetitionBudget /= factor;
        mapped.Append(pattern.AsSpan(start, index - start));
    }

    private static int ReadRepeatBound(string pattern, ref int index)
    {
        var start = index;
        var bound = 0;
        while (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
        {
            bound = bound * 10 + pattern[index++] - '0';
            if (bound > 64)
                throw Invalid();
        }
        if (index == start)
            throw Invalid();
        return bound;
    }

    private static InvalidOperationException Invalid() =>
        new("I-Regexp pattern exceeds the supported bounded grammar.");

    public void BuildString(StringBuilder builder) => builder.Append('?').Append(expression);
}
