// Copyright (c) 2026 Nicogo
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;

namespace AssetStore.App.Services;

/// <summary>Availability of the local command-line tools the desktop publish flow relies on.</summary>
/// <param name="GitInstalled">Whether <c>git</c> is on PATH.</param>
/// <param name="GitVersion">The reported git version line, if any.</param>
/// <param name="GhInstalled">Whether the GitHub CLI (<c>gh</c>) is on PATH.</param>
/// <param name="GhVersion">The reported gh version line, if any.</param>
/// <param name="GhAuthenticated">Whether <c>gh auth status</c> reports a signed-in account.</param>
/// <param name="GhAccount">The signed-in GitHub login, if known.</param>
public sealed record CliStatus(
    bool GitInstalled, string? GitVersion,
    bool GhInstalled, string? GhVersion,
    bool GhAuthenticated, string? GhAccount)
{
    /// <summary>Everything needed to open a PR from the machine: git + gh + an authenticated gh.</summary>
    public bool ReadyToPublish => GitInstalled && GhInstalled && GhAuthenticated;

    public static CliStatus Unavailable { get; } = new(false, null, false, null, false, null);
}

/// <summary>
/// Opens registry pull requests using the machine's own command-line tools (<c>git</c> + the
/// GitHub CLI, <c>gh</c>) instead of a pasted token — the same "use the tools you already have"
/// model as the local install. Implemented only on the desktop host; the browser host uses a
/// no-op that reports the tools as unavailable.
/// </summary>
public interface ICliPublisher
{
    /// <summary>True on hosts that can run local processes (desktop); false in the browser.</summary>
    bool Supported { get; }

    /// <summary>Detects git/gh and whether gh is authenticated.</summary>
    Task<CliStatus> CheckAsync(CancellationToken ct = default);

    /// <summary>Opens a PR adding (or updating) <c>registry/&lt;id&gt;.json</c> via gh.</summary>
    Task<PublishResult> PublishAsync(RegistryEntry entry, CancellationToken ct = default);

    /// <summary>Opens a PR adding a certified version to an existing entry via gh.</summary>
    Task<PublishResult> CertifyAsync(string id, CertifiedVersion version, CancellationToken ct = default);

    /// <summary>Opens a PR deleting <c>registry/&lt;id&gt;.json</c> via gh.</summary>
    Task<PublishResult> RemoveAsync(string id, CancellationToken ct = default);
}

/// <summary>Browser fallback: no local CLI tools, so every action reports unavailable.</summary>
public sealed class NullCliPublisher : ICliPublisher
{
    private static readonly PublishResult NotHere =
        new(false, null, "Command-line publishing is only available in the desktop app.");

    public bool Supported => false;

    public Task<CliStatus> CheckAsync(CancellationToken ct = default) => Task.FromResult(CliStatus.Unavailable);

    public Task<PublishResult> PublishAsync(RegistryEntry entry, CancellationToken ct = default) => Task.FromResult(NotHere);

    public Task<PublishResult> CertifyAsync(string id, CertifiedVersion version, CancellationToken ct = default) => Task.FromResult(NotHere);

    public Task<PublishResult> RemoveAsync(string id, CancellationToken ct = default) => Task.FromResult(NotHere);
}
