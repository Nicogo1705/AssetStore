// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.Json;

namespace AssetStore.Cli;

/// <summary>Best-effort GitHub stargazer-count lookup for index enrichment.</summary>
public sealed class GitHubStars
{
    private readonly HttpClient _http = new();

    public GitHubStars(string? token)
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("assetstore-cli");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
    }

    /// <summary>Returns the stargazer count for a GitHub repo URL, or null (non-GitHub or failure).</summary>
    public int? Get(string repoUrl)
    {
        if (!TryParseGitHub(repoUrl, out var owner, out var name))
        {
            return null;
        }

        try
        {
            var json = _http.GetStringAsync($"https://api.github.com/repos/{owner}/{name}").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("stargazers_count").GetInt32();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseGitHub(string repoUrl, out string owner, out string name)
    {
        owner = name = "";
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
        {
            return false;
        }

        owner = parts[0];
        name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return true;
    }
}
