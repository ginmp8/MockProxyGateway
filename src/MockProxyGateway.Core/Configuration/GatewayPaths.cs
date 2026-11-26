using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using MockProxyGateway.Matching;

namespace MockProxyGateway.Configuration;

internal static partial class GatewayPaths
{
    internal static bool IsSafe(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains("//", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains('?') || path.Contains('#') || path.Any(char.IsControl))
            return false;
        foreach (var part in path.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(part);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\') ||
                decoded.Contains('%') || decoded.Any(char.IsControl))
                return false;
        }
        return true;
    }

    internal static bool StartsWithSegment(string value, string prefix) =>
        value.Equals(prefix, StringComparison.Ordinal) || value.StartsWith(prefix + "/", StringComparison.Ordinal);

    internal static bool IsEscapedTargetPart(string value, bool query)
    {
        // Validate URI wire syntax before bypassing URI path/query normalization.
        // Percent escapes are preserved, but must be well formed.
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%')
            {
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                    return false;
                i += 2;
            }
            else if (!(char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@/".Contains(c) || (query && c == '?')))
                return false;
        }
        return true;
    }

    internal static string GetForwardPath(string? rawPath, string prefix, string decodedPath)
    {
        if (string.IsNullOrEmpty(rawPath))
            return new PathString(decodedPath).ToUriComponent();
        var prefixEnd = prefix.Length;
        if (!StartsWithSegment(rawPath, prefix))
        {
            // Kestrel decodes safe percent escapes in the routing prefix and
            // service alias. Find the corresponding raw segment boundary;
            // never reconstruct or normalize the remaining wire path.
            prefixEnd = 0;
            foreach (var c in prefix)
            {
                if (c != '/')
                    continue;
                prefixEnd = rawPath.IndexOf('/', prefixEnd + 1);
                if (prefixEnd < 0)
                {
                    prefixEnd = rawPath.Length;
                    break;
                }
            }
            if (!string.Equals(PathString.FromUriComponent(rawPath[..prefixEnd]).Value,
                PathString.FromUriComponent(prefix).Value, StringComparison.Ordinal))
                throw new RequestFailure(400);
        }
        var remainder = rawPath.Length == prefixEnd ? "/" : rawPath[prefixEnd..];
        if (!IsSafe(remainder) || !IsEscapedTargetPart(remainder, query: false) ||
            !string.Equals(PathString.FromUriComponent(remainder).Value, decodedPath, StringComparison.Ordinal))
            throw new RequestFailure(400);
        return remainder;
    }

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\\z", RegexOptions.CultureInvariant)]
    internal static partial Regex Identifier();
    [GeneratedRegex("\\A[!#$%&'*+.^_\u0060|~0-9A-Za-z-]+\\z", RegexOptions.CultureInvariant)]
    internal static partial Regex HttpToken();
}

internal sealed class RoutePattern
{
    private readonly string[] _segments;
    private readonly int[] _ranks;
    internal string Shape { get; }
    internal int Kind { get; }

    internal RoutePattern(string pattern)
    {
        if (!GatewayPaths.IsSafe(pattern))
            throw new InvalidOperationException("Invalid endpoint path.");
        _segments = Split(pattern);
        _ranks = new int[_segments.Length];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var shape = new string[_segments.Length];
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            if (segment.StartsWith('{') && segment.EndsWith('}'))
            {
                var catchAll = segment.StartsWith("{**", StringComparison.Ordinal);
                var name = catchAll ? segment[3..^1] : segment[1..^1];
                if (!GatewayPaths.Identifier().IsMatch(name) || !names.Add(name) || (catchAll && i != _segments.Length - 1))
                    throw new InvalidOperationException("Invalid route parameter.");
                _ranks[i] = catchAll ? 0 : 1;
                shape[i] = catchAll ? "{**}" : "{}";
            }
            else
            {
                if (segment.Contains('{') || segment.Contains('}') || segment.Contains('%'))
                    throw new InvalidOperationException("Invalid literal route segment.");
                _ranks[i] = 2;
                shape[i] = segment;
            }
        }
        Kind = _ranks.Contains(0) ? 0 : _ranks.Contains(1) ? 1 : 2;
        Shape = "/" + string.Join('/', shape);
    }

    internal Dictionary<string, string>? Match(string path)
    {
        var parts = Split(path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < _segments.Length; i++)
        {
            if (_ranks[i] == 0)
            {
                result.Add(_segments[i][3..^1], string.Join('/', parts.Skip(i)));
                return result;
            }
            if (i >= parts.Length)
                return null;
            if (_ranks[i] == 1)
                result.Add(_segments[i][1..^1], parts[i]);
            else if (!string.Equals(_segments[i], parts[i], StringComparison.Ordinal))
                return null;
        }
        return parts.Length == _segments.Length ? result : null;
    }

    internal int CompareSpecificity(RoutePattern other)
    {
        var comparison = Kind.CompareTo(other.Kind);
        if (comparison != 0)
            return comparison;
        for (var i = 0; i < Math.Min(_ranks.Length, other._ranks.Length); i++)
        {
            comparison = _ranks[i].CompareTo(other._ranks[i]);
            if (comparison != 0)
                return comparison;
        }
        return _ranks.Length.CompareTo(other._ranks.Length);
    }

    private static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
