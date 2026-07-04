// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;
using AssetStore.Core.Models;

namespace AssetStore.App.Services;

/// <summary>Holds the loaded catalog and notifies components when it changes.</summary>
public sealed class CatalogState(CatalogLoader loader)
{
    public AssetCatalog? Catalog { get; private set; }

    public bool Loading { get; private set; }

    public bool FromCache { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>Loads the catalog once. Safe to call from multiple components.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Catalog is not null || Loading)
        {
            return;
        }

        Loading = true;
        Error = null;
        Notify();

        try
        {
            var result = await loader.LoadAsync();
            Catalog = result.Catalog;
            FromCache = result.FromCache;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
            Notify();
        }
    }

    public IndexedAsset? Find(string id) =>
        Catalog?.Assets.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    private void Notify() => Changed?.Invoke();
}
