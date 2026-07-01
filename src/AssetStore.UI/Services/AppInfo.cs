// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.App.Services;

/// <summary>
/// Facts about the application itself. Bound from configuration (section "App") so the download/update
/// links can be pointed at a different distribution repository — e.g. a Stride community org — without
/// code changes.
/// </summary>
public sealed class AppInfo
{
    /// <summary>The application's own GitHub repository — the source of desktop release downloads.</summary>
    public string Repo { get; init; } = "https://github.com/Nicogo1705/AssetStore";
}
