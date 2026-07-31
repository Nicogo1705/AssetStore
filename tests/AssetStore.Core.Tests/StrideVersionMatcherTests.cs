// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;

namespace AssetStore.Core.Tests;

public sealed class StrideVersionMatcherTests
{
    [Theory]
    [InlineData("4.2.0.1", "4.2.0.1", StrideMatch.Exact, true)]
    [InlineData("4.2.0.1", "4.2.1.0", StrideMatch.Exact, false)]
    [InlineData("4.2.0.1", "4.2.9.9", StrideMatch.Minor, true)]
    [InlineData("4.1.0.0", "4.2.0.0", StrideMatch.Minor, false)]
    [InlineData("4.1.0.0", "4.2.0.0", StrideMatch.Any, true)]
    [InlineData("4.2.0.1", "4.1.0.0", StrideMatch.AtLeast, true)]
    [InlineData("4.2.0.1", "4.2.0.0", StrideMatch.AtLeast, true)]
    [InlineData("4.1.0.0", "4.2.0.0", StrideMatch.AtLeast, false)]
    [InlineData("5.0.0.0", "4.2.0.0", StrideMatch.AtLeast, true)]
    [InlineData("4.9.0.0", "4.2.0.0", StrideMatch.MajorOnly, true)]
    [InlineData("5.0.0.0", "4.2.0.0", StrideMatch.MajorOnly, false)]
    [InlineData("4.4.0-beta4", "4.4.0", StrideMatch.Exact, true)]        // suffix ignored
    [InlineData("4.4.0-beta4", "4.4.0-beta4", StrideMatch.ExactString, true)]
    [InlineData("v4.4.0-beta4", "4.4.0-BETA4", StrideMatch.ExactString, true)] // 'v' + case tolerated
    [InlineData("4.4.0-beta4", "4.4.0-beta2", StrideMatch.ExactString, false)]
    [InlineData("4.4.0-beta4", "4.4.0", StrideMatch.ExactString, false)]
    public void Matches_as_expected(string asset, string target, StrideMatch mode, bool expected) =>
        Assert.Equal(expected, StrideVersionMatcher.IsCompatible(asset, target, mode));

    [Fact]
    public void Unknown_asset_version_is_compatible_only_under_loose_modes()
    {
        Assert.True(StrideVersionMatcher.IsCompatible(null, "4.2.0.0", StrideMatch.Minor));
        Assert.True(StrideVersionMatcher.IsCompatible(null, "4.2.0.0", StrideMatch.MajorOnly));
        Assert.False(StrideVersionMatcher.IsCompatible(null, "4.2.0.0", StrideMatch.Exact));
        Assert.False(StrideVersionMatcher.IsCompatible(null, "4.2.0.0", StrideMatch.ExactString));
    }
}
