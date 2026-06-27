// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;

namespace AssetStore.Core.Git;

/// <summary>Thin wrapper over the installed <c>git</c> executable.</summary>
/// <remarks>
/// Decentralization-friendly: works against any git host. Only the operations needed by the
/// prototype are exposed (resolve a commit, shallow clone a ref).
/// </remarks>
public sealed class GitClient(string gitExecutable = "git")
{
    /// <summary>Returns the full commit SHA that <paramref name="refName"/> resolves to in a repo, or null.</summary>
    public string? ResolveCommit(string repositoryPath, string refName = "HEAD")
    {
        var (exitCode, output, _) = Run(repositoryPath, "rev-parse", refName);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Shallow-clones <paramref name="repoUrl"/> at <paramref name="refName"/> into a directory.</summary>
    public void ShallowClone(string repoUrl, string refName, string destination)
    {
        var (exitCode, _, error) = Run(
            workingDirectory: null,
            "clone", "--depth", "1", "--branch", refName, repoUrl, destination);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git clone failed for {repoUrl}@{refName}: {error}");
        }
    }

    /// <summary>True if <c>git</c> is available on the PATH.</summary>
    public bool IsAvailable()
    {
        try
        {
            return Run(null, "--version").ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private (int ExitCode, string StdOut, string StdErr) Run(string? workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(gitExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Unable to start '{gitExecutable}'.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
