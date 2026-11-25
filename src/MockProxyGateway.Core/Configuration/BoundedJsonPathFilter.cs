using System.Text;
using System.Text.Json;

namespace MockProxyGateway.Configuration;

// JsonPath.Net owns parsing and evaluation. This smaller grammar is solely an
// admission policy for filter work that can be bounded before evaluation.
// It examines the original expression, not a potentially lossy AST rendering.
internal static class BoundedJsonPathFilter
{
    internal static string Normalize(string path, out bool usesRoot, out int maxUnionProduct)
    {
        usesRoot = false;
        maxUnionProduct = 1;
        var index = 0;
        var grouping = new List<(int Offset, char Character)>();
        while (index < path.Length)
        {
            if (path[index] is '\'' or '"')
            {
                ReadQuoted(path, ref index);
                continue;
            }
            if (path[index++] == '[' && Take(path, ref index, "?"))
            {
                ReadExpression(path, ref index, 0, grouping, ref usesRoot, ref maxUnionProduct);
                Require(path, ref index, "]");
                if (grouping.Count == 0)
                    return path;
                var normalized = new StringBuilder(path);
                foreach (var (offset, character) in grouping.OrderByDescending(x => x.Offset))
                    normalized.Insert(offset, character);
                return normalized.ToString();
            }
        }
        throw Invalid();
    }

    private static void ReadExpression(string path, ref int index, int depth, List<(int Offset, char Character)> grouping, ref bool usesRoot, ref int maxUnionProduct)
    {
        ReadTerm(path, ref index, depth, grouping, ref usesRoot, ref maxUnionProduct);
        while (Take(path, ref index, "&&") || Take(path, ref index, "||"))
            ReadTerm(path, ref index, depth, grouping, ref usesRoot, ref maxUnionProduct);
    }

    private static void ReadTerm(string path, ref int index, int depth, List<(int Offset, char Character)> grouping, ref bool usesRoot, ref int maxUnionProduct)
    {
        if (depth > 8)
            throw Invalid();
        SkipWhitespace(path, ref index);
        var termStart = index;
        if (Take(path, ref index, "!"))
        {
            ReadTerm(path, ref index, depth + 1, grouping, ref usesRoot, ref maxUnionProduct);
            // The pinned unary parser consumes a complete logical expression.
            // Explicitly group the negated term to preserve RFC NOT precedence.
            grouping.Add((termStart, '('));
            grouping.Add((index, ')'));
            return;
        }
        if (Take(path, ref index, "("))
        {
            ReadExpression(path, ref index, depth + 1, grouping, ref usesRoot, ref maxUnionProduct);
            Require(path, ref index, ")");
            return;
        }

        var left = ReadOperand(path, ref index, ref usesRoot, ref maxUnionProduct);
        if (Take(path, ref index, "==") || Take(path, ref index, "!=") ||
            Take(path, ref index, "<=") || Take(path, ref index, ">=") ||
            Take(path, ref index, "<") || Take(path, ref index, ">"))
        {
            var right = ReadOperand(path, ref index, ref usesRoot, ref maxUnionProduct);
            if (!left.Comparable || !right.Comparable)
                throw Invalid();
        }
        else if (!left.Query)
            throw Invalid();
    }

    // A query may be tested for existence; comparisons require a singular
    // query or a value. The pinned parser still validates full RFC syntax.
    private static (bool Query, bool Comparable) ReadOperand(string path, ref int index, ref bool usesRoot, ref int maxUnionProduct)
    {
        SkipWhitespace(path, ref index);
        if (index >= path.Length)
            throw Invalid();
        if (path[index] is '@' or '$')
        {
            var root = path[index++] == '$';
            usesRoot |= root;
            var singular = ReadSegments(path, ref index, ref maxUnionProduct);
            return (true, singular);
        }
        // These built-ins have bounded work on a nodelist without recursion.
        // Root queries receive the separate repeated-root charge.
        // Do not admit dynamic/regex or custom functions.
        if (Take(path, ref index, "count") || Take(path, ref index, "value"))
        {
            Require(path, ref index, "(");
            SkipWhitespace(path, ref index);
            if (index >= path.Length || path[index] is not ('@' or '$'))
                throw Invalid();
            usesRoot |= path[index++] == '$';
            ReadSegments(path, ref index, ref maxUnionProduct);
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

    private static bool ReadSegments(string path, ref int index, ref int maxUnionProduct)
    {
        var singular = true;
        var unionProduct = 1;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
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
