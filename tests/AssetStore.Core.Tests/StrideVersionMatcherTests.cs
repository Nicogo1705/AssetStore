// Copyright (c) Stride contributors (https://stride3d.net)
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
    public void Matches_as_expected(string asset, string target, StrideMatch mode, bool expected) =>
        Assert.Equal(expected, StrideVersionMatcher.IsCompatible(asset, target, mode));

    [Fact]
    public void Unknown_asset_version_is_compatible_unless_exact()
    {
        Assert.True(StrideVersionMatcher.IsCompatible(null, "4.2.0.0", StrideMatch.Minor));
        Assert.False(StrideVersionMatcher.IsCompatible(null, "4.2.0.0", StrideMatch.Exact));
    }
}
