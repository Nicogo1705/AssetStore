// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;

namespace AssetStore.Core.Indexing;

/// <summary>
/// Provides access to an asset's working tree (the directory containing <c>AssetData/</c>) for a
/// given registry entry, plus the resolved commit. Implementations may use a local checkout
/// (prototype/tests) or clone over git (CI).
/// </summary>
public interface IAssetSource
{
    /// <summary>Materializes the asset and returns its checkout location and resolved commit.</summary>
    AssetCheckout Fetch(RegistryEntry entry);
}

/// <summary>A materialized asset checkout.</summary>
/// <param name="RepositoryRoot">Root of the asset repository working tree.</param>
/// <param name="AssetDataPath">Path to the <c>AssetData/</c> folder.</param>
/// <param name="Commit">Resolved 40-char commit SHA, or null if it could not be determined.</param>
public sealed record AssetCheckout(string RepositoryRoot, string AssetDataPath, string? Commit);
