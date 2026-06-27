// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;

namespace AssetStore.Core.Catalog;

/// <summary>How to order catalog results.</summary>
public enum CatalogSort
{
    Name,
    Category,
    Stars,
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

    /// <summary>Whether free-text search also looks inside the description (defaults to true).</summary>
    public bool SearchDescription { get; init; } = true;

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

    /// <summary>Distinct major.minor Stride versions present in the catalog, newest first (e.g. "4.2", "4.1").</summary>
    public IReadOnlyList<string> StrideVersions =>
        Assets.Select(a => StrideVersionMatcher.Parse(a.Latest.DetectedStrideVersion))
              .Where(v => v is not null)
              .Select(v => $"{v!.Major}.{v.Minor}")
              .Distinct(StringComparer.Ordinal)
              .OrderByDescending(s => Version.Parse(s))
              .ToList();

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

        // With a search term, rank by relevance (name > id > tags > description) instead of the facet sort.
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            return result
                .Select(a => (Asset: a, Score: Score(a, query.Text!, query.SearchDescription)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Asset.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Asset)
                .ToList();
        }

        result = query.SortBy switch
        {
            CatalogSort.Category => result.OrderBy(a => a.Manifest.Category, StringComparer.OrdinalIgnoreCase)
                                          .ThenBy(a => a.Manifest.Name, StringComparer.OrdinalIgnoreCase),
            CatalogSort.Stars => result.OrderByDescending(a => a.Stars ?? -1)
                                       .ThenBy(a => a.Manifest.Name, StringComparer.OrdinalIgnoreCase),
            _ => result.OrderBy(a => a.Manifest.Name, StringComparer.OrdinalIgnoreCase),
        };

        if (query.Descending)
        {
            result = result.Reverse();
        }

        return result.ToList();
    }

    /// <summary>
    /// Relevance score for a free-text query. Name matches dominate (exact &gt; prefix &gt; substring),
    /// followed by id, tags, then description. Zero means no match.
    /// </summary>
    private static int Score(IndexedAsset asset, string text, bool searchDescription)
    {
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        var m = asset.Manifest;
        var score = 0;

        if (m.Name.Equals(text, ci)) score += 1000;
        else if (m.Name.StartsWith(text, ci)) score += 500;
        else if (m.Name.Contains(text, ci)) score += 200;

        if (m.Id.Contains(text, ci)) score += 120;

        if (m.Tags.Any(t => t.Equals(text, ci))) score += 150;
        else if (m.Tags.Any(t => t.Contains(text, ci))) score += 80;

        if (searchDescription && m.Description.Contains(text, ci)) score += 30;

        return score;
    }
}
