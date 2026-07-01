// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Runtime.InteropServices;

namespace AssetStore.App.Services;

/// <summary>
/// The desktop builds published by the release workflow, and how to name their release assets. Asset
/// names are stable so <c>releases/latest/download/&lt;name&gt;</c> deep links never go stale.
/// </summary>
public static class DesktopBuilds
{
    /// <param name="Rid">.NET runtime identifier the build is published for.</param>
    /// <param name="Label">Human label for the download page.</param>
    /// <param name="AssetName">Release asset file name.</param>
    /// <param name="Os">OS family: windows | macos | linux (matches the browser's detected OS).</param>
    public sealed record Build(string Rid, string Label, string AssetName, string Os);

    public static readonly IReadOnlyList<Build> All =
    [
        new("win-x64", "Windows (x64)", "AssetStore-win-x64.zip", "windows"),
        new("osx-arm64", "macOS (Apple Silicon)", "AssetStore-osx-arm64.tar.gz", "macos"),
        new("osx-x64", "macOS (Intel)", "AssetStore-osx-x64.tar.gz", "macos"),
        new("linux-x64", "Linux (x64)", "AssetStore-linux-x64.tar.gz", "linux"),
    ];

    /// <summary>The build matching the current OS/architecture, or null when unknown (e.g. running in WASM).</summary>
    public static Build? Current()
    {
        var os =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : null;
        if (os is null)
        {
            return null;
        }

        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var rid = $"{os}-{arch}";
        return All.FirstOrDefault(b => b.Rid == rid)
            ?? All.FirstOrDefault(b => b.Rid.StartsWith(os, StringComparison.Ordinal));
    }
}
