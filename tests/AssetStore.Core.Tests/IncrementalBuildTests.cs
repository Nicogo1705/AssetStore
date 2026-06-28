// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Indexing;
using AssetStore.Core.Models;
using AssetStore.Core.Validation;
using static AssetStore.Core.Tests.CatalogTestData;

namespace AssetStore.Core.Tests;

public sealed class IncrementalBuildTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Unchanged_assets_are_reused_without_fetching()
    {
        if (!TestPaths.Available)
        {
            return;
        }

        // Previous index pins both example assets at Sha; headProvider reports the same Sha => no change.
        var previous = Index(
            ExampleAsset("com.example.math-utils"),
            ExampleAsset("com.example.shader-pack"),
            ExampleAsset("com.example.broken")); // every registry entry already known -> nothing re-fetched

        var source = new ThrowingSource();
        var builder = new IndexBuilder(
            TestPaths.Container,
            source,
            AssetValidator.FromContainer(TestPaths.Container),
            headProvider: (_, _) => Sha);

        var index = builder.BuildIncremental(previous, "2026-01-01T00:00:00Z");

        Assert.Equal(0, source.FetchCount); // nothing re-fetched
        Assert.Contains(index.Assets, a => a.Id == "com.example.math-utils");
        Assert.Contains(index.Assets, a => a.Id == "com.example.shader-pack");
        Assert.All(index.Assets, a => Assert.Equal("2026-01-01T00:00:00Z", a.LastValidatedAt));
    }

    [Fact]
    public void Changed_asset_is_reprocessed()
    {
        if (!TestPaths.Available)
        {
            return;
        }

        var previous = Index(
            ExampleAsset("com.example.math-utils"),
            ExampleAsset("com.example.shader-pack") with
            {
                Latest = ExampleAsset("com.example.shader-pack").Latest with { DetectedStrideVersion = null },
            });

        // math unchanged (Sha), shader moved (different head) => only shader is reprocessed.
        string Head(string repo, string _) => repo.EndsWith("ShaderPack", StringComparison.Ordinal) ? "ffffffffffffffffffffffffffffffffffffffff" : Sha;

        var builder = new IndexBuilder(
            TestPaths.Container,
            new LocalAssetSource(TestPaths.Workspace!),
            AssetValidator.FromContainer(TestPaths.Container),
            headProvider: Head);

        var index = builder.BuildIncremental(previous, "2026-01-01T00:00:00Z");

        var shader = index.Assets.Single(a => a.Id == "com.example.shader-pack");
        Assert.Equal("4.2.0.1", shader.Latest.DetectedStrideVersion); // recomputed from the .csproj
        Assert.Contains("com.example.math-utils", shader.Latest.ResolvedDependencies);
    }

    private static IndexedAsset ExampleAsset(string id) => Asset(id, id, "Scripts") with
    {
        Repo = id.EndsWith("shader-pack", StringComparison.Ordinal)
            ? "https://github.com/Nicogo1705/ExampleAsset.ShaderPack"
            : "https://github.com/Nicogo1705/ExampleAsset.MathUtils",
        Latest = Asset(id, id, "Scripts").Latest with { Commit = Sha },
    };

    private sealed class ThrowingSource : IAssetSource
    {
        public int FetchCount { get; private set; }

        public AssetCheckout Fetch(RegistryEntry entry)
        {
            FetchCount++;
            throw new InvalidOperationException("should not be fetched");
        }
    }
}
