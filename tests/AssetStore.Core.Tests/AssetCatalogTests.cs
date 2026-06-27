// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;
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
    public void Free_text_searches_name_and_description()
    {
        Assert.Single(_catalog.Query(new CatalogQuery { Text = "fire" }));
        Assert.Single(_catalog.Query(new CatalogQuery { Text = "MATH" }));
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
}
