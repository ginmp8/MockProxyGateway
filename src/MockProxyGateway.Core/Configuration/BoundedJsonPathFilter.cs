using System.Text;
using System.Text.Json;
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
                    if (TryReadLengthFilter(path, ref index, out var local, ref usesRepeatedTraversal))
                        localFilters.Add(filterIndex, local!);
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

    // The locked length() counts graphemes instead of Unicode scalars. Admit
    // a standalone singular-query-to-number comparison with a private selector,
    // never by replacing a process-global JsonPath.Net function registration.
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
        var start = cursor;
        if (cursor >= path.Length || path[cursor] is not ('@' or '$'))
            throw Invalid();
        usesRepeatedTraversal |= path[cursor] == '$';
        while (cursor < path.Length && path[cursor] != ')')
        {
            if (path[cursor] is '\'' or '"')
                ReadQuoted(path, ref cursor);
            else
                cursor++;
        }
        if (cursor == path.Length ||
            !JsonPath.TryParse(path[start..cursor].Trim(), out var argument,
                new PathParsingOptions { AllowRelativePathStart = true }) ||
            !argument.IsSingular || argument.Segments.Length > 16)
            throw Invalid();
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
        start = cursor;
        while (cursor < path.Length && (char.IsAsciiDigit(path[cursor]) || path[cursor] is '-' or '+' or '.' or 'e' or 'E'))
            cursor++;
        if (start == cursor)
            throw Invalid();
        using var number = JsonDocument.Parse(path[start..cursor]);
        selector = new BoundedLengthFilterSelector(argument, RuleCompiler.Number(number.RootElement), comparison,
            path[expressionStart..cursor]);
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
    private static (bool Query, bool Comparable) ReadOperand(string path, ref int index, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth)
    {
        SkipWhitespace(path, ref index);
        if (index >= path.Length)
            throw Invalid();
        if (path[index] is '@' or '$')
        {
            var root = path[index++] == '$';
            usesRepeatedTraversal |= root;
            var singular = ReadSegments(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
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
            ReadSegments(path, ref index, grouping, ref usesRepeatedTraversal, ref maxUnionProduct, ref hasNestedFilter, ref hasRepeatedPredicateDescent, filterDepth);
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

    private static bool ReadSegments(string path, ref int index, List<(int Offset, char Character)> grouping, ref bool usesRepeatedTraversal, ref int maxUnionProduct, ref bool hasNestedFilter, ref bool hasRepeatedPredicateDescent, int filterDepth)
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
