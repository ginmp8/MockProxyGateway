using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Path;
using MockProxyGateway.Matching;

namespace MockProxyGateway.Configuration;

// Bounded I-Regexp subset. Never invoke or replace the library's global regex functions.
internal sealed class BoundedRegexFilterSelector(JsonPath? argument, bool? literalResult, BoundedRegexFilterSelector.CompiledPattern regex, string expression) : ISelector
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
        if (literalResult is bool constant)
            return constant;
        var query = argument ?? throw Invalid();
        var value = query.Evaluate(query.Scope == PathScope.Global ? rootNode : candidate)
            .Matches.FirstOrDefault()?.Value;
        return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && regex.IsMatch(text);
    }

    internal static CompiledPattern Compile(string pattern, bool fullMatch)
    {
        // Retain the direct translation for patterns without property escapes.
        var alphabet = pattern.Contains(@"\p", StringComparison.Ordinal) ||
            pattern.Contains(@"\P", StringComparison.Ordinal) ? new ScalarAlphabet() : null;
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
                if (alphabet is null)
                    mapped.Append(@"(?:[^\uD800-\uDFFF\r\n]|[\uD800-\uDBFF][\uDC00-\uDFFF])");
                else
                    mapped.Append(alphabet.Add([(0, 9), (11, 12), (14, 0x10FFFF)], 0, false));
                index++;
            }
            else if (current == '[')
            {
                ReadCharacterClass(pattern, ref index, mapped, alphabet);
            }
            else if (current == '\\')
            {
                if (++index == pattern.Length)
                    throw Invalid();
                var escaped = pattern[index++];
                if (escaped is 'p' or 'P')
                {
                    var categories = ReadCategory(pattern, ref index, escaped == 'P');
                    mapped.Append(alphabet!.Add([], categories, false));
                }
                else
                {
                    var literal = escaped switch
                    {
                        'n' => "\n",
                        'r' => "\r",
                        't' => "\t",
                        '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}' => escaped.ToString(),
                        _ => throw Invalid()
                    };
                    if (alphabet is null)
                        mapped.Append(Regex.Escape(literal));
                    else
                        mapped.Append(alphabet.Add([(literal[0], literal[0])], 0, false));
                }
            }
            else
            {
                if (current is '*' or '+' or '?' or '{' or '}' or ']' ||
                    !Rune.TryGetRuneAt(pattern, index, out var rune))
                    throw Invalid();
                if (alphabet is not null)
                    mapped.Append(alphabet.Add([(rune.Value, rune.Value)], 0, false));
                else
                {
                    var escaped = Regex.Escape(rune.ToString());
                    if (rune.Utf16SequenceLength == 2)
                        mapped.Append("(?:").Append(escaped).Append(')');
                    else
                        mapped.Append(escaped);
                }
                index += rune.Utf16SequenceLength;
            }
            if (index < pattern.Length && pattern[index] is '*' or '+' or '?')
                mapped.Append(pattern[index++]);
            else if (index < pattern.Length && pattern[index] == '{')
                ReadRangeQuantifier(pattern, ref index, mapped, ref repetitionBudget);
        }
        if (depth != 0)
            throw Invalid();

        var translated = alphabet is null ? mapped.ToString() : alphabet.Compile(mapped);
        var source = fullMatch ? @"\A(?:" + translated + @")\z" : translated;
        return new CompiledPattern(new Regex(source, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(25)), alphabet);
    }

    // Work in scalar intervals, not UTF-16 code units. Negation must never
    // match half a supplementary character, including in unanchored search.
    private static void ReadCharacterClass(string pattern, ref int index, StringBuilder mapped, ScalarAlphabet? alphabet)
    {
        index++;
        var negate = index < pattern.Length && pattern[index] == '^';
        if (negate)
            index++;
        var ranges = new List<(int First, int Last)>();
        uint categories = 0;
        var propertyCount = 0;
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

            if (index + 1 < pattern.Length && pattern[index] == '\\' && pattern[index + 1] is 'p' or 'P')
            {
                index += 2;
                categories |= ReadCategory(pattern, ref index, pattern[index - 1] == 'P');
                if (++propertyCount + ranges.Count > 32)
                    throw Invalid();
                continue;
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
            if (ranges.Count + propertyCount > 32)
                throw Invalid();
        }
        if (ranges.Count + propertyCount == 0 || index >= pattern.Length || pattern[index++] != ']')
            throw Invalid();
        if (alphabet is not null)
        {
            mapped.Append(alphabet.Add(ranges, categories, negate));
            return;
        }

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

    internal sealed class CompiledPattern(Regex regex, ScalarAlphabet? alphabet)
    {
        internal bool IsMatch(string text) =>
            RuleResolver.MatchRegex(regex, alphabet is null ? text : alphabet.Encode(text));
    }

    // Partition scalar intervals and Unicode general categories by atom
    // membership. Equivalent scalars share a private symbol. Quantifiers,
    // negation and search operate on whole scalars, never surrogate halves.
    // No Unicode range explosion or global function mutation is needed.
    internal sealed class ScalarAlphabet
    {
        private const int CategoryCount = 30;
        private const int FirstSymbol = 0x100;
        private const int FirstToken = 0xE000;
        private readonly List<Atom> _atoms = [];
        private int[] _boundaries = [];
        private char[] _table = [];

        private sealed record Atom(List<(int First, int Last)> Ranges, uint Categories, bool Negated)
        {
            internal bool Includes(int scalar, int category)
            {
                var included = (Categories & (1u << category)) != 0;
                foreach (var range in Ranges)
                    included |= scalar >= range.First && scalar <= range.Last;
                return included != Negated;
            }
        }

        internal char Add(List<(int First, int Last)> ranges, uint categories, bool negate)
        {
            if (_atoms.Count >= 64)
                throw Invalid();
            var token = (char)(FirstToken + _atoms.Count);
            _atoms.Add(new Atom(ranges, categories, negate));
            return token;
        }

        internal string Compile(StringBuilder tokens)
        {
            var boundaries = new SortedSet<int> { 0, 0x110000 };
            foreach (var atom in _atoms)
            {
                foreach (var range in atom.Ranges)
                {
                    boundaries.Add(range.First);
                    boundaries.Add(range.Last + 1);
                }
            }
            if (boundaries.Count > 256 ||
                (long)boundaries.Count * CategoryCount * _atoms.Sum(atom => atom.Ranges.Count + 1) > 1_000_000)
                throw Invalid();
            _boundaries = [.. boundaries];
            _table = new char[(_boundaries.Length - 1) * CategoryCount];
            var signatures = new List<ulong>();
            var symbols = new Dictionary<ulong, char>();
            for (var interval = 0; interval < _boundaries.Length - 1; interval++)
            {
                for (var category = 0; category < CategoryCount; category++)
                {
                    ulong signature = 0;
                    for (var atom = 0; atom < _atoms.Count; atom++)
                    {
                        if (_atoms[atom].Includes(_boundaries[interval], category))
                            signature |= 1UL << atom;
                    }
                    if (!symbols.TryGetValue(signature, out var symbol))
                    {
                        if (signatures.Count >= 512)
                            throw Invalid();
                        symbol = (char)(FirstSymbol + signatures.Count);
                        symbols.Add(signature, symbol);
                        signatures.Add(signature);
                    }
                    _table[interval * CategoryCount + category] = symbol;
                }
            }

            var translated = new StringBuilder();
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i] - FirstToken;
                if (token < 0 || token >= _atoms.Count)
                {
                    translated.Append(tokens[i]);
                    continue;
                }
                var ranges = new StringBuilder();
                for (var start = 0; start < signatures.Count; start++)
                {
                    if ((signatures[start] & (1UL << token)) == 0)
                        continue;
                    var end = start;
                    while (end + 1 < signatures.Count && (signatures[end + 1] & (1UL << token)) != 0)
                        end++;
                    AppendCodeUnitRange(ranges, FirstSymbol + start, FirstSymbol + end);
                    start = end;
                }
                // U+0000 is outside the private alphabet.
                translated.Append('[').Append(ranges.Length == 0 ? @"\u0000" : ranges.ToString()).Append(']');
                if (translated.Length > 4096)
                    throw Invalid();
            }
            return translated.ToString();
        }

        internal string Encode(string text)
        {
            var encoded = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                var index = Array.BinarySearch(_boundaries, rune.Value);
                if (index < 0)
                    index = ~index - 1;
                encoded.Append(_table[index * CategoryCount + (int)Rune.GetUnicodeCategory(rune)]);
            }
            return encoded.ToString();
        }
    }

    private static uint ReadCategory(string pattern, ref int index, bool negate)
    {
        if (index >= pattern.Length || pattern[index++] != '{')
            throw Invalid();
        var start = index;
        while (index < pattern.Length && char.IsAsciiLetter(pattern[index]) && index - start < 2)
            index++;
        if (index == start || index >= pattern.Length || pattern[index] != '}')
            throw Invalid();
        var category = pattern[start..index++];
        // UnicodeCategory indices: letters 0-4, marks 5-7, numbers 8-10,
        // separators 11-13, others 14-17 and 29, punctuation 18-24,
        // symbols 25-28. RFC 9485 does not admit the surrogate category Cs.
        const uint all = (1u << 30) - 1;
        var mask = category switch
        {
            "L" => 0x1Fu,
            "M" => 0xE0u,
            "N" => 0x700u,
            "Z" => 0x3800u,
            "C" => 0x2003C000u,
            "P" => 0x1FC0000u,
            "S" => 0x1E000000u,
            "Lu" => Bit(UnicodeCategory.UppercaseLetter),
            "Ll" => Bit(UnicodeCategory.LowercaseLetter),
            "Lt" => Bit(UnicodeCategory.TitlecaseLetter),
            "Lm" => Bit(UnicodeCategory.ModifierLetter),
            "Lo" => Bit(UnicodeCategory.OtherLetter),
            "Mn" => Bit(UnicodeCategory.NonSpacingMark),
            "Mc" => Bit(UnicodeCategory.SpacingCombiningMark),
            "Me" => Bit(UnicodeCategory.EnclosingMark),
            "Nd" => Bit(UnicodeCategory.DecimalDigitNumber),
            "Nl" => Bit(UnicodeCategory.LetterNumber),
            "No" => Bit(UnicodeCategory.OtherNumber),
            "Zs" => Bit(UnicodeCategory.SpaceSeparator),
            "Zl" => Bit(UnicodeCategory.LineSeparator),
            "Zp" => Bit(UnicodeCategory.ParagraphSeparator),
            "Cc" => Bit(UnicodeCategory.Control),
            "Cf" => Bit(UnicodeCategory.Format),
            "Co" => Bit(UnicodeCategory.PrivateUse),
            "Cn" => Bit(UnicodeCategory.OtherNotAssigned),
            "Pc" => Bit(UnicodeCategory.ConnectorPunctuation),
            "Pd" => Bit(UnicodeCategory.DashPunctuation),
            "Ps" => Bit(UnicodeCategory.OpenPunctuation),
            "Pe" => Bit(UnicodeCategory.ClosePunctuation),
            "Pi" => Bit(UnicodeCategory.InitialQuotePunctuation),
            "Pf" => Bit(UnicodeCategory.FinalQuotePunctuation),
            "Po" => Bit(UnicodeCategory.OtherPunctuation),
            "Sm" => Bit(UnicodeCategory.MathSymbol),
            "Sc" => Bit(UnicodeCategory.CurrencySymbol),
            "Sk" => Bit(UnicodeCategory.ModifierSymbol),
            "So" => Bit(UnicodeCategory.OtherSymbol),
            _ => throw Invalid()
        };
        return negate ? all ^ mask : mask;
    }

    private static uint Bit(UnicodeCategory category) => 1u << (int)category;

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
