// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;
using static AssetStore.Core.Tests.CatalogTestData;

namespace AssetStore.Core.Tests;

public sealed class CatalogLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("catalog-").FullName;

    [Fact]
    public async Task Loads_from_file_source_and_populates_cache()
    {
        var indexPath = Path.Combine(_dir, "index.lock.json");
        var cachePath = Path.Combine(_dir, "cache.json");
        await File.WriteAllTextAsync(indexPath, AssetStoreJson.Serialize(Index(Asset("com.a.x", "X", "Tools"))));

        var loader = new CatalogLoader(new FileCatalogSource(indexPath), new FileCatalogCache(cachePath));
        var result = await loader.LoadAsync();

        Assert.False(result.FromCache);
        Assert.Single(result.Catalog.Assets);
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task Falls_back_to_cache_when_source_fails()
    {
        var cachePath = Path.Combine(_dir, "cache.json");
        await new FileCatalogCache(cachePath).SaveAsync(Index(Asset("com.a.cached", "Cached", "Tools")));

        var loader = new CatalogLoader(new ThrowingSource(), new FileCatalogCache(cachePath));
        var result = await loader.LoadAsync();

        Assert.True(result.FromCache);
        Assert.Equal("com.a.cached", result.Catalog.Assets[0].Id);
    }

    [Fact]
    public async Task Rethrows_when_no_cache_available()
    {
        var loader = new CatalogLoader(new ThrowingSource(), new FileCatalogCache(Path.Combine(_dir, "missing.json")));

        await Assert.ThrowsAsync<HttpRequestException>(() => loader.LoadAsync());
    }

    private sealed class ThrowingSource : ICatalogSource
    {
        public Task<IndexLock> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("offline");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
