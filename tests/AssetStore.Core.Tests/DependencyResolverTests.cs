// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Dependencies;

namespace AssetStore.Core.Tests;

public sealed class DependencyResolverTests
{
    private static Dictionary<string, IReadOnlyList<string>> Graph(params (string Id, string[] Deps)[] nodes) =>
        nodes.ToDictionary(n => n.Id, n => (IReadOnlyList<string>)n.Deps, StringComparer.Ordinal);

    [Fact]
    public void Resolves_transitive_dependencies()
    {
        var graph = Graph(("a", ["b"]), ("b", ["c"]), ("c", []));

        var result = DependencyResolver.Resolve("a", graph);

        Assert.False(result.HasCycle);
        Assert.False(result.HasMissing);
        Assert.Equal(["b", "c"], result.Dependencies.OrderBy(x => x));
    }

    [Fact]
    public void Detects_cycles()
    {
        var graph = Graph(("a", ["b"]), ("b", ["a"]));

        var result = DependencyResolver.Resolve("a", graph);

        Assert.True(result.HasCycle);
    }

    [Fact]
    public void Detects_cycle_not_involving_the_root()
    {
        var graph = Graph(("a", ["b"]), ("b", ["c"]), ("c", ["b"]));

        var result = DependencyResolver.Resolve("a", graph);

        Assert.True(result.HasCycle);
    }

    [Fact]
    public void Reports_missing_dependencies()
    {
        var graph = Graph(("a", ["ghost"]));

        var result = DependencyResolver.Resolve("a", graph);

        Assert.True(result.HasMissing);
        Assert.Contains("ghost", result.Missing);
    }
}
