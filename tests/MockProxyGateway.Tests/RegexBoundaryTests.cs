using System.Text.RegularExpressions;
using MockProxyGateway.Matching;
using Xunit;

namespace MockProxyGateway.Tests;

public sealed partial class RegexBoundaryTests
{
    [Fact]
    public void ActualEngineTimeoutIsClassifiedAsInspectionFailure()
    {
        // This intentionally expensive backtracking pattern is not permitted as a
        // backtracking production evaluator. It forces the actual engine's timeout
        // so the shared exception boundary is tested without a fake exception.
        var failure = Assert.Throws<RequestFailure>(() => RuleResolver.MatchRegex(
            TimeoutPattern(), new string('a', 4096) + "!"));
        Assert.Equal(400, failure.StatusCode);
        Assert.Equal("regex-timeout", failure.Category);
    }

    [GeneratedRegex("^(a+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1)]
    private static partial Regex TimeoutPattern();
}
