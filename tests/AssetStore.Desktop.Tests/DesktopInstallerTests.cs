// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Projects;
using AssetStore.Desktop.Services;

namespace AssetStore.Desktop.Tests;

/// <summary>
/// End-to-end tests of <see cref="DesktopInstaller"/> over a synthetic machine
/// (<see cref="InstallerWorkspace"/>): real git clones, real csproj/sln files, the global cache
/// redirected into temp — no network. All in ONE class: the cache root comes from process-wide
/// environment variables, so these tests must not run concurrently with each other (xunit runs
/// tests of a single class sequentially).
/// </summary>
public sealed class DesktopInstallerTests
{
    private const string MsBuildMarker = "$([System.Environment]::GetFolderPath(SpecialFolder.ApplicationData))";

    [Fact]
    public void ListCachedAssets_reads_both_layouts_and_computes_statuses()
    {
        using var ws = new InstallerWorkspace();

        // Versioned clone at HEAD == certified pin → up-to-date.
        var (_, pinnedHead) = ws.CreateAssetClone(
            Path.Combine("appdata", "StrideAssetStore", "Assets", "v1.0.0", "PinnedAsset"), "com.t.pinned", "PinnedAsset");
        // Legacy flat clone whose catalog latest moved on → outdated.
        var (_, _) = ws.CreateAssetClone(
            Path.Combine("appdata", "StrideAssetStore", "Assets", "LegacyAsset"), "com.t.legacy", "LegacyAsset");
        // Clone whose id the catalog doesn't know → unknown.
        ws.CreateAssetClone(
            Path.Combine("appdata", "StrideAssetStore", "Assets", "StrayAsset"), "com.t.stray", "StrayAsset");
        // AssetData folder without a manifest → broken.
        Directory.CreateDirectory(Path.Combine(ws.CacheRoot, "BrokenAsset", "AssetData"));
        // An empty ref folder must be skipped, not listed.
        Directory.CreateDirectory(Path.Combine(ws.CacheRoot, "v9.9.9"));

        var catalog = InstallerWorkspace.Catalog(
            InstallerWorkspace.CatalogEntry("com.t.pinned", "PinnedAsset",
                latestCommit: new string('a', 40), certifiedTag: "v1.0.0", certifiedCommit: pinnedHead),
            InstallerWorkspace.CatalogEntry("com.t.legacy", "LegacyAsset", latestCommit: new string('b', 40)));

        var cached = new DesktopInstaller().ListCachedAssets(catalog);

        Assert.Equal(4, cached.Count);
        var pinned = Assert.Single(cached, c => c.Id == "com.t.pinned");
        Assert.Equal("v1.0.0", pinned.Ref);
        Assert.Equal("up-to-date", pinned.Status);
        Assert.True(pinned.SizeBytes > 0);

        var legacy = Assert.Single(cached, c => c.Id == "com.t.legacy");
        Assert.Equal("", legacy.Ref);
        Assert.Equal("outdated", legacy.Status);

        Assert.Equal("unknown", Assert.Single(cached, c => c.Id == "com.t.stray").Status);
        Assert.Equal("broken", Assert.Single(cached, c => c.Name == "BrokenAsset").Status);
    }

    [Fact]
    public void Analyze_reports_local_assets_stride_versions_and_dependencies()
    {
        using var ws = new InstallerWorkspace();
        var (cloneRoot, head) = ws.CreateAssetClone(Path.Combine("clones", "TestAsset"), "com.t.asset", "TestAsset");
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"), strideVersion: "4.5.0.1");
        CsprojEditor.AddProjectReference(gameCsproj,
            Path.Combine(cloneRoot, "AssetData", "TestAsset", "TestAsset.csproj"));

        var catalog = InstallerWorkspace.Catalog(
            InstallerWorkspace.CatalogEntry("com.t.asset", "TestAsset", latestCommit: head));

        var view = new DesktopInstaller().Analyze(gameCsproj, catalog);

        var node = Assert.Single(view.Projects);
        Assert.Equal("Game", node.Name);
        Assert.Equal("4.5.0.1", node.StrideVersion);

        var asset = Assert.Single(node.Assets);
        Assert.Equal("com.t.asset", asset.Id);
        Assert.Equal("local", asset.Kind);
        Assert.Equal("up-to-date", asset.Status);
        Assert.Equal(head, asset.InstalledCommit);
        Assert.Equal(cloneRoot, asset.CloneRoot);
        Assert.Equal("4.4.0.2", asset.StrideVersion); // what the installed clone targets, not the game

        // Non-asset deps get their own line; the store asset's ProjectReference must NOT reappear there.
        Assert.NotNull(node.Dependencies);
        Assert.Contains(node.Dependencies!, d => d.Name == "Newtonsoft.Json" && d.Version == "13.0.3");
        Assert.DoesNotContain(node.Dependencies!, d => d.Name == "TestAsset");
    }

    [Fact]
    public void Analyze_surfaces_not_downloaded_global_cache_references_as_missing()
    {
        using var ws = new InstallerWorkspace();
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"));
        CsprojEditor.AddRawProjectReference(gameCsproj,
            $@"{MsBuildMarker}\StrideAssetStore\Assets\master\TestAsset\AssetData\TestAsset\TestAsset.csproj");

        var catalog = InstallerWorkspace.Catalog(
            InstallerWorkspace.CatalogEntry("com.t.asset", "TestAsset", latestCommit: new string('c', 40)));

        var asset = Assert.Single(Assert.Single(new DesktopInstaller().Analyze(gameCsproj, catalog).Projects).Assets);
        Assert.Equal("missing", asset.Status);
        Assert.Equal("com.t.asset", asset.Id); // mapped back to the catalog via the repo folder name
        Assert.Equal("master", asset.Ref);
    }

