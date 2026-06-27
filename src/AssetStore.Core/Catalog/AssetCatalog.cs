// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;

namespace AssetStore.Core.Catalog;

/// <summary>How to order catalog results.</summary>
public enum CatalogSort
{
    Name,
    Category,
}

/// <summary>A catalog query: free text, facets and ordering.</summary>
public sealed record CatalogQuery
{
    public string? Text { get; init; }

    public string? Category { get; init; }

    public IReadOnlyCollection<string> Tags { get; init; } = [];

    /// <summary>Target Stride version to filter compatibility against (with <see cref="StrideMatch"/>).</summary>
    public string? StrideVersion { get; init; }

    public StrideMatch StrideMatch { get; init; } = StrideMatch.Minor;

    /// <summary>Only assets that have at least one certified version.</summary>
    public bool CertifiedOnly { get; init; }

    public CatalogSort SortBy { get; init; } = CatalogSort.Name;

    public bool Descending { get; init; }
}

/// <summary>A queryable in-memory view over an <see cref="IndexLock"/>.</summary>
public sealed class AssetCatalog(IndexLock index)
{
    public string GeneratedAt => index.GeneratedAt;

    public IReadOnlyList<IndexedAsset> Assets => index.Assets;

    /// <summary>Distinct categories present in the catalog, sorted.</summary>
    public IReadOnlyList<string> Categories =>
        Assets.Select(a => a.Manifest.Category).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>Distinct tags present in the catalog, sorted.</summary>
    public IReadOnlyList<string> Tags =>
        Assets.SelectMany(a => a.Manifest.Tags).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>Applies a query and returns the matching, ordered assets.</summary>
    public IReadOnlyList<IndexedAsset> Query(CatalogQuery query)
    {
        IEnumerable<IndexedAsset> result = Assets;

        if (!string.IsNullOrWhiteSpace(query.Category))
        {
            result = result.Where(a => string.Equals(a.Manifest.Category, query.Category, StringComparison.Ordinal));
        }

        if (query.Tags.Count > 0)
        {
            result = result.Where(a => query.Tags.All(t => a.Manifest.Tags.Contains(t, StringComparer.Ordinal)));
        }

        if (query.CertifiedOnly)
        {
            result = result.Where(a => a.Certified.Count > 0);
        }

        if (!string.IsNullOrWhiteSpace(query.StrideVersion))
        {
            result = result.Where(a =>
                StrideVersionMatcher.IsCompatible(a.Latest.DetectedStrideVersion, query.StrideVersion!, query.StrideMatch));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            result = result.Where(a => Matches(a, query.Text!));
        }

        result = query.SortBy switch
        {
            CatalogSort.Category => result.OrderBy(a => a.Manifest.Category, StringComparer.OrdinalIgnoreCase)
                                          .ThenBy(a => a.Manifest.Name, StringComparer.OrdinalIgnoreCase),
            _ => result.OrderBy(a => a.Manifest.Name, StringComparer.OrdinalIgnoreCase),
        };

        if (query.Descending)
        {
            result = result.Reverse();
        }

        return result.ToList();
    }

    private static bool Matches(IndexedAsset asset, string text)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        var m = asset.Manifest;
        return m.Name.Contains(text, ci)
            || m.Id.Contains(text, ci)
            || m.Description.Contains(text, ci)
            || m.Tags.Any(t => t.Contains(text, ci));
    }
}
