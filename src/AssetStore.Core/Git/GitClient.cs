// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AssetStore.Core.Git;

/// <summary>Thin wrapper over the installed <c>git</c> executable.</summary>
/// <remarks>
/// Decentralization-friendly: works against any git host. Only the operations needed by the
/// prototype are exposed (resolve a commit, shallow clone a ref).
/// </remarks>
public sealed class GitClient(string gitExecutable = "git")
{
    private static readonly Regex FolderNamePattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>Returns the full commit SHA that <paramref name="refName"/> resolves to in a repo, or null.</summary>
    public string? ResolveCommit(string repositoryPath, string refName = "HEAD")
    {
        RejectOptionLike(refName); // refs come from untrusted registry data — same guard as the other wrappers
        var (exitCode, output, _) = Run(repositoryPath, "rev-parse", refName);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>Updates an existing checkout to the tip of <paramref name="refName"/> (shallow fetch + hard reset).</summary>
    public bool UpdateToRef(string repositoryPath, string refName)
    {
        RejectOptionLike(refName);
        if (Run(repositoryPath, [.. SafeProtocol, "fetch", "--depth", "1", "origin", refName]).ExitCode != 0)
        {
            return false;
        }

        return Run(repositoryPath, "reset", "--hard", "FETCH_HEAD").ExitCode == 0;
    }

    /// <summary>
    /// Shallow-clones <paramref name="repoUrl"/> at <paramref name="refName"/> into a directory,
    /// checking out <b>only the <c>AssetData/</c> folder</b> (sparse). The store consumes nothing
    /// else, so the rest of the repo — sample/.Windows projects, solutions, etc. — is never written.
    /// </summary>
    public void ShallowClone(string repoUrl, string refName, string destination)
    {
        RejectOptionLike(repoUrl, refName);

        // `--branch` only accepts a branch/tag name; for a raw commit SHA we do a blobless clone of the
        // default branch (no depth cap so any reachable commit can be checked out) and check out the SHA.
        var isCommit = IsCommitSha(refName);
        var cloneArgs = isCommit
            ? new[] { "clone", "--no-checkout", "--filter=blob:none", "--", repoUrl, destination }
            : ["clone", "--no-checkout", "--depth", "1", "--branch", refName, "--", repoUrl, destination];

        var clone = Run(null, [.. SafeProtocol, .. cloneArgs]);
        if (clone.ExitCode != 0)
        {
            throw new InvalidOperationException($"git clone failed for {repoUrl}@{refName}: {clone.StdErr}");
        }

        // Restrict the working tree to AssetData/ before checking it out.
        Run(destination, "config", "core.sparseCheckout", "true");
        Run(destination, "config", "core.sparseCheckoutCone", "false");
        File.WriteAllText(Path.Combine(destination, ".git", "info", "sparse-checkout"), "/AssetData/\n");

        var checkout = Run(destination, [.. SafeProtocol, "checkout", refName]);
        if (checkout.ExitCode != 0)
        {
            throw new InvalidOperationException($"git checkout failed for {repoUrl}@{refName}: {checkout.StdErr}");
        }
    }

    /// <summary>
    /// Derives a safe local folder name from a repo URL (last path segment, sans .git), rejecting
    /// anything that could escape a parent directory (e.g. "..", path separators, invalid chars).
    /// </summary>
    public static string SafeRepoFolderName(string repoUrl)
    {
        var name = repoUrl.TrimEnd('/').Split('/').Last();
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        // OS-independent allowlist (Path.GetInvalidFileNameChars differs per platform — e.g. ':' is
        // allowed on Linux). GitHub repo names are [A-Za-z0-9._-] anyway.
        if (name is "" or "." or ".." || !FolderNamePattern.IsMatch(name))
        {
            throw new InvalidOperationException($"Unsafe repository folder name derived from '{repoUrl}'.");
        }

        return name;
    }

    /// <summary>Resolves the commit a branch/tag points to on the remote, without cloning. For an annotated
    /// tag the peeled commit (<c>^{}</c> line) is preferred over the tag-object SHA, so it matches a checkout.</summary>
    public string? ResolveRemoteCommit(string repoUrlOrPath, string refName)
    {
        RejectOptionLike(repoUrlOrPath, refName);
        var (exitCode, output, _) = Run(null, [.. SafeProtocol, "ls-remote", "--", repoUrlOrPath, refName]);
        if (exitCode != 0)
        {
            return null;
        }

        string? first = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            var sha = line[..tab].Trim();
            var name = line[(tab + 1)..].Trim();
            if (name.EndsWith("^{}", StringComparison.Ordinal))
            {
                return sha; // peeled commit of an annotated tag — the real commit a checkout lands on
            }

            first ??= sha;
        }

        return first;
    }

    /// <summary>ISO-8601 committer date of a commit in a local checkout, or null.</summary>
    public string? GetCommitDate(string repositoryPath, string commit = "HEAD")
    {
        RejectOptionLike(commit);
        var (exitCode, output, _) = Run(repositoryPath, "show", "-s", "--format=%cI", commit);
        return exitCode == 0 ? output.Trim() : null;
    }

    private static bool IsCommitSha(string value) =>
        value.Length == 40 && value.All(char.IsAsciiHexDigit);

    /// <summary>Lists a repository's tags as (tag, commit) without cloning, via <c>ls-remote --tags</c>.</summary>
    public IReadOnlyList<(string Tag, string Commit)> ListRemoteTags(string repoUrlOrPath)
    {
        RejectOptionLike(repoUrlOrPath);
        var (exitCode, output, _) = Run(null, [.. SafeProtocol, "ls-remote", "--tags", "--", repoUrlOrPath]);
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

        // Read both pipes concurrently to avoid a deadlock when one fills its buffer (git writes
        // progress to stderr while output goes to stdout).
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    // Block non-https transports (notably git's `ext::` = arbitrary command execution) and any
    // argument that looks like an option, since repo URLs / refs come from untrusted registry data.
    private static readonly string[] SafeProtocol =
        ["-c", "protocol.ext.allow=never", "-c", "protocol.file.allow=never"];

    private static void RejectOptionLike(params string[] values)
    {
        foreach (var v in values)
        {
            if (v.StartsWith('-'))
            {
                throw new InvalidOperationException($"Refusing git argument that looks like an option: '{v}'.");
            }
        }
    }
}
