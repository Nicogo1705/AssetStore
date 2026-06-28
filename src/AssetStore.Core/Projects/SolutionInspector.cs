// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AssetStore.Core.Projects;

/// <summary>A C# project referenced by a solution.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Path">Absolute path to the .csproj.</param>
public sealed record SolutionProject(string Name, string Path);

/// <summary>Lists the C# projects of a solution (.sln or .slnx).</summary>
public static partial class SolutionInspector
{
    /// <summary>Returns the .csproj projects of a solution file, with absolute paths.</summary>
    public static IReadOnlyList<SolutionProject> ReadProjects(string solutionPath)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(solutionPath))!;
        var ext = System.IO.Path.GetExtension(solutionPath).ToLowerInvariant();
        var text = File.ReadAllText(solutionPath);

        var entries = ext == ".slnx" ? ParseSlnx(text) : ParseSln(text);

        return entries
            .Where(p => p.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => new SolutionProject(
                p.Name,
                System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, p.Path.Replace('\\', System.IO.Path.DirectorySeparatorChar)))))
            .ToList();
    }

    private static IEnumerable<(string Name, string Path)> ParseSln(string text)
    {
        foreach (Match m in SlnProjectRegex().Matches(text))
        {
            yield return (m.Groups["name"].Value, m.Groups["path"].Value);
        }
    }

    private static IEnumerable<(string Name, string Path)> ParseSlnx(string text)
    {
        var root = XElement.Parse(text);
        foreach (var project in root.Descendants().Where(e => e.Name.LocalName == "Project"))
        {
            var path = (string?)project.Attribute("Path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return (System.IO.Path.GetFileNameWithoutExtension(path), path);
            }
        }
    }

    // Project("{TYPE-GUID}") = "Name", "relative\path.csproj", "{PROJECT-GUID}"
    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""(?<name>[^""]*)"",\s*""(?<path>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectRegex();
}
