// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;

namespace AssetStore.Core.Tests;

/// <summary>Builds synthetic catalog data for tests.</summary>
internal static class CatalogTestData
{
    public static IndexedAsset Asset(
        string id,
        string name,
        string category,
        string[]? tags = null,
        string? strideVersion = "4.2.0.1",
        bool certified = false,
        string? description = null)
    {
        return new IndexedAsset
        {
            Id = id,
            Repo = $"https://example.com/{id}",
            Manifest = new AssetManifest
            {
                Id = id,
                Name = name,
                Description = description ?? $"{name} description",
                Category = category,
                License = "MIT",
                Tags = tags ?? [],
            },
            Latest = new IndexedVersion
            {
                Ref = "main",
                Commit = new string('a', 40),
                ContentHash = "hash",
                DetectedStrideVersion = strideVersion,
                Validated = true,
            },
            Certified = certified
                ? [new IndexedCertifiedVersion { Version = "1.0.0", Commit = new string('b', 40) }]
                : [],
            ValidationStatus = "ok",
        };
    }

    public static IndexLock Index(params IndexedAsset[] assets) =>
        new() { GeneratedAt = "2026-01-01T00:00:00Z", Assets = assets };
}
