// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Xml.Linq;

namespace AssetStore.Core.Projects;

/// <summary>Reads Stride-relevant information out of MSBuild <c>.csproj</c> files.</summary>
public static class CsprojInspector
{
    /// <summary>
    /// Detects the Stride version referenced by a project by scanning <c>Stride.* PackageReference</c>
    /// entries. <c>Stride.Engine</c> is preferred when several are present.
    /// </summary>
    public static string? DetectStrideVersion(string csprojPath)
    {
        var project = XElement.Load(csprojPath);
        string? fallback = null;

        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var include = (string?)reference.Attribute("Include");
            if (include is null || !include.StartsWith("Stride.", StringComparison.Ordinal))
            {
                continue;
            }

            var version = (string?)reference.Attribute("Version")
                ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            if (include.Equals("Stride.Engine", StringComparison.Ordinal))
            {
                return version;
            }

            fallback ??= version;
        }

        return fallback;
    }

    /// <summary>Returns the raw <c>Include</c> paths of every <c>ProjectReference</c> in a project.</summary>
    public static IReadOnlyList<string> GetProjectReferences(string csprojPath)
    {
        var project = XElement.Load(csprojPath);
        return project
            .Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();
    }

    /// <summary>Enumerates every <c>.csproj</c> file under a directory.</summary>
    public static IReadOnlyList<string> FindProjects(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];
}
