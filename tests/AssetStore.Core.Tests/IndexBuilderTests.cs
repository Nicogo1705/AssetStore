// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Indexing;
using AssetStore.Core.Validation;

namespace AssetStore.Core.Tests;

/// <summary>
/// End-to-end indexing check against a synthetic workspace (git-backed asset repos we generate on the fly),
/// so it depends on no published repository — including a deliberately broken asset and a dependency pair.
/// </summary>
public sealed class IndexBuilderTests
{
    [Fact]
    public void Builds_index_with_valid_broken_and_dependent_assets()
    {
        using var ws = SyntheticWorkspace.TryCreate();
        if (ws is null || !TestPaths.Available)
        {
            return; // needs git + the sibling AssetContainer (schemas/catalog for validation)
        }

        ws.AddAsset("Widget", SyntheticWorkspace.Manifest("com.test.widget", "Widget", "Scripts"), SyntheticWorkspace.Csproj());
        ws.AddAsset("Gadget", SyntheticWorkspace.Manifest("com.test.gadget", "Gadget", "Scripts", deps: ["com.test.widget"]), SyntheticWorkspace.Csproj(), projectReferenceToRepo: "Widget");
        ws.AddAsset("Busted", SyntheticWorkspace.Manifest("com.test.busted", "Busted", "NotACategory"), SyntheticWorkspace.Csproj());

        var validator = AssetValidator.FromContainer(TestPaths.Container);
        var index = new IndexBuilder(ws.Container, new LocalAssetSource(ws.Root), validator).Build("2026-01-01T00:00:00Z");

        var widget = index.Assets.Single(a => a.Id == "com.test.widget");
        Assert.Equal("ok", widget.ValidationStatus);
        Assert.Equal("4.2.0.1", widget.Latest.DetectedStrideVersion);
        Assert.Equal("net8.0", widget.Latest.TargetFramework);
        Assert.Contains(widget.Latest.ExternalDependencies, p => p.Name == "Stride.Engine");
        Assert.Matches("^[0-9a-f]{40}$", widget.Latest.Commit);
        Assert.NotEmpty(widget.Latest.ContentHash);

        // ProjectReference to Widget is resolved into a store dependency.
        var gadget = index.Assets.Single(a => a.Id == "com.test.gadget");
        Assert.Equal("ok", gadget.ValidationStatus);
        Assert.Contains("com.test.widget", gadget.Latest.ResolvedDependencies);

        // The generated invalid asset is flagged, not silently accepted.
        var busted = index.Assets.Single(a => a.Id == "com.test.busted");
        Assert.NotEqual("ok", busted.ValidationStatus);
    }
}
