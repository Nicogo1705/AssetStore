// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Tests;

/// <summary>Locates the sibling AssetContainer / example repositories for integration tests.</summary>
internal static class TestPaths
{
    /// <summary>The directory that holds AssetContainer and the ExampleAsset.* repos, or null.</summary>
    public static string? Workspace { get; } = FindWorkspace();

    public static string Container => Path.Combine(Workspace!, "AssetContainer");

    public static bool Available => Workspace is not null;

    private static string? FindWorkspace()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "AssetContainer", "schemas")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
