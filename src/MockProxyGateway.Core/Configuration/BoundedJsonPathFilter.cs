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
    // JsonPath.Net accepts all .NET whitespace as separators and trims
    // complete queries. RFC 9535 allows only SP, HTAB, LF and CR as blank
    // space; other Unicode whitespace can instead be part of a member name.
    // Protect those names with equivalent bracket selectors before parsing.
    internal static string NormalizeWhitespace(string path)
    {
        var normalized = new StringBuilder(path.Length);
        var index = 0;
        while (index < path.Length)
        {
            var start = index;
            var current = path[index];
            if (current is '\'' or '"')
            {
                ReadQuoted(path, ref index);
                AppendQuotedEscapes(normalized, path.AsSpan(start, index - start));
                continue;
            }

            if (current == '.')
            {
                var descendant = path.AsSpan(index).StartsWith("..", StringComparison.Ordinal);
                index += descendant ? 2 : 1;
                var nameStart = index;
                if (index < path.Length && IsNameFirst(path[index]))
                {
                    var requiresBrackets = false;
                    while (index < path.Length && (IsNameFirst(path[index]) || char.IsAsciiDigit(path[index])))
                    {
                        requiresBrackets |= char.IsWhiteSpace(path[index]);
                        index++;
                    }

                    if (requiresBrackets)
                    {
                        if (descendant)
                            normalized.Append("..");
                        normalized.Append("['").Append(path.AsSpan(nameStart, index - nameStart)).Append("']");
                        continue;
                    }
                }
                normalized.Append(path.AsSpan(start, index - start));
                continue;
            }

            // The pinned integer parser uses char.IsDigit followed by subtraction
            // of ASCII '0'. Non-ASCII decimal digits are not RFC index/slice digits.
            // Quoted strings and shorthand member names were consumed above.
            if ((char.IsWhiteSpace(current) && !IsBlankSpace(current)) ||
                (char.IsDigit(current) && !char.IsAsciiDigit(current)))
                throw Invalid();
            normalized.Append(current);
            index++;
        }
        return normalized.ToString();
    }

    // The pinned name parser consumes the next backslash while reading a
    // Unicode escape run. Keep adjacent legal short escapes in that same run
    // using their equivalent UTF-16 code units, without changing the value.
    private static void AppendQuotedEscapes(StringBuilder target, ReadOnlySpan<char> token)
    {
        var quote = token[0];
        target.Append(quote);
        var afterHex = false;
        var pendingHighSurrogate = false;
        for (var i = 1; i < token.Length - 1;)
        {
            if (token[i] != '\\')
            {
                if (pendingHighSurrogate)
                    throw Invalid();
                target.Append(token[i++]);
                afterHex = false;
                continue;
            }

            var start = i++;
            var escaped = token[i++];
            if (escaped == 'u')
            {
                if (i + 4 > token.Length - 1 ||
                    !char.IsAsciiHexDigit(token[i]) || !char.IsAsciiHexDigit(token[i + 1]) ||
                    !char.IsAsciiHexDigit(token[i + 2]) || !char.IsAsciiHexDigit(token[i + 3]))
                    throw Invalid();
                // RFC 9535 quoted strings permit only Unicode scalar values.
                // JsonNode.Parse can defer string decoding until evaluation, so
                // validate escaped surrogate pairs before publishing any rule.
                var codeUnit = (char)ushort.Parse(token.Slice(i, 4),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (pendingHighSurrogate != char.IsLowSurrogate(codeUnit))
                    throw Invalid();
                pendingHighSurrogate = char.IsHighSurrogate(codeUnit);
                i += 4;
                target.Append(token[start..i]);
                afterHex = true;
                continue;
            }

            if (pendingHighSurrogate)
                throw Invalid();

            if (afterHex)
            {
                var replacement = escaped switch
                {
                    'b' => "\\u0008",
                    't' => "\\u0009",
                    'n' => "\\u000A",
                    'f' => "\\u000C",
                    'r' => "\\u000D",
                    '/' => "\\u002F",
                    '\\' => "\\u005C",
                    '"' when quote == '"' => "\\u0022",
                    '\'' when quote == '\'' => "\\u0027",
                    _ => null
                };
                if (replacement is not null)
                {
                    target.Append(replacement);
                    continue;
                }
            }

            target.Append(token[start..i]);
            afterHex = false;
        }
        if (pendingHighSurrogate)
            throw Invalid();
        target.Append(quote);
    }

    private static bool IsNameFirst(char c) => char.IsAsciiLetter(c) || c == '_' || c >= '\u0080';
    private static bool IsBlankSpace(char c) => c is ' ' or '\t' or '\r' or '\n';

    internal static string Normalize(string path, out bool usesRepeatedTraversal, out int maxUnionProduct, out bool hasNestedFilter, out bool hasRepeatedPredicateDescent, out int privateValueUnionProduct, out bool hasRootDrivenNestedFilter, out Dictionary<int, ISelector> localFilters)
    {
        localFilters = [];
        var filterIndex = 0;
        usesRepeatedTraversal = false;
        privateValueUnionProduct = 0;
        maxUnionProduct = 1;
        hasNestedFilter = false;
        hasRepeatedPredicateDescent = false;
        hasRootDrivenNestedFilter = false;
        const int filterDepth = 0;
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
                    if (RequiresLocalPredicate(path, index))
                    {
                        var start = index;
                        var parser = new LocalExpressionParser(path, index, usesRepeatedTraversal, maxUnionProduct,
                            hasNestedFilter, hasRepeatedPredicateDescent, privateValueUnionProduct);
                        var predicate = parser.ReadOr(0);
                        index = parser.Position;
                        usesRepeatedTraversal = parser.UsesRepeatedTraversal;
                        privateValueUnionProduct = parser.PrivateValueUnionProduct;
                        maxUnionProduct = parser.MaxUnionProduct;
                        hasNestedFilter = parser.HasNestedFilter;
                        hasRootDrivenNestedFilter |= parser.HasRootDrivenNestedFilter;
                        hasRepeatedPredicateDescent = parser.HasRepeatedPredicateDescent;
                        localFilters.Add(filterIndex, new BoundedCompoundFilterSelector(predicate,
                            parser.RegexCount, path[start..index]));
                    }
                    else
                        ReadExpression(path, ref index, 0, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
                    filterIndex++;
                }
                else
                    _ = ReadSelector(path, ref index);
            }
            while (Take(path, ref index, ","));
            Require(path, ref index, "]");
        }
        if (grouping.Count == 0)
            return path;
        var normalized = new StringBuilder(path);
        foreach (var (offset, character) in grouping.OrderByDescending(x => x.Offset))
            normalized.Insert(offset, character);
        return normalized.ToString();
    }

    // Detect private functions, ordered comparisons or document-root references
    // in a nested predicate, skipping quoted strings. These need a local selector
    // for RFC scalar ordering, Nothing semantics or the original root scope.
    // Other ordinary outer predicates retain the library evaluation path.
    private static bool RequiresLocalPredicate(string path, int index, bool insideNestedFilter = false)
    {
        var brackets = 0;
        var parentheses = 0;
        var sawRoot = false;
        while (index < path.Length)
        {
            var current = path[index];
            if (current is '\'' or '"')
            {
                ReadQuoted(path, ref index);
                continue;
            }
            if (current is '<' or '>')
                return true;
            if (current == '$')
            {
                if (insideNestedFilter || brackets > 0)
                    return true;
                sawRoot = true;
            }
            if (current == '?' && brackets > 0 && sawRoot)
                return true;
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
        int unionProduct, bool nestedFilter, bool repeatedDescent, int privateValueQuery)
    {
        private int _index = start;
        private bool _usesRepeatedTraversal = repeatedTraversal;
        private int _privateValueUnionProduct = privateValueQuery;
        private int _maxUnionProduct = unionProduct;
        private bool _hasNestedFilter = nestedFilter;
        private bool _hasRepeatedPredicateDescent = repeatedDescent;
        private int _termCount;
        internal int Position => _index;
        internal bool UsesRepeatedTraversal => _usesRepeatedTraversal;
        internal int PrivateValueUnionProduct => _privateValueUnionProduct;
        internal int MaxUnionProduct => _maxUnionProduct;
        internal bool HasNestedFilter => _hasNestedFilter;
        internal bool HasRootDrivenNestedFilter { get; private set; }
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

        private Func<JsonNode?, JsonNode?, bool> ReadTerm(int depth, bool negatedTest = false)
        {
            if (depth > 8)
                throw Invalid();
            if (Take(path, ref _index, "!"))
            {
                // RFC 9535 allows a single NOT before a test or parenthesized expression.
                if (negatedTest)
                    throw Invalid();
                var operand = ReadTerm(depth + 1, negatedTest: true);
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
            // The ordinary length adapter cannot consume a private nested
            // filter. Probe without committing parser state; the full operand
            // reader below admits only a single nested filter and retains its
            // original traversal/regex budget flags.
            ISelector? local;
            var lengthCursor = _index;
            var lengthTraversal = _usesRepeatedTraversal;
            var lengthValueQuery = _privateValueUnionProduct;
            try
            {
                if (TryReadLengthFilter(path, ref lengthCursor, out local, ref lengthTraversal, ref lengthValueQuery))
                {
                    if (negatedTest)
                        throw Invalid();
                    _index = lengthCursor;
                    _usesRepeatedTraversal = lengthTraversal;
                    _privateValueUnionProduct = lengthValueQuery;
                    return ((BoundedLengthFilterSelector)local!).MatchesCandidate;
                }
            }
            catch (InvalidOperationException)
            {
                // Reparse with the bounded local operand reader; an ordinary
                // malformed length expression is still rejected below.
            }
            if (TryReadRegexFilter(path, ref _index, out local, ref _usesRepeatedTraversal, ref _privateValueUnionProduct))
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
                    _maxUnionProduct, _hasNestedFilter, _hasRepeatedPredicateDescent, _privateValueUnionProduct);
                var nestedPredicate = nested.ReadOr(0);
                cursor = nested.Position;
                // Root references inside this relative, non-overlapping nested
                // query are covered by the existing whole-input quadratic charge.
                // Keep the enclosing traversal flag until its query shape is
                // classified; a root-driven query can revisit every candidate.
                if (ContainsUnquotedDescent(path, nestedStart, cursor))
                    throw Invalid();
                _privateValueUnionProduct = nested.PrivateValueUnionProduct;
                _maxUnionProduct = nested.MaxUnionProduct;
                _hasNestedFilter = nested.HasNestedFilter;
                _hasRepeatedPredicateDescent = nested.HasRepeatedPredicateDescent;
                RegexCount += nested.RegexCount;
                nestedPrivate = new BoundedCompoundFilterSelector(nestedPredicate, nested.RegexCount,
                    path[nestedStart..cursor]);
            }

            (bool Query, bool Comparable) ReadLocalOperand(ref int cursor,
                out (JsonPath? Query, int? Literal)? lengthOperand)
            {
                lengthOperand = null;
                if (Take(path, ref cursor, "length"))
                {
                    lengthOperand = ReadLengthArgument(path, ref cursor, ref _usesRepeatedTraversal, ref _privateValueUnionProduct);
                    return (false, true);
                }
                return ReadOperand(path, ref cursor, grouping, ref _usesRepeatedTraversal,
                    ref _maxUnionProduct, ref _hasNestedFilter, ref _hasRepeatedPredicateDescent, 0, ReadNestedPrivate);
            }

            var repeatedBeforeLeaf = _usesRepeatedTraversal;
            var left = ReadLocalOperand(ref _index, out var leftLength);
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
            (JsonPath? Query, int? Literal)? rightLength = null;
            if (comparison is not null)
            {
                if (negatedTest)
                    throw Invalid();
                if (leftLength is null && nestedPrivate is null && TryReadReversedLengthFilter(path, ref _index,
                        path[leafStart..leftEnd].Trim(), comparison, out local, ref _usesRepeatedTraversal, ref _privateValueUnionProduct))
                    return ((BoundedLengthFilterSelector)local!).MatchesCandidate;
                var right = ReadLocalOperand(ref _index, out rightLength);
                if (!left.Comparable || !right.Comparable)
                    throw Invalid();
            }
            else if (!left.Query)
                throw Invalid();

            if (nestedPrivate is null && (leftLength is not null || rightLength is not null))
                throw Invalid();

            // Keep the existing unary NOT correction for ordinary nested
            // predicates; outer logical precedence is handled above.
            var expression = new StringBuilder(path[leafStart.._index]);
            foreach (var (offset, character) in grouping.OrderByDescending(x => x.Offset))
                expression.Insert(offset - leafStart, character);
            if (nestedPrivate is not null)
            {
                // Inner predicates always receive the original document root.
                // The enclosing query may start at the root or outer candidate;
                // root-driven traversal requires a stronger pre-evaluation bound.
                var queryText = comparison is null ? expression.ToString().Trim()
                    : leftContainsPrivate ? path[leafStart..leftEnd].Trim() : path[rightStart.._index].Trim();
                string? function = null;
                JsonElement expected = default;
                JsonPath? expectedQuery = null;
                var expectedFromRoot = false;
                string? expectedFunction = null;
                var expectedLength = leftContainsPrivate ? rightLength : leftLength;
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
                    // ReadOperand has already admitted both operands under the same
                    // relative-query and traversal limits. Only count/value may
                    // consume a non-singular query on the other side.
                    if (expectedLength is null && (rightText.StartsWith("count", StringComparison.Ordinal) ||
                        rightText.StartsWith("value", StringComparison.Ordinal)))
                    {
                        var otherOpen = rightText.IndexOf('(');
                        if (otherOpen < 0 || !rightText.EndsWith(')'))
                            throw Invalid();
                        expectedFunction = rightText[..otherOpen].Trim();
                        if (expectedFunction is not ("count" or "value"))
                            throw Invalid();
                        rightText = rightText[(otherOpen + 1)..^1].Trim();
                        if (!rightText.StartsWith('@') && !rightText.StartsWith('$'))
                            throw Invalid();
                    }
                    if (rightText.StartsWith('@'))
                    {
                        if (!JsonPath.TryParse("$" + rightText[1..], out expectedQuery) ||
                            (expectedFunction is null && !expectedQuery.IsSingular) ||
                            expectedQuery.Segments.Length > 16)
                            throw Invalid();
                    }
                    else if (expectedLength is null && rightText.StartsWith('$'))
                    {
                        if (!JsonPath.TryParse(rightText, out expectedQuery) ||
                            (expectedFunction is null && !expectedQuery.IsSingular) ||
                            expectedQuery.Segments.Length > 16 ||
                            expectedQuery.Segments.Any(segment => segment.IsRecursive || segment.Selectors.Length != 1 ||
                                segment.Selectors[0] is FilterSelector))
                            throw Invalid();
                        expectedFromRoot = true;
                    }
                    else if (expectedLength is null)
                    {
                        expected = ReadComparisonLiteral(rightText);
                    }
                }
                var queryFromRoot = queryText.StartsWith('$');
                if ((!queryFromRoot && !queryText.StartsWith('@')) ||
                    !JsonPath.TryParse("$" + queryText[1..], out var query) ||
                    query.Segments.Length > 16)
                    throw Invalid();
                if (queryFromRoot)
                {
                    // Root traversal can run once per outer candidate.
                    // Keep union/descent exclusions and charge cubic work and
                    // quadratic regex visits. The independently bounded length operand
                    // is covered by the same conservative traversal charge.
                    if (ContainsUnquotedDescent(path, leafStart, _index) ||
                        query.Segments.Any(segment => segment.IsRecursive || segment.Selectors.Length != 1))
                        throw Invalid();
                    HasRootDrivenNestedFilter = true;
                    _usesRepeatedTraversal = repeatedBeforeLeaf;
                }
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
                if (expectedFromRoot || expectedLength?.Query?.Scope == PathScope.Global)
                {
                    // Relative enclosing selection is non-overlapping; its quadratic
                    // charge also covers bounded root length operands, including
                    // explicit value conversion. Root-driven enclosing selection
                    // receives the cubic charge. Preserve all earlier traversal flags.
                    if (ContainsUnquotedDescent(path, leafStart, _index) ||
                        query.Segments.Any(segment => segment.IsRecursive || segment.Selectors.Length != 1))
                        throw Invalid();
                    _usesRepeatedTraversal = repeatedBeforeLeaf;
                }
                if (function is not null)
                {
                    return (root, candidate) =>
                    {
                        var actual = EvaluateNestedComparison(query, function, root, queryFromRoot ? root : candidate);
                        var operand = expectedLength is { } length
                            ? LengthValue(length.Query, length.Literal, root, candidate)
                            : expectedQuery is null ? expected :
                                EvaluateComparisonQuery(expectedQuery, expectedFunction, expectedFromRoot ? root : candidate);
                        return leftContainsPrivate ? CompareValue(actual, operand, comparison!) : CompareValue(operand, actual, comparison!);
                    };
                }
                return (root, candidate) =>
                {
                    // Consume the entire bounded nodelist, as before. Stopping at
                    // the first match could hide an inspection failure in a later
                    // candidate and change the selected action under negation.
                    var hasMatch = false;
                    foreach (var _ in EvaluateNestedQuery(query, root, queryFromRoot ? root : candidate))
                        hasMatch = true;
                    return hasMatch;
                };
            }

            // The pinned library compares strings by UTF-16 code units and
            // returns false for <= between two absent query results. Compile
            // ordered leaves with the same typed comparator as private nested
            // comparisons. The original parser has already validated operands
            // and reserved their traversal and union bounds.
            if (comparison is "<" or ">" or "<=" or ">=")
            {
                string OperandText(int from, int to)
                {
                    var operand = new StringBuilder(path[from..to]);
                    foreach (var (offset, character) in grouping.Where(x => x.Offset >= from && x.Offset <= to)
                        .OrderByDescending(x => x.Offset))
                        operand.Insert(offset - from, character);
                    return operand.ToString().Trim();
                }

                var readLeft = CompileOrderedOperand(OperandText(leafStart, leftEnd));
                var readRight = CompileOrderedOperand(OperandText(rightStart, _index));
                return (root, candidate) => CompareValue(readLeft(root, candidate), readRight(root, candidate), comparison);
            }

            if (!JsonPath.TryParse("$[?" + expression.ToString().Trim() + "]", out var parsed) ||
                parsed.Segments[0].Selectors[0] is not FilterSelector filter)
                throw Invalid();
            return filter.Expression.Evaluate;
        }
    }

    // JsonPath.Evaluate(candidate) would replace the document root with the
    // candidate. Traverse the already compiled, non-recursive selectors with
    // their public root argument instead; relative node locations are private.
    private static IEnumerable<Node> EvaluateNestedQuery(JsonPath query, JsonNode? root, JsonNode? candidate)
    {
        IEnumerable<Node> matches = JsonPath.Root.Evaluate(candidate).Matches;
        foreach (var segment in query.Segments)
        {
            if (segment.IsRecursive || segment.Selectors.Length != 1)
                throw Invalid();
            var selector = segment.Selectors[0];
            matches = matches.SelectMany(node => selector.Evaluate(node, root));
        }
        return matches;
    }

    private static JsonElement EvaluateNestedComparison(JsonPath query, string function, JsonNode? root, JsonNode? candidate)
    {
        var matches = EvaluateNestedQuery(query, root, candidate).ToArray();
        return function == "count" ? JsonSerializer.SerializeToElement(matches.Length) :
            matches.Length == 1 ? JsonSerializer.SerializeToElement(matches[0].Value) : default;
    }

    private static bool ContainsUnquotedDescent(string path, int start, int end)
    {
        for (var index = start; index < end;)
        {
            if (path[index] is '\'' or '"')
                ReadQuoted(path, ref index);
            else if (path.AsSpan(index, end - index).StartsWith("..", StringComparison.Ordinal))
                return true;
            else
                index++;
        }
        return false;
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

    private static Func<JsonNode?, JsonNode?, JsonElement> CompileOrderedOperand(string text)
    {
        string? function = null;
        if (text.StartsWith("count", StringComparison.Ordinal) || text.StartsWith("value", StringComparison.Ordinal))
        {
            var open = text.IndexOf('(');
            if (open < 0 || !text.EndsWith(')'))
                throw Invalid();
            function = text[..open].Trim();
            if (function is not ("count" or "value"))
                throw Invalid();
            text = text[(open + 1)..^1].Trim();
        }
        if (text.StartsWith('@') || text.StartsWith('$'))
        {
            var fromRoot = text[0] == '$';
            if (!JsonPath.TryParse("$" + text[1..], out var query) ||
                (function is null && !query.IsSingular))
                throw Invalid();
            return (root, candidate) => EvaluateComparisonQuery(query, function, fromRoot ? root : candidate);
        }
        if (function is not null)
            throw Invalid();
        var literal = ReadComparisonLiteral(text);
        return (_, _) => literal;
    }

    private static JsonElement LengthValue(JsonPath? query, int? literal, JsonNode? root, JsonNode? candidate) =>
        BoundedLengthFilterSelector.GetLength(query, literal, root, candidate) is { } length
            ? JsonSerializer.SerializeToElement(length) : default;

    private static JsonElement EvaluateComparisonQuery(JsonPath query, string? function, JsonNode? candidate) =>
        function == "count" ? JsonSerializer.SerializeToElement(query.Evaluate(candidate).Matches.Count) :
            SelectedValue(query, candidate);

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

    // Explicit conversion from a nodelist to ValueType. Admit bounded
    // unions with their multiplicity, but no recursive or filtered queries.
    // Precharge repeated whole-input work before any action.
    private static JsonPath ReadValueQuery(string path, ref int index, ref bool usesRepeatedTraversal, ref int privateValueUnionProduct)
    {
        Require(path, ref index, "(");
        SkipWhitespace(path, ref index);
        var start = index;
        if (index >= path.Length || path[index] is not ('@' or '$'))
            throw Invalid();
        var root = path[index++] == '$';
        var repeated = false;
        var unionProduct = 1;
        var nested = false;
        var repeatedDescent = false;
        _ = ReadSegments(path, ref index, [], ref repeated, ref unionProduct,
            ref nested, ref repeatedDescent, 0);
        if (repeated || nested ||
            !JsonPath.TryParse(path[start..index].Trim(), out var query,
                new PathParsingOptions { AllowRelativePathStart = true }) ||
            query.Segments.Length > 16)
            throw Invalid();
        Require(path, ref index, ")");
        // A relative conversion needs the conservative work charge, but
        // does not escape the nested candidate's scope like a root query.
        // Preserve the largest private operand union product across all leaves.
        usesRepeatedTraversal |= root;
        privateValueUnionProduct = Math.Max(privateValueUnionProduct, unionProduct);
        return query;
    }

    private static bool TryReadLengthFilter(string path, ref int index, out ISelector? selector, ref bool usesRepeatedTraversal, ref int privateValueUnionProduct)
    {
        selector = null;
        var cursor = index;
        SkipWhitespace(path, ref cursor);
        var expressionStart = cursor;
        if (!Take(path, ref cursor, "length"))
            return false;
        var (argument, literalLength) = ReadLengthArgument(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
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
        (JsonPath? Query, int? Literal)? expectedLength = null;
        var expectedCount = false;
        if (Take(path, ref cursor, "length"))
            expectedLength = ReadLengthArgument(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
        else if ((expectedCount = Take(path, ref cursor, "count")) || Take(path, ref cursor, "value"))
            expectedQuery = ReadValueQuery(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
        else if (path[cursor] is '@' or '$')
            expectedQuery = ReadSingularQuery(path, ref cursor, ref usesRepeatedTraversal);
        else
            expectedLiteral = ReadScalarLiteral(path, ref cursor);
        selector = new BoundedLengthFilterSelector(argument, literalLength, expectedQuery, expectedLiteral, comparison,
            path[expressionStart..cursor], expectedLength, expectedCount);
        index = cursor;
        return true;
    }

    private static (JsonPath? Argument, int? LiteralLength) ReadLengthArgument(string path, ref int cursor, ref bool usesRepeatedTraversal, ref int privateValueUnionProduct, bool allowNumericFunction = true)
    {
        Require(path, ref cursor, "(");
        SkipWhitespace(path, ref cursor);
        if (cursor >= path.Length)
            throw Invalid();
        JsonPath? argument = null;
        int? literalLength = null;
        if (allowNumericFunction && TryReadNonStringFunction(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct))
        {
            Require(path, ref cursor, ")");
            return (null, null);
        }
        if (Take(path, ref cursor, "value"))
            argument = ReadValueQuery(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
        else if (path[cursor] is '@' or '$')
            argument = ReadSingularQuery(path, ref cursor, ref usesRepeatedTraversal);
        else
        {
            var literal = ReadScalarLiteral(path, ref cursor);
            if (literal.ValueKind == JsonValueKind.String)
                literalLength = BoundedLengthFilterSelector.ScalarCount(literal.GetString()!);
        }
        Require(path, ref cursor, ")");
        return (argument, literalLength);
    }

    // Count returns a number; length returns a number or Nothing. Neither
    // can supply a string, array or object to a length/regex operand. Validate
    // the complete bounded inner argument and keep its original work flags,
    // then fold the result without an unnecessary request-time traversal.
    private static bool TryReadNonStringFunction(string path, ref int cursor, ref bool usesRepeatedTraversal, ref int privateValueUnionProduct)
    {
        if (Take(path, ref cursor, "count"))
        {
            _ = ReadValueQuery(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
            return true;
        }
        if (!Take(path, ref cursor, "length"))
            return false;
        _ = ReadLengthArgument(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct, allowNumericFunction: false);
        return true;
    }

    private static bool TryReadReversedLengthFilter(string path, ref int index, string operand,
        string comparison, out ISelector? selector, ref bool usesRepeatedTraversal, ref int privateValueUnionProduct)
    {
        selector = null;
        var cursor = index;
        if (!Take(path, ref cursor, "length"))
            return false;

        var operandCursor = 0;
        JsonPath? expectedQuery = null;
        JsonElement expectedLiteral = default;
        var expectedCount = Take(operand, ref operandCursor, "count");
        if (expectedCount || Take(operand, ref operandCursor, "value"))
            expectedQuery = ReadValueQuery(operand, ref operandCursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
        else if (operand.StartsWith('@') || operand.StartsWith('$'))
            expectedQuery = ReadSingularQuery(operand, ref operandCursor, ref usesRepeatedTraversal);
        else
            expectedLiteral = ReadScalarLiteral(operand, ref operandCursor);
        SkipWhitespace(operand, ref operandCursor);
        if (operandCursor != operand.Length)
            throw Invalid();

        var (argument, literalLength) = ReadLengthArgument(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
        // The existing selector compares length against its operand. Reverse only
        // the ordered operators; equality and Nothing semantics are symmetric.
        var reversed = comparison switch
        {
            "<" => ">",
            ">" => "<",
            "<=" => ">=",
            ">=" => "<=",
            _ => comparison
        };
        selector = new BoundedLengthFilterSelector(argument, literalLength, expectedQuery, expectedLiteral, reversed,
            operand + " " + comparison + " " + path[index..cursor].Trim(), expectedCount: expectedCount);
        index = cursor;
        return true;
    }

    private static bool TryReadRegexFilter(string path, ref int index, out ISelector? selector, ref bool usesRepeatedTraversal, ref int privateValueUnionProduct)
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
        if (Take(path, ref cursor, "value"))
            argument = ReadValueQuery(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct);
        else if (path[cursor] is '@' or '$')
            argument = ReadSingularQuery(path, ref cursor, ref usesRepeatedTraversal);
        else if (!TryReadNonStringFunction(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct))
        {
            var literal = ReadScalarLiteral(path, ref cursor);
            if (literal.ValueKind == JsonValueKind.String)
                literalArgument = literal.GetString();
        }
        Require(path, ref cursor, ",");
        // Dynamic string patterns remain rejected: compilation work is configuration-owned.
        // A statically non-string function result is always logical false.
        var patternLiteral = TryReadNonStringFunction(path, ref cursor, ref usesRepeatedTraversal, ref privateValueUnionProduct)
            ? default : ReadScalarLiteral(path, ref cursor);
        Require(path, ref cursor, ")");
        var pattern = patternLiteral.ValueKind == JsonValueKind.String
            ? BoundedRegexFilterSelector.Compile(patternLiteral.GetString()!, fullMatch)
            : null;
        // Non-string arguments are logical false, never coerced to text.
        // Constant input/pattern pairs are evaluated at activation.
        bool? literalResult = pattern is null ? false : argument is not null ? null :
            literalArgument is not null && pattern.IsMatchAtCompilation(literalArgument);
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

    private static void ReadTerm(string path, ref int index, int depth, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth, bool negatedTest = false)
    {
        if (depth > 8)
            throw Invalid();
        SkipWhitespace(path, ref index);
        var termStart = index;
        if (Take(path, ref index, "!"))
        {
            if (negatedTest)
                throw Invalid();
            ReadTerm(path, ref index, depth + 1, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth, negatedTest: true);
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
            if (negatedTest)
                throw Invalid();
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
            var singular = ReadSegments(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth, nestedPrivateReader, root);
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
            var root = path[index++] == '$';
            usesRepeatedTraversal |= root;
            ReadSegments(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth, nestedPrivateReader, root);
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

    private static bool ReadSegments(string path, ref int index, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth, NestedPrivateReader? nestedPrivateReader = null, bool rootQuery = false)
    {
        var singular = true;
        var unionProduct = 1;
        var descentCount = 0;
        while (index < path.Length)
        {
            // RFC 9535 permits blank space before each query segment.
            // Do not consume trailing expression whitespace, alter quoted
            // literals or admit whitespace inside a shorthand segment.
            var segmentStart = index;
            while (index < path.Length && path[index] is ' ' or '\t' or '\r' or '\n')
                index++;
            if (index == path.Length || path[index] is not ('.' or '['))
            {
                index = segmentStart;
                break;
            }
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
                    // Admit one nested filter. Root-driven enclosing queries use
                    // local selectors and the stronger bound; unions and descent
                    // remain excluded from nested predicates.
                    if (filterDepth != 0 || hasNestedFilter)
                        throw Invalid();
                    hasNestedFilter = true;
                    if (rootQuery || RequiresLocalPredicate(path, index, insideNestedFilter: true))
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
        while (index < path.Length && IsBlankSpace(path[index]))
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