    [Fact]
    public void ReadTargets_excludes_store_asset_projects()
    {
        using var ws = new InstallerWorkspace();
        var (cloneRoot, _) = ws.CreateAssetClone(Path.Combine("clones", "TestAsset"), "com.t.asset", "TestAsset");
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"));
        var sln = ws.CreateSolution("Game.sln",
            gameCsproj, Path.Combine(cloneRoot, "AssetData", "TestAsset", "TestAsset.csproj"));

        var targets = new DesktopInstaller().ReadTargets(sln);

        Assert.Equal("Game", Assert.Single(targets).Name);
    }

    [Fact]
    public void ListDanglingStoreProjects_reports_deleted_cache_clones_with_their_asset_id()
    {
        using var ws = new InstallerWorkspace();
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"));
        // A .sln entry pointing into the global cache whose files were deleted.
        var goneCsproj = Path.Combine(ws.CacheRoot, "master", "TestAsset", "AssetData", "TestAsset", "TestAsset.csproj");
        var sln = ws.CreateSolution("Game.sln", gameCsproj, goneCsproj);

        var catalog = InstallerWorkspace.Catalog(
            InstallerWorkspace.CatalogEntry("com.t.asset", "TestAsset", latestCommit: new string('d', 40)));

        var dangling = Assert.Single(new DesktopInstaller().ListDanglingStoreProjects(sln, catalog));
        Assert.Equal("com.t.asset", dangling.AssetId);
        Assert.Equal(goneCsproj, dangling.CsprojPath);
    }

    [Fact]
    public void AttachCached_writes_a_portable_include_for_global_cache_clones()
    {
        using var ws = new InstallerWorkspace();
        ws.CreateAssetClone(
            Path.Combine("appdata", "StrideAssetStore", "Assets", "master", "TestAsset"), "com.t.asset", "TestAsset");
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"));
        var cloneRoot = Path.Combine(ws.CacheRoot, "master", "TestAsset");
        var installer = new DesktopInstaller();

        var result = installer.AttachCached(cloneRoot, [gameCsproj], InstallerWorkspace.Catalog());

        Assert.True(result.Success);
        var csproj = File.ReadAllText(gameCsproj);
        Assert.Contains(MsBuildMarker, csproj); // portable on any machine, any OS
        Assert.DoesNotContain(ws.CacheRoot, csproj); // never the machine-local absolute path

        // Idempotent: attaching again must not duplicate the reference.
        var again = installer.AttachCached(cloneRoot, [gameCsproj], InstallerWorkspace.Catalog());
        Assert.True(again.Success);
        Assert.Contains(again.Messages, m => m.Contains("already references"));
    }

    [Fact]
    public void AttachCached_writes_a_relative_include_outside_the_cache_and_UninstallLocal_removes_it()
    {
        using var ws = new InstallerWorkspace();
        var (cloneRoot, head) = ws.CreateAssetClone(Path.Combine("clones", "TestAsset"), "com.t.asset", "TestAsset");
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"));
        var installer = new DesktopInstaller();
        var catalog = InstallerWorkspace.Catalog(
            InstallerWorkspace.CatalogEntry("com.t.asset", "TestAsset", latestCommit: head));

        Assert.True(installer.AttachCached(cloneRoot, [gameCsproj], catalog).Success);
        var csproj = File.ReadAllText(gameCsproj);
        Assert.Contains(@"..\clones\TestAsset\AssetData\TestAsset\TestAsset.csproj", csproj);

        var installed = Assert.Single(Assert.Single(installer.Analyze(gameCsproj, catalog).Projects).Assets);
        Assert.True(installer.UninstallLocal(gameCsproj, installed.RawInclude));
        Assert.Empty(Assert.Single(installer.Analyze(gameCsproj, catalog).Projects).Assets);
    }

    [Fact]
    public void SwitchRef_swaps_the_reference_to_an_already_cached_ref_without_network()
    {
        using var ws = new InstallerWorkspace();
        var (masterRoot, masterHead) = ws.CreateAssetClone(
            Path.Combine("appdata", "StrideAssetStore", "Assets", "master", "TestAsset"), "com.t.asset", "TestAsset");
        var (_, taggedHead) = ws.CreateAssetClone(
            Path.Combine("appdata", "StrideAssetStore", "Assets", "v1.0.0", "TestAsset"), "com.t.asset", "TestAsset");
        var gameCsproj = ws.CreateGameProject(Path.Combine("Game", "Game.csproj"));
        var installer = new DesktopInstaller();
        var entry = InstallerWorkspace.CatalogEntry("com.t.asset", "TestAsset",
            latestCommit: masterHead, certifiedTag: "v1.0.0", certifiedCommit: taggedHead);
        var catalog = InstallerWorkspace.Catalog(entry);

        Assert.True(installer.AttachCached(masterRoot, [gameCsproj], catalog).Success);
        var current = Assert.Single(Assert.Single(installer.Analyze(gameCsproj, catalog).Projects).Assets);
        Assert.Equal("master", current.Ref);

        var switched = installer.SwitchRef(entry, current, gameCsproj, "v1.0.0", catalog, solutionPath: null);

        Assert.True(switched.Success);
        var after = Assert.Single(Assert.Single(installer.Analyze(gameCsproj, catalog).Projects).Assets);
        Assert.Equal("v1.0.0", after.Ref);
        Assert.Equal(taggedHead, after.InstalledCommit);
        Assert.Equal("up-to-date", after.Status); // judged against the v1.0.0 pin, not the moving latest
        Assert.DoesNotContain(@"\master\", File.ReadAllText(gameCsproj));
    }
}
