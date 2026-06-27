// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Models;

/// <summary>
/// Strongly-typed view of <c>registry/&lt;id&gt;.json</c> in the AssetContainer repository.
/// </summary>
public sealed record RegistryEntry
{
    public required string Id { get; init; }

    public required string Repo { get; init; }

    public string? SubmittedBy { get; init; }

    public required RefPointer Latest { get; init; }

    /// <summary>Versions stamped as quality-approved by the Stride team. CODEOWNERS-protected.</summary>
    public IReadOnlyList<CertifiedVersion> Certified { get; init; } = [];
}

/// <summary>Points at a branch/tag, optionally pinned to a resolved commit.</summary>
public sealed record RefPointer
{
    public required string Ref { get; init; }

    /// <summary>Resolved 40-char commit SHA (filled by the bot).</summary>
    public string? Commit { get; init; }
}

/// <summary>A certified version, pinned to an immutable commit.</summary>
public sealed record CertifiedVersion
{
    public required string Version { get; init; }

    public string? Tag { get; init; }

    public required string Commit { get; init; }

    public string? CertifiedBy { get; init; }

    public string? CertifiedAt { get; init; }
}
