// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;
using AssetStore.Core.Serialization;

namespace AssetStore.Core.Catalog;

/// <summary>Loads the aggregated index (<c>index.lock.json</c>) from somewhere.</summary>
public interface ICatalogSource
{
    Task<IndexLock> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the index from a local file.</summary>
public sealed class FileCatalogSource(string path) : ICatalogSource
{
    public async Task<IndexLock> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return AssetStoreJson.Deserialize<IndexLock>(json);
    }
}

/// <summary>Downloads the index over HTTP (e.g. a raw GitHub URL). Works in Blazor WASM.</summary>
public sealed class HttpCatalogSource(HttpClient client, Uri url) : ICatalogSource
{
    public async Task<IndexLock> LoadAsync(CancellationToken cancellationToken = default)
    {
        var json = await client.GetStringAsync(url, cancellationToken);
        return AssetStoreJson.Deserialize<IndexLock>(json);
    }
}
