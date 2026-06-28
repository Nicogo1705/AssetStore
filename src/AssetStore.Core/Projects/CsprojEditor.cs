// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Xml.Linq;

namespace AssetStore.Core.Projects;

/// <summary>Edits MSBuild <c>.csproj</c> files (adding project references).</summary>
public static class CsprojEditor
{
    /// <summary>
    /// Adds a <c>&lt;ProjectReference&gt;</c> from <paramref name="csprojPath"/> to
    /// <paramref name="referencedCsprojPath"/> (idempotent). Returns true if the file was modified.
    /// </summary>
    public static bool AddProjectReference(string csprojPath, string referencedCsprojPath)
    {
        var csprojDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath))!;
        var include = Path.GetRelativePath(csprojDir, Path.GetFullPath(referencedCsprojPath))
            .Replace('/', '\\'); // MSBuild convention

        var doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace);
        var project = doc.Root ?? throw new InvalidOperationException($"'{csprojPath}' has no root element.");

        var already = project.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Any(p => string.Equals(NormalizePath(p), NormalizePath(include), StringComparison.OrdinalIgnoreCase));

        if (already)
        {
            return false;
        }

        var ns = project.Name.Namespace;
        var itemGroup = new XElement(ns + "ItemGroup",
            new XText("\n    "),
            new XElement(ns + "ProjectReference", new XAttribute("Include", include)),
            new XText("\n  "));

        // Append on its own indented lines so the .csproj stays readable.
        project.Add(new XText("\n  "), itemGroup, new XText("\n"));

        doc.Save(csprojPath);
        return true;
    }

    private static string? NormalizePath(string? path) =>
        path?.Replace('/', '\\').Trim();
}
