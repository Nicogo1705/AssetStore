// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;
using AssetStore.Core.Models;
using static AssetStore.Core.Tests.CatalogTestData;

namespace AssetStore.Core.Tests;

public sealed class AssetCatalogTests
{
    private readonly AssetCatalog _catalog = new(Index(
        Asset("com.a.water", "Water Shader", "Shaders", ["water", "pbr"]),
        Asset("com.a.fire", "Fire VFX", "VFX", ["fire"], certified: true),
        Asset("com.a.math", "Math Utils", "Scripts", ["math"], strideVersion: "4.1.0.0")));

    [Fact]
    public void Filters_by_category()
    {
        var result = _catalog.Query(new CatalogQuery { Category = "Shaders" });

        Assert.Single(result);
        Assert.Equal("com.a.water", result[0].Id);
    }

    [Fact]
    public void Filters_by_tag()
    {
        var result = _catalog.Query(new CatalogQuery { Tags = ["water"] });

        Assert.Single(result);
        Assert.Equal("com.a.water", result[0].Id);
    }

    [Fact]
    public void Free_text_searches_name_id_and_tags()
    {
        Assert.Single(_catalog.Query(new CatalogQuery { Text = "fire" }));  // name
        Assert.Single(_catalog.Query(new CatalogQuery { Text = "MATH" }));  // name (case-insensitive)
        Assert.Single(_catalog.Query(new CatalogQuery { Text = "pbr" }));   // tag, water shader only
    }

    [Fact]
    public void Description_search_can_be_disabled()
    {
        // Test descriptions are "<name> description"; the word only appears in descriptions.
        Assert.Equal(3, _catalog.Query(new CatalogQuery { Text = "description", SearchDescription = true }).Count);
        Assert.Empty(_catalog.Query(new CatalogQuery { Text = "description", SearchDescription = false }));
    }

    [Fact]
    public void Ranks_name_matches_above_description_matches()
    {
        var catalog = new AssetCatalog(Index(
            Asset("com.x.helper", "Helper", "Tools", description: "uses water internally"),
            Asset("com.x.water", "Water Tool", "Tools")));

        var result = catalog.Query(new CatalogQuery { Text = "water" });

        Assert.Equal(["com.x.water", "com.x.helper"], result.Select(a => a.Id));
    }

    [Fact]
    public void Certified_only_filters()
    {
        var result = _catalog.Query(new CatalogQuery { CertifiedOnly = true });

        Assert.Single(result);
        Assert.Equal("com.a.fire", result[0].Id);
    }

    [Fact]
    public void Stride_minor_match_excludes_other_minor()
    {
        var result = _catalog.Query(new CatalogQuery { StrideVersion = "4.2.0.1", StrideMatch = StrideMatch.Minor });

        Assert.DoesNotContain(result, a => a.Id == "com.a.math"); // 4.1.x
        Assert.Contains(result, a => a.Id == "com.a.water");      // 4.2.x
    }

    [Fact]
    public void Sorts_by_name_ascending_by_default()
    {
        var names = _catalog.Query(new CatalogQuery()).Select(a => a.Manifest.Name).ToList();

        Assert.Equal(["Fire VFX", "Math Utils", "Water Shader"], names);
    }

    [Fact]
    public void Exposes_distinct_categories_and_tags()
    {
        Assert.Equal(["Scripts", "Shaders", "VFX"], _catalog.Categories);
        Assert.Equal(["fire", "math", "pbr", "water"], _catalog.Tags);
    }

    [Fact]
    public void Exposes_distinct_stride_versions_newest_first()
    {
        Assert.Equal(["4.2", "4.1"], _catalog.StrideVersions);
    }

    [Fact]
    public void Stride_at_least_filters_older_versions()
    {
        var result = _catalog.Query(new CatalogQuery { StrideVersion = "4.2", StrideMatch = StrideMatch.AtLeast });

        Assert.DoesNotContain(result, a => a.Id == "com.a.math"); // 4.1
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Sorts_by_stars_descending()
    {
        var catalog = new AssetCatalog(Index(
            StarredAsset("com.s.low", "Low", 3),
            StarredAsset("com.s.high", "High", 99),
            StarredAsset("com.s.mid", "Mid", 42)));

        var ids = catalog.Query(new CatalogQuery { SortBy = CatalogSort.Stars }).Select(a => a.Id);

        Assert.Equal(["com.s.high", "com.s.mid", "com.s.low"], ids);
    }

    private static IndexedAsset StarredAsset(string id, string name, int stars) =>
        Asset(id, name, "Tools") with { Stars = stars };
}
