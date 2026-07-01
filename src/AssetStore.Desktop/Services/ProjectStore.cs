// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.Json;

namespace AssetStore.Desktop.Services;

/// <summary>A solution/project the user tracks in the "My projects" manager.</summary>
/// <param name="Path">Absolute path to a .sln/.slnx/.csproj.</param>
public sealed record TrackedProject(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public bool IsSolution =>
        Path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || Path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the tracked file still exists on disk.</summary>
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// Persists the list of projects the user manages, in a small JSON file under the OS application-data
/// folder (e.g. <c>%APPDATA%/StrideAssetStore/projects.json</c>) so it survives across browsers and
/// restarts. Desktop-only (needs filesystem access).
/// </summary>
public sealed class ProjectStore
{
    private readonly string _file;
    private readonly Lock _gate = new();

    public ProjectStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StrideAssetStore");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "projects.json");
    }

    /// <summary>The tracked projects, most-recently-added last.</summary>
    public IReadOnlyList<TrackedProject> List()
    {
        lock (_gate)
        {
            return Read().Select(p => new TrackedProject(p)).ToList();
        }
    }

    /// <summary>Adds a project by path (idempotent, case-insensitive). Returns the normalized path.</summary>
    public string Add(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var list = Read();
            if (!list.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(full);
                Write(list);
            }

            return full;
        }
    }

    /// <summary>Stops tracking a project (does not touch the project on disk).</summary>
    public void Remove(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            var list = Read();
            if (list.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Write(list);
            }
        }
    }

    private List<string> Read()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file)) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void Write(List<string> list)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch
        {
            // Best-effort: a locked or read-only app-data folder shouldn't crash the manager.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
