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
                ReadCharacterClass(pattern, ref index, mapped);
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

    // Work in scalar intervals, not UTF-16 code units. Negation must never
    // match half a supplementary character, including in unanchored search.
    private static void ReadCharacterClass(string pattern, ref int index, StringBuilder mapped)
    {
        index++;
        var negate = index < pattern.Length && pattern[index] == '^';
        if (negate)
            index++;
        var ranges = new List<(int First, int Last)>();
        if (index < pattern.Length && pattern[index] == '-')
        {
            ranges.Add(('-', '-'));
            index++;
        }

        while (index < pattern.Length && pattern[index] != ']')
        {
            if (pattern[index] == '-' && index + 1 < pattern.Length && pattern[index + 1] == ']')
            {
                ranges.Add(('-', '-'));
                index++;
                break;
            }

            var first = ReadClassScalar(pattern, ref index);
            var last = first;
            if (index + 1 < pattern.Length && pattern[index] == '-' && pattern[index + 1] != ']')
            {
                index++;
                last = ReadClassScalar(pattern, ref index);
                if (last < first)
                    throw Invalid();
            }
            ranges.Add((first, last));
            if (ranges.Count > 32)
                throw Invalid();
        }
        if (ranges.Count == 0 || index >= pattern.Length || pattern[index++] != ']')
            throw Invalid();

        // Merge intervals before complementing and translating them. Split at
        // the surrogate hole even when the original range crosses that hole.
        ranges.Sort((left, right) => left.First.CompareTo(right.First));
        var merged = new List<(int First, int Last)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.First <= merged[^1].Last + 1)
                merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, range.Last));
            else
                merged.Add(range);
        }

        if (negate)
        {
            ranges.Clear();
            var cursor = 0;
            foreach (var range in merged)
            {
                if (cursor < range.First)
                    ranges.Add((cursor, range.First - 1));
                cursor = range.Last + 1;
            }
            if (cursor <= 0x10FFFF)
                ranges.Add((cursor, 0x10FFFF));
        }
        else
            ranges = merged;

        var alternatives = new List<string>();
        var bmp = new StringBuilder();
        foreach (var range in ranges)
        {
            AppendCodeUnitRange(bmp, range.First, Math.Min(range.Last, 0xD7FF));
            AppendCodeUnitRange(bmp, Math.Max(range.First, 0xE000), Math.Min(range.Last, 0xFFFF));
            if (range.Last >= 0x10000)
                AddSupplementaryRange(alternatives, Math.Max(range.First, 0x10000), range.Last);
        }
        if (bmp.Length > 0)
            alternatives.Add("[" + bmp + "]");
        if (alternatives.Count == 0)
        {
            // A negated class covering every scalar cannot match any input.
            mapped.Append(@"[\uD800-[\uD800]]");
            return;
        }
        mapped.Append("(?:").AppendJoin('|', alternatives).Append(')');
        if (mapped.Length > 4096)
            throw Invalid();
    }

    private static int ReadClassScalar(string pattern, ref int index)
    {
        if (index >= pattern.Length)
            throw Invalid();
        if (pattern[index] == '\\')
        {
            if (++index >= pattern.Length)
                throw Invalid();
            return pattern[index++] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}' => pattern[index - 1],
                _ => throw Invalid()
            };
        }
        if (pattern[index] is '-' or '[' or ']' || !Rune.TryGetRuneAt(pattern, index, out var rune))
            throw Invalid();
        index += rune.Utf16SequenceLength;
        return rune.Value;
    }

    private static void AddSupplementaryRange(List<string> alternatives, int first, int last)
    {
        var firstHigh = 0xD800 + ((first - 0x10000) >> 10);
        var lastHigh = 0xD800 + ((last - 0x10000) >> 10);
        var firstLow = 0xDC00 + ((first - 0x10000) & 0x3FF);
        var lastLow = 0xDC00 + ((last - 0x10000) & 0x3FF);
        if (firstHigh == lastHigh)
        {
            alternatives.Add(CodeUnitClass(firstHigh, firstHigh) + CodeUnitClass(firstLow, lastLow));
            return;
        }

        alternatives.Add(CodeUnitClass(firstHigh, firstHigh) + CodeUnitClass(firstLow, 0xDFFF));
        if (firstHigh + 1 < lastHigh)
            alternatives.Add(CodeUnitClass(firstHigh + 1, lastHigh - 1) + CodeUnitClass(0xDC00, 0xDFFF));
        alternatives.Add(CodeUnitClass(lastHigh, lastHigh) + CodeUnitClass(0xDC00, lastLow));
    }

    private static string CodeUnitClass(int first, int last)
    {
        var result = new StringBuilder("[");
        AppendCodeUnitRange(result, first, last);
        return result.Append(']').ToString();
    }

    private static void AppendCodeUnitRange(StringBuilder result, int first, int last)
    {
        if (first > last)
            return;
        result.Append(@"\u").Append(first.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        if (first != last)
            result.Append('-').Append(@"\u").Append(last.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
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
