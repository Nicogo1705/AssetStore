// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Indexing;
using AssetStore.Core.Validation;

namespace AssetStore.Core.Tests;

/// <summary>End-to-end check against the AssetContainer and ExampleAsset.* repositories.</summary>
public sealed class IndexBuilderTests
{
    [Fact]
    public void Builds_index_from_example_assets()
    {
        if (!TestPaths.Available)
        {
            return;
        }

        var index = BuildIndex();

        var math = index.Assets.Single(a => a.Id == "com.example.math-utils");
        Assert.Equal("ok", math.ValidationStatus);
        Assert.Equal("4.2.0.1", math.Latest.DetectedStrideVersion);
        Assert.Empty(math.Latest.ResolvedDependencies);

        var shader = index.Assets.Single(a => a.Id == "com.example.shader-pack");
        Assert.Equal("ok", shader.ValidationStatus);
        Assert.Contains("com.example.math-utils", shader.Latest.ResolvedDependencies);
        Assert.Matches("^[0-9a-f]{40}$", shader.Latest.Commit);
        Assert.NotEmpty(shader.Latest.ContentHash);
        Assert.Single(shader.Certified); // certified[] is now mapped into the index

        // The intentionally-invalid example is flagged, not silently accepted.
        var broken = index.Assets.SingleOrDefault(a => a.Id == "com.example.broken");
        if (broken is not null)
        {
            Assert.NotEqual("ok", broken.ValidationStatus);
        }
    }

    private static Core.Models.IndexLock BuildIndex()
    {
        var validator = AssetValidator.FromContainer(TestPaths.Container);
        var source = new LocalAssetSource(TestPaths.Workspace!);
        return new IndexBuilder(TestPaths.Container, source, validator).Build("2026-01-01T00:00:00Z");
    }
}
