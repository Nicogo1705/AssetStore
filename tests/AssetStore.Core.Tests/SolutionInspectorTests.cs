// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Projects;

namespace AssetStore.Core.Tests;

public sealed class SolutionInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sln-").FullName;

    [Fact]
    public void Reads_csproj_projects_from_sln()
    {
        var sln = Write("Game.sln", """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Game", "Game\Game.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Game.Windows", "Game.Windows\Game.Windows.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "Solution Items", "Solution Items", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            """);

        var projects = SolutionInspector.ReadProjects(sln);

        Assert.Equal(2, projects.Count); // the "Solution Items" folder is excluded
        Assert.Contains(projects, p => p.Name == "Game" && p.Path.EndsWith("Game.csproj"));
        Assert.Contains(projects, p => p.Name == "Game.Windows");
    }

    [Fact]
    public void Reads_projects_from_slnx()
    {
        var slnx = Write("Game.slnx", """
            <Solution>
              <Project Path="Game/Game.csproj" />
              <Project Path="Game.Windows/Game.Windows.csproj" />
            </Solution>
            """);

        var projects = SolutionInspector.ReadProjects(slnx);

        Assert.Equal(2, projects.Count);
        Assert.All(projects, p => Assert.True(Path.IsPathRooted(p.Path)));
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
