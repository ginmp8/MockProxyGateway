using System.Text.RegularExpressions;
using MockProxyGateway.Configuration;
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

    [Fact]
    public void ActualEngineTimeoutDuringConstantEvaluationRejectsConfiguration()
    {
        var pattern = new BoundedRegexFilterSelector.CompiledPattern(TimeoutPattern(), null);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            pattern.IsMatchAtCompilation(new string('a', 4096) + "!"));
        Assert.Equal("JSONPath constant regex evaluation timed out.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrailingClassHyphenCountsTowardsMemberLimit(bool withCategory)
    {
        var prefix = withCategory ? @"\p{L}" : "a";
        var valid = "[" + prefix + new string('b', 30) + "-]";
        Assert.True(BoundedRegexFilterSelector.Compile(valid, true).IsMatch("-"));
        var invalid = "[" + prefix + new string('b', 31) + "-]";
        Assert.Throws<InvalidOperationException>(() => BoundedRegexFilterSelector.Compile(invalid, true));
    }

    [GeneratedRegex("^(a+)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1)]
    private static partial Regex TimeoutPattern();
}
