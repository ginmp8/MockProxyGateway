using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Path;

namespace MockProxyGateway.Configuration;

// JsonPath.Net owns parsing and evaluation. This smaller grammar is solely an
// admission policy for filter work that can be bounded before evaluation.
// It examines the original expression, not a potentially lossy AST rendering.
internal static class BoundedJsonPathFilter
{
    internal static string Normalize(string path, out bool usesRepeatedTraversal, out int maxUnionProduct, out bool hasNestedFilter, out bool hasRepeatedPredicateDescent, out Dictionary<int, ISelector> localFilters)
    {
        localFilters = [];
        var filterIndex = 0;
        usesRepeatedTraversal = false;
        maxUnionProduct = 1;
        hasNestedFilter = false;
        hasRepeatedPredicateDescent = false;
        const int filterDepth = 0;
        var foundFilter = false;
        var index = 0;
        var grouping = new List<(int Offset, char Character)>();
        while (index < path.Length)
        {
            if (path[index] is '\'' or '"')
            {
                ReadQuoted(path, ref index);
                continue;
            }
            if (path[index++] != '[')
                continue;

            // The library has already parsed this segment. Visit each selector
            // so a filter after a name/index/slice is checked as rigorously as
            // the first selector, and NOT normalization never skips a union arm.
            do
            {
                if (Take(path, ref index, "?"))
                {
                    if (ContainsLocalFunction(path, index))
                    {
                        var start = index;
                        var parser = new LocalExpressionParser(path, index, usesRepeatedTraversal, maxUnionProduct,
                            hasNestedFilter, hasRepeatedPredicateDescent);
                        var predicate = parser.ReadOr(0);
                        index = parser.Position;
                        usesRepeatedTraversal = parser.UsesRepeatedTraversal;
                        maxUnionProduct = parser.MaxUnionProduct;
                        hasNestedFilter = parser.HasNestedFilter;
                        hasRepeatedPredicateDescent = parser.HasRepeatedPredicateDescent;
                        localFilters.Add(filterIndex, new BoundedCompoundFilterSelector(predicate,
                            parser.RegexCount, path[start..index]));
                    }
                    else
                        ReadExpression(path, ref index, 0, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
                    filterIndex++;
                    foundFilter = true;
                }
                else
                    _ = ReadSelector(path, ref index);
            }
            while (Take(path, ref index, ","));
            Require(path, ref index, "]");
        }
        if (!foundFilter)
            throw Invalid();
        if (grouping.Count == 0)
            return path;
        var normalized = new StringBuilder(path);
        foreach (var (offset, character) in grouping.OrderByDescending(x => x.Offset))
            normalized.Insert(offset, character);
        return normalized.ToString();
    }

    // Detect private function calls anywhere in the current predicate, skipping
    // quoted strings. Nested private calls require explicit selector replacement.
    // Ordinary expressions retain the
    // original admission and normalization path.
    private static bool ContainsLocalFunction(string path, int index)
    {
        var brackets = 0;
        var parentheses = 0;
        while (index < path.Length)
        {
            var current = path[index];
            if (current is '\'' or '"')
            {
                ReadQuoted(path, ref index);
                continue;
            }
            if (current == '[')
                brackets++;
            else if (current == ']')
            {
                if (brackets == 0)
                    return false;
                brackets--;
            }
            else
            {
                if (current == '(')
                    parentheses++;
                else if (current == ')')
                    parentheses--;
                else if (current == ',' && brackets == 0 && parentheses == 0)
                    return false;
                else
                {
                    var cursor = index;
                    if ((Take(path, ref cursor, "length") || Take(path, ref cursor, "match") ||
                         Take(path, ref cursor, "search")) && Take(path, ref cursor, "("))
                        return true;
                }
            }
            index++;
        }
        return false;
    }

    // Use the pinned library's public leaf-expression evaluation with the
    // actual root and current candidate. Private predicates never mutate the
    // host function registry or duplicate candidate nodes.
    private sealed class LocalExpressionParser(string path, int start, bool repeatedTraversal,
        int unionProduct, bool nestedFilter, bool repeatedDescent)
    {
        private int _index = start;
        private bool _usesRepeatedTraversal = repeatedTraversal;
        private int _maxUnionProduct = unionProduct;
        private bool _hasNestedFilter = nestedFilter;
        private bool _hasRepeatedPredicateDescent = repeatedDescent;
        private int _termCount;
        internal int Position => _index;
        internal bool UsesRepeatedTraversal => _usesRepeatedTraversal;
        internal int MaxUnionProduct => _maxUnionProduct;
        internal bool HasNestedFilter => _hasNestedFilter;
        internal bool HasRepeatedPredicateDescent => _hasRepeatedPredicateDescent;
        internal int RegexCount { get; private set; }

        internal Func<JsonNode?, JsonNode?, bool> ReadOr(int depth)
        {
            var predicate = ReadAnd(depth);
            while (Take(path, ref _index, "||"))
            {
                var left = predicate;
                var right = ReadAnd(depth);
                predicate = (root, candidate) => left(root, candidate) || right(root, candidate);
            }
            return predicate;
        }

        private Func<JsonNode?, JsonNode?, bool> ReadAnd(int depth)
        {
            var predicate = ReadTerm(depth);
            while (Take(path, ref _index, "&&"))
            {
                var left = predicate;
                var right = ReadTerm(depth);
                predicate = (root, candidate) => left(root, candidate) && right(root, candidate);
            }
            return predicate;
        }

        private Func<JsonNode?, JsonNode?, bool> ReadTerm(int depth)
        {
            if (depth > 8)
                throw Invalid();
            if (Take(path, ref _index, "!"))
            {
                var operand = ReadTerm(depth + 1);
                return (root, candidate) => !operand(root, candidate);
            }
            if (Take(path, ref _index, "("))
            {
                var operand = ReadOr(depth + 1);
                Require(path, ref _index, ")");
                return operand;
            }
            if (++_termCount > 16)
                throw Invalid();
            if (TryReadLengthFilter(path, ref _index, out var local, ref _usesRepeatedTraversal))
                return ((BoundedLengthFilterSelector)local!).MatchesCandidate;
            if (TryReadRegexFilter(path, ref _index, out local, ref _usesRepeatedTraversal))
            {
                RegexCount++;
                return ((BoundedRegexFilterSelector)local!).MatchesCandidate;
            }

            SkipWhitespace(path, ref _index);
            var leafStart = _index;
            var grouping = new List<(int Offset, char Character)>();
            ISelector? nestedPrivate = null;
            void ReadNestedPrivate(ref int cursor)
            {
                if (nestedPrivate is not null)
                    throw Invalid();
                var nestedStart = cursor;
                var nested = new LocalExpressionParser(path, cursor, _usesRepeatedTraversal,
                    _maxUnionProduct, _hasNestedFilter, _hasRepeatedPredicateDescent);
                var nestedPredicate = nested.ReadOr(0);
                cursor = nested.Position;
                _usesRepeatedTraversal = nested.UsesRepeatedTraversal;
                _maxUnionProduct = nested.MaxUnionProduct;
                _hasNestedFilter = nested.HasNestedFilter;
                _hasRepeatedPredicateDescent = nested.HasRepeatedPredicateDescent;
                RegexCount += nested.RegexCount;
                nestedPrivate = new BoundedCompoundFilterSelector(nestedPredicate, nested.RegexCount,
                    path[nestedStart..cursor]);
            }

            var left = ReadOperand(path, ref _index, grouping, ref _usesRepeatedTraversal,
                ref _maxUnionProduct, ref _hasNestedFilter, ref _hasRepeatedPredicateDescent, 0, ReadNestedPrivate);
            var leftEnd = _index;
            var leftContainsPrivate = nestedPrivate is not null;
            string? comparison = null;
            foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
            {
                if (Take(path, ref _index, op))
                {
                    comparison = op;
                    break;
                }
            }
            var rightStart = _index;
            if (comparison is not null)
            {
                var right = ReadOperand(path, ref _index, grouping, ref _usesRepeatedTraversal,
                    ref _maxUnionProduct, ref _hasNestedFilter, ref _hasRepeatedPredicateDescent, 0, ReadNestedPrivate);
                if (!left.Comparable || !right.Comparable)
                    throw Invalid();
            }
            else if (!left.Query)
                throw Invalid();

            // Keep the existing unary NOT correction for ordinary nested
            // predicates; outer logical precedence is handled above.
            var expression = new StringBuilder(path[leafStart.._index]);
            foreach (var (offset, character) in grouping.OrderByDescending(x => x.Offset))
                expression.Insert(offset - leafStart, character);
            if (nestedPrivate is not null)
            {
                // The compiler rejects root references, recursion and predicate
                // unions whenever any nested filter is present. Evaluate the
                // isolated relative query on the current candidate.
                var queryText = comparison is null ? expression.ToString().Trim()
                    : leftContainsPrivate ? path[leafStart..leftEnd].Trim() : path[rightStart.._index].Trim();
                string? function = null;
                JsonElement expected = default;
                JsonPath? expectedQuery = null;
                if (comparison is not null)
                {
                    var open = queryText.IndexOf('(');
                    if (open < 0 || !queryText.EndsWith(')'))
                        throw Invalid();
                    function = queryText[..open].Trim();
                    if (function is not ("count" or "value"))
                        throw Invalid();
                    queryText = queryText[(open + 1)..^1].Trim();
                    var rightText = leftContainsPrivate ? path[rightStart.._index].Trim() : path[leafStart..leftEnd].Trim();
                    if (rightText.StartsWith('@'))
                    {
                        if (!JsonPath.TryParse("$" + rightText[1..], out expectedQuery) ||
                            !expectedQuery.IsSingular || expectedQuery.Segments.Length > 16)
                            throw Invalid();
                    }
                    else
                    {
                        expected = ReadComparisonLiteral(rightText);
                        if (function == "count" && expected.ValueKind != JsonValueKind.Number)
                            throw Invalid();
                    }
                }
                if (queryText[0] != '@' ||
                    !JsonPath.TryParse("$" + queryText[1..], out var query) ||
                    query.Segments.Length > 16)
                    throw Invalid();
                var replaced = 0;
                foreach (var segment in query.Segments)
                {
                    for (var i = 0; i < segment.Selectors.Length; i++)
                    {
                        if (segment.Selectors[i] is not FilterSelector)
                            continue;
                        segment.Selectors[i] = nestedPrivate;
                        replaced++;
                    }
                }
                if (replaced != 1)
                    throw Invalid();
                if (function == "count")
                {
                    return (_, candidate) =>
                    {
                        var actual = JsonSerializer.SerializeToElement(query.Evaluate(candidate).Matches.Count);
                        var operand = expectedQuery is null ? expected : SelectedValue(expectedQuery, candidate);
                        return leftContainsPrivate ? CompareValue(actual, operand, comparison!) : CompareValue(operand, actual, comparison!);
                    };
                }
                if (function == "value")
                {
                    return (_, candidate) =>
                    {
                        var actual = SelectedValue(query, candidate);
                        var operand = expectedQuery is null ? expected : SelectedValue(expectedQuery, candidate);
                        return leftContainsPrivate ? CompareValue(actual, operand, comparison!) : CompareValue(operand, actual, comparison!);
                    };
                }
                return (_, candidate) => query.Evaluate(candidate).Matches.Count != 0;
            }

            if (!JsonPath.TryParse("$[?" + expression.ToString().Trim() + "]", out var parsed) ||
                parsed.Segments[0].Selectors[0] is not FilterSelector filter)
                throw Invalid();
            return filter.Expression.Evaluate;
        }
    }

    private static JsonElement ReadComparisonLiteral(string text)
    {
        if (text.Length == 0)
            throw Invalid();
        if (text[0] is '\'' or '"')
        {
            if (!JsonPath.TryParse("$[" + text + "]", out var literal) ||
                literal.Segments[0].Selectors[0] is not NameSelector name)
                throw Invalid();
            return JsonSerializer.SerializeToElement(name.Name);
        }
        if (!char.IsAsciiDigit(text[0]) && text[0] != '-' && text is not ("true" or "false" or "null"))
            throw Invalid();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement SelectedValue(JsonPath query, JsonNode? candidate)
    {
        var matches = query.Evaluate(candidate).Matches;
        // Undefined represents Nothing; a selected JSON null remains a value.
        return matches.Count == 1 ? JsonSerializer.SerializeToElement(matches[0].Value) : default;
    }

    private static bool CompareValue(JsonElement actual, JsonElement expected, string comparison)
    {
        if (actual.ValueKind == JsonValueKind.Undefined || expected.ValueKind == JsonValueKind.Undefined)
        {
            var bothNothing = actual.ValueKind == expected.ValueKind;
            return bothNothing ? comparison is "==" or "<=" or ">=" : comparison == "!=";
        }
        var equal = JsonElement.DeepEquals(actual, expected);
        if (equal)
            return CompareOrder(0, comparison);
        if (comparison is "==" or "!=")
            return comparison == "!=";
        if (actual.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number)
            return CompareOrder(RuleCompiler.Number(actual).CompareTo(RuleCompiler.Number(expected)), comparison);
        if (actual.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String)
        {
            // UTF-8 lexicographic order is Unicode scalar order, unlike UTF-16
            // ordinal order for supplementary characters.
            var a = Encoding.UTF8.GetBytes(actual.GetString()!);
            var b = Encoding.UTF8.GetBytes(expected.GetString()!);
            return CompareOrder(a.AsSpan().SequenceCompareTo(b), comparison);
        }
        return false;
    }

    private static bool CompareOrder(int order, string comparison) => comparison switch
    {
        "==" => order == 0,
        "!=" => order != 0,
        "<" => order < 0,
        ">" => order > 0,
        "<=" => order <= 0,
        ">=" => order >= 0,
        _ => throw Invalid()
    };

    // The locked length() counts graphemes instead of Unicode scalars. Admit
    // a singular-query or string-literal comparison with a private predicate,
    // never by replacing a process-global JsonPath.Net function registration.
    private static JsonElement ReadScalarLiteral(string path, ref int index)
    {
        SkipWhitespace(path, ref index);
        var start = index;
        if (index >= path.Length)
            throw Invalid();
        if (path[index] is '\'' or '"')
            ReadQuoted(path, ref index);
        else if (!Take(path, ref index, "true") && !Take(path, ref index, "false") &&
                 !Take(path, ref index, "null"))
        {
            while (index < path.Length && (char.IsAsciiDigit(path[index]) || path[index] is '-' or '+' or '.' or 'e' or 'E'))
                index++;
        }
        var literal = ReadComparisonLiteral(path[start..index].Trim());
        if (literal.ValueKind == JsonValueKind.Number)
            _ = RuleCompiler.Number(literal);
        return literal;
    }

    private static JsonPath ReadSingularQuery(string path, ref int index, ref bool usesRepeatedTraversal)
    {
        SkipWhitespace(path, ref index);
        var start = index;
        if (index >= path.Length || path[index] is not ('@' or '$'))
            throw Invalid();
        usesRepeatedTraversal |= path[index++] == '$';
        var unionProduct = 1;
        var nested = false;
        var repeatedDescent = false;
        if (!ReadSegments(path, ref index, [], ref usesRepeatedTraversal, ref unionProduct,
                ref nested, ref repeatedDescent, 0) ||
            !JsonPath.TryParse(path[start..index].Trim(), out var query,
                new PathParsingOptions { AllowRelativePathStart = true }) ||
            !query.IsSingular || query.Segments.Length > 16)
            throw Invalid();
        return query;
    }

    private static bool TryReadLengthFilter(string path, ref int index, out ISelector? selector, ref bool usesRepeatedTraversal)
    {
        selector = null;
        var cursor = index;
        SkipWhitespace(path, ref cursor);
        var expressionStart = cursor;
        if (!Take(path, ref cursor, "length"))
            return false;
        Require(path, ref cursor, "(");
        SkipWhitespace(path, ref cursor);
        if (cursor >= path.Length)
            throw Invalid();
        JsonPath? argument = null;
        int? literalLength = null;
        if (path[cursor] is '@' or '$')
            argument = ReadSingularQuery(path, ref cursor, ref usesRepeatedTraversal);
        else
        {
            var literal = ReadScalarLiteral(path, ref cursor);
            if (literal.ValueKind == JsonValueKind.String)
                literalLength = BoundedLengthFilterSelector.ScalarCount(literal.GetString()!);
        }
        Require(path, ref cursor, ")");
        string? comparison = null;
        foreach (var op in new[] { "==", "!=", "<=", ">=", "<", ">" })
        {
            if (Take(path, ref cursor, op))
            {
                comparison = op;
                break;
            }
        }
        if (comparison is null)
            throw Invalid();
        SkipWhitespace(path, ref cursor);
        if (cursor >= path.Length)
            throw Invalid();
        JsonPath? expectedQuery = null;
        JsonElement expectedLiteral = default;
        if (path[cursor] is '@' or '$')
            expectedQuery = ReadSingularQuery(path, ref cursor, ref usesRepeatedTraversal);
        else
            expectedLiteral = ReadScalarLiteral(path, ref cursor);
        selector = new BoundedLengthFilterSelector(argument, literalLength, expectedQuery, expectedLiteral, comparison,
            path[expressionStart..cursor]);
        index = cursor;
        return true;
    }

    private static bool TryReadRegexFilter(string path, ref int index, out ISelector? selector, ref bool usesRepeatedTraversal)
    {
        selector = null;
        var cursor = index;
        SkipWhitespace(path, ref cursor);
        var expressionStart = cursor;
        var fullMatch = Take(path, ref cursor, "match");
        if (!fullMatch && !Take(path, ref cursor, "search"))
            return false;
        Require(path, ref cursor, "(");
        SkipWhitespace(path, ref cursor);
        if (cursor >= path.Length)
            throw Invalid();
        JsonPath? argument = null;
        string? literalArgument = null;
        if (path[cursor] is '@' or '$')
            argument = ReadSingularQuery(path, ref cursor, ref usesRepeatedTraversal);
        else
        {
            var literal = ReadScalarLiteral(path, ref cursor);
            if (literal.ValueKind == JsonValueKind.String)
                literalArgument = literal.GetString();
        }
        Require(path, ref cursor, ",");
        // Dynamic patterns remain rejected: compilation work is configuration-owned.
        var patternLiteral = ReadScalarLiteral(path, ref cursor);
        Require(path, ref cursor, ")");
        var pattern = patternLiteral.ValueKind == JsonValueKind.String
            ? BoundedRegexFilterSelector.Compile(patternLiteral.GetString()!, fullMatch)
            : null;
        // Non-string arguments are logical false, never coerced to text.
        // Constant input/pattern pairs are evaluated at activation.
        bool? literalResult = pattern is null ? false : argument is not null ? null :
            literalArgument is not null && pattern.IsMatch(literalArgument);
        selector = new BoundedRegexFilterSelector(argument, literalResult, pattern, path[expressionStart..cursor]);
        index = cursor;
        return true;
    }

    private static void ReadExpression(string path, ref int index, int depth, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth)
    {
        ReadTerm(path, ref index, depth, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
        while (Take(path, ref index, "&&") || Take(path, ref index, "||"))
            ReadTerm(path, ref index, depth, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
    }

    private static void ReadTerm(string path, ref int index, int depth, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth)
    {
        if (depth > 8)
            throw Invalid();
        SkipWhitespace(path, ref index);
        var termStart = index;
        if (Take(path, ref index, "!"))
        {
            ReadTerm(path, ref index, depth + 1, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
            // The pinned unary parser consumes a complete logical expression.
            // Explicitly group the negated term to preserve RFC NOT precedence.
            grouping.Add((termStart, '('));
            grouping.Add((index, ')'));
            return;
        }
        if (Take(path, ref index, "("))
        {
            ReadExpression(path, ref index, depth + 1, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
            Require(path, ref index, ")");
            return;
        }

        var left = ReadOperand(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
        if (Take(path, ref index, "==") || Take(path, ref index, "!=") ||
            Take(path, ref index, "<=") || Take(path, ref index, ">=") ||
            Take(path, ref index, "<") || Take(path, ref index, ">"))
        {
            var right = ReadOperand(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
            if (!left.Comparable || !right.Comparable)
                throw Invalid();
        }
        else if (!left.Query)
            throw Invalid();
    }

    // A query may be tested for existence; comparisons require a singular
    // query or a value. The pinned parser still validates full RFC syntax.
    private delegate void NestedPrivateReader(ref int index);

    private static (bool Query, bool Comparable) ReadOperand(string path, ref int index, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth, NestedPrivateReader? nestedPrivateReader = null)
    {
        SkipWhitespace(path, ref index);
        if (index >= path.Length)
            throw Invalid();
        if (path[index] is '@' or '$')
        {
            var root = path[index++] == '$';
            usesRepeatedTraversal |= root;
            var singular = ReadSegments(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth, nestedPrivateReader);
            return (true, singular);
        }
        // These built-ins have bounded work on a nodelist.
        // Root queries and descent receive the separate repeated-traversal charge.
        // Do not admit dynamic/regex or custom functions.
        if (Take(path, ref index, "count") || Take(path, ref index, "value"))
        {
            Require(path, ref index, "(");
            SkipWhitespace(path, ref index);
            if (index >= path.Length || path[index] is not ('@' or '$'))
                throw Invalid();
            usesRepeatedTraversal |= path[index++] == '$';
            ReadSegments(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth, nestedPrivateReader);
            Require(path, ref index, ")");
            return (false, true);
        }
        if (index < path.Length && path[index] is '\'' or '"')
        {
            ReadQuoted(path, ref index);
            return (false, true);
        }
        if (Take(path, ref index, "true") || Take(path, ref index, "false") || Take(path, ref index, "null"))
            return (false, true);

        var start = index;
        while (index < path.Length && (char.IsAsciiDigit(path[index]) || path[index] is '-' or '+' or '.' or 'e' or 'E'))
            index++;
        if (start == index)
            throw Invalid();
        using var number = JsonDocument.Parse(path[start..index]);
        _ = RuleCompiler.Number(number.RootElement);
        return (false, true);
    }

    private static bool ReadSegments(string path, ref int index, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth, NestedPrivateReader? nestedPrivateReader = null)
    {
        var singular = true;
        var unionProduct = 1;
        var descentCount = 0;
        while (index < path.Length)
        {
            // Two descents may multiply overlapping paths. The compiler
            // requests a separate cubic precharge for that query shape.
            var descendant = path.AsSpan(index).StartsWith("..", StringComparison.Ordinal);
            if (descendant)
            {
                if (++descentCount > 2)
                    throw Invalid();
                hasRepeatedPredicateDescent |= descentCount == 2;
                singular = false;
                usesRepeatedTraversal = true;
                index += 2;
                if (index >= path.Length)
                    throw Invalid();
            }
            if ((!descendant && path[index] == '.') || (descendant && path[index] != '['))
            {
                if (!descendant)
                    index++;
                if (index < path.Length && path[index] == '*')
                {
                    singular = false;
                    index++;
                    continue;
                }
                var start = index;
                while (index < path.Length && (char.IsAsciiLetterOrDigit(path[index]) || path[index] == '_' || path[index] >= 128))
                    index++;
                if (start == index)
                    throw Invalid();
            }
            else if (path[index] == '[')
            {
                index++;
                if (Take(path, ref index, "?"))
                {
                    // Admit one nested filter with a relative, non-overlapping
                    // query. The compiler excludes root references, unions and
                    // descent from any expression containing this nested filter.
                    if (filterDepth != 0 || hasNestedFilter)
                        throw Invalid();
                    hasNestedFilter = true;
                    if (ContainsLocalFunction(path, index))
                    {
                        if (nestedPrivateReader is null)
                            throw Invalid();
                        nestedPrivateReader(ref index);
                    }
                    else
                        ReadExpression(path, ref index, 0, grouping, ref usesRepeatedTraversal,
                            ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth + 1);
                    Require(path, ref index, "]");
                    singular = false;
                    continue;
                }
                var selectors = 1;
                singular &= ReadSelector(path, ref index);
                while (Take(path, ref index, ","))
                {
                    if (++selectors > 4 / unionProduct)
                        throw Invalid();
                    _ = ReadSelector(path, ref index);
                    singular = false;
                }
                unionProduct *= selectors;
                Require(path, ref index, "]");
            }
            else
                break;
        }
        maxUnionProduct = Math.Max(maxUnionProduct, unionProduct);
        return singular;
    }

    private static bool ReadSelector(string path, ref int index)
    {
        SkipWhitespace(path, ref index);
        if (Take(path, ref index, "*"))
            return false;
        if (index < path.Length && path[index] is '\'' or '"')
        {
            ReadQuoted(path, ref index);
            return true;
        }
        var hasIndex = ReadOptionalIndex(path, ref index);
        if (Take(path, ref index, ":"))
        {
            ReadOptionalIndex(path, ref index);
            if (Take(path, ref index, ":"))
                ReadOptionalIndex(path, ref index);
            return false;
        }
        if (!hasIndex)
            throw Invalid();
        return true;
    }

    // A slice selects each member at most once; unlike unions or descent it
    // cannot multiply visits to a candidate subtree. The library validates
    // integer syntax/range and clamps slice bounds during evaluation.
    private static bool ReadOptionalIndex(string path, ref int index)
    {
        SkipWhitespace(path, ref index);
        var negative = index < path.Length && path[index] == '-';
        if (negative)
            index++;
        var start = index;
        while (index < path.Length && char.IsAsciiDigit(path[index]))
            index++;
        if (negative && start == index)
            throw Invalid();
        return start != index;
    }

    private static void ReadQuoted(string path, ref int index)
    {
        var quote = path[index++];
        while (index < path.Length)
        {
            var current = path[index++];
            if (current == quote)
                return;
            if (current == '\\')
                index++;
        }
        throw Invalid();
    }

    private static bool Take(string path, ref int index, string token)
    {
        SkipWhitespace(path, ref index);
        if (!path.AsSpan(index).StartsWith(token, StringComparison.Ordinal))
            return false;
        index += token.Length;
        return true;
    }

    private static void Require(string path, ref int index, string token)
    {
        if (!Take(path, ref index, token))
            throw Invalid();
    }

    private static void SkipWhitespace(string path, ref int index)
    {
        while (index < path.Length && char.IsWhiteSpace(path[index]))
            index++;
    }

    private static InvalidOperationException Invalid() =>
        new("JSONPath filter exceeds the supported bounded expression grammar.");
}

// Preserve original nodes and locations; precharge every potential regex leaf
// even when the actual predicate will short-circuit.
internal sealed class BoundedCompoundFilterSelector(Func<JsonNode?, JsonNode?, bool> predicate,
    int regexCount, string expression) : ISelector
{
    private static readonly ISelector Children = JsonPath.Parse("$[*]").Segments[0].Selectors[0];

    internal int RegexCount { get; } = regexCount;

    public IEnumerable<Node> Evaluate(Node match, JsonNode? rootNode)
    {
        foreach (var candidate in Children.Evaluate(match, rootNode))
        {
            if (predicate(rootNode, candidate.Value))
                yield return candidate;
        }
    }

    public void BuildString(StringBuilder builder) => builder.Append('?').Append(expression);
}
