// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Indexing;
using AssetStore.Core.Models;
using AssetStore.Core.Validation;

namespace AssetStore.Core.Tests;

/// <summary>Incremental indexing against a synthetic workspace — no dependency on any published repository.</summary>
public sealed class IncrementalBuildTests
{
    [Fact]
    public void Unchanged_assets_are_reused_without_fetching()
    {
        using var ws = SyntheticWorkspace.TryCreate();
        if (ws is null || !TestPaths.Available)
        {
            return;
        }

        ws.AddAsset("Widget", SyntheticWorkspace.Manifest("com.test.widget", "Widget", "Scripts"), SyntheticWorkspace.Csproj());
        ws.AddAsset("Gadget", SyntheticWorkspace.Manifest("com.test.gadget", "Gadget", "Scripts", deps: ["com.test.widget"]), SyntheticWorkspace.Csproj(), projectReferenceToRepo: "Widget");

        var validator = AssetValidator.FromContainer(TestPaths.Container);
        var previous = new IndexBuilder(ws.Container, new LocalAssetSource(ws.Root), validator).Build("2026-01-01T00:00:00Z");
        var heads = previous.Assets.ToDictionary(a => a.Repo, a => a.Latest.Commit, StringComparer.Ordinal);

        // Every ref reports its previous commit => nothing changed => nothing is fetched.
        var source = new ThrowingSource();
        var builder = new IndexBuilder(ws.Container, source, validator, headProvider: (repo, _) => heads[repo]);
        var index = builder.BuildIncremental(previous, "2026-02-02T00:00:00Z");

        Assert.Equal(0, source.FetchCount);
        Assert.Equal(previous.Assets.Count, index.Assets.Count);
        Assert.All(index.Assets, a => Assert.Equal("2026-02-02T00:00:00Z", a.LastValidatedAt));
    }

    [Fact]
    public void Changed_asset_is_reprocessed()
    {
        using var ws = SyntheticWorkspace.TryCreate();
        if (ws is null || !TestPaths.Available)
        {
            return;
        }

        ws.AddAsset("Widget", SyntheticWorkspace.Manifest("com.test.widget", "Widget", "Scripts"), SyntheticWorkspace.Csproj());
        ws.AddAsset("Gadget", SyntheticWorkspace.Manifest("com.test.gadget", "Gadget", "Scripts", deps: ["com.test.widget"]), SyntheticWorkspace.Csproj(), projectReferenceToRepo: "Widget");

        var validator = AssetValidator.FromContainer(TestPaths.Container);
        var previous = new IndexBuilder(ws.Container, new LocalAssetSource(ws.Root), validator).Build("2026-01-01T00:00:00Z");
        var heads = previous.Assets.ToDictionary(a => a.Repo, a => a.Latest.Commit, StringComparer.Ordinal);

        // Widget's ref reports a new commit => it is re-fetched and re-indexed (its dependency re-resolved).
        string Head(string repo, string _) =>
            repo.EndsWith("Widget", StringComparison.Ordinal) ? new string('f', 40) : heads[repo];

        var builder = new IndexBuilder(ws.Container, new LocalAssetSource(ws.Root), validator, headProvider: Head);
        var index = builder.BuildIncremental(previous, "2026-02-02T00:00:00Z");

        var gadget = index.Assets.Single(a => a.Id == "com.test.gadget");
        Assert.Contains("com.test.widget", gadget.Latest.ResolvedDependencies);
    }

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
