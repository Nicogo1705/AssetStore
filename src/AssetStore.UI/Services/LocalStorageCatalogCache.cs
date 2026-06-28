// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Catalog;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;
using Microsoft.JSInterop;

namespace AssetStore.App.Services;

/// <summary>Browser-localStorage implementation of the catalog cache (offline fallback in WASM).</summary>
public sealed class LocalStorageCatalogCache(IJSRuntime js, string key = "assetstore.index") : ICatalogCache
{
    public async Task<IndexLock?> TryLoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, key);
            return string.IsNullOrEmpty(json) ? null : AssetStoreJson.Deserialize<IndexLock>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(IndexLock index, CancellationToken cancellationToken = default)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", cancellationToken, key, AssetStoreJson.Serialize(index));
        }
        catch
        {
            // Best-effort cache; ignore storage failures (quota, private mode, etc.).
        }
    }
}
