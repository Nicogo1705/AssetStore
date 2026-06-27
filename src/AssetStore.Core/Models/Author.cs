// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Models;

/// <summary>An asset author.</summary>
public sealed record Author
{
    public required string Name { get; init; }

    public string? Url { get; init; }
}
