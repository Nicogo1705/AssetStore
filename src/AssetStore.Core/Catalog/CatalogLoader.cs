// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Catalog;

/// <summary>
/// Loads the catalog from a source, keeping a local cache. On a source failure it falls back to the
/// cached copy so the app stays usable offline.
/// </summary>
public sealed class CatalogLoader(ICatalogSource source, ICatalogCache? cache = null)
{
    /// <summary>The result of a load, including whether it came from the cache.</summary>
    /// <param name="Catalog">The loaded catalog.</param>
    /// <param name="FromCache">True when the source failed and the cached copy was used.</param>
    public sealed record Result(AssetCatalog Catalog, bool FromCache);

    /// <summary>Loads from the source (refreshing the cache), or falls back to the cache on failure.</summary>
    public async Task<Result> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var index = await source.LoadAsync(cancellationToken);
            if (cache is not null)
            {
                await cache.SaveAsync(index, cancellationToken);
            }

            return new Result(new AssetCatalog(index), FromCache: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && cache is not null)
        {
            var cached = await cache.TryLoadAsync(cancellationToken);
            if (cached is not null)
            {
                return new Result(new AssetCatalog(cached), FromCache: true);
            }

            throw;
        }
    }
}
