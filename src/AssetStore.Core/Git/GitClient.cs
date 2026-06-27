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

    /// <summary>Updates an existing checkout to the tip of <paramref name="refName"/> (shallow fetch + hard reset).</summary>
    public bool UpdateToRef(string repositoryPath, string refName)
    {
        if (Run(repositoryPath, "fetch", "--depth", "1", "origin", refName).ExitCode != 0)
        {
            return false;
        }

        return Run(repositoryPath, "reset", "--hard", "FETCH_HEAD").ExitCode == 0;
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

    /// <summary>Resolves the commit a branch/tag points to on the remote, without cloning.</summary>
    public string? ResolveRemoteCommit(string repoUrlOrPath, string refName)
    {
        var (exitCode, output, _) = Run(null, "ls-remote", repoUrlOrPath, refName);
        if (exitCode != 0)
        {
            return null;
        }

        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var tab = firstLine?.IndexOf('\t') ?? -1;
        return tab > 0 ? firstLine![..tab].Trim() : null;
    }

    /// <summary>Lists a repository's tags as (tag, commit) without cloning, via <c>ls-remote --tags</c>.</summary>
    public IReadOnlyList<(string Tag, string Commit)> ListRemoteTags(string repoUrlOrPath)
    {
        var (exitCode, output, _) = Run(null, "ls-remote", "--tags", repoUrlOrPath);
        return exitCode == 0 ? ParseLsRemoteTags(output) : [];
    }

    /// <summary>Parses <c>git ls-remote --tags</c> output, preferring the peeled commit of annotated tags.</summary>
    public static IReadOnlyList<(string Tag, string Commit)> ParseLsRemoteTags(string output)
    {
        const string prefix = "refs/tags/";
        var commits = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var sha = line[..tab].Trim();
            var refName = line[(tab + 1)..].Trim();
            if (!refName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var tag = refName[prefix.Length..];
            var peeled = tag.EndsWith("^{}", StringComparison.Ordinal);
            if (peeled)
            {
                tag = tag[..^3];
            }

            if (!commits.ContainsKey(tag))
            {
                order.Add(tag);
            }

            // Peeled line (annotated tag) carries the actual commit and overrides the tag-object sha.
            if (peeled || !commits.ContainsKey(tag))
            {
                commits[tag] = sha;
            }
        }

        return order.Select(t => (t, commits[t])).ToList();
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
