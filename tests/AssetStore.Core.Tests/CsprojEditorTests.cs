// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Projects;

namespace AssetStore.Core.Tests;

public sealed class CsprojEditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("editor-").FullName;

    [Fact]
    public void Adds_a_project_reference_then_is_idempotent()
    {
        var game = Write("Game/Game.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>");
        var lib = Write("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        Assert.True(CsprojEditor.AddProjectReference(game, lib));   // added
        Assert.False(CsprojEditor.AddProjectReference(game, lib));  // already present

        var refs = CsprojInspector.GetProjectReferences(game);
        Assert.Single(refs);
        Assert.Equal(@"..\Lib\Lib.csproj", refs[0]);
    }

    private string Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
