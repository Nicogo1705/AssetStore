// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AssetStore.Cli.Commands;

/// <summary>
/// Lightweight, scale-friendly refresh of the <c>stars</c> field only: reads an existing
/// index.lock.json and updates star counts via the GitHub API without cloning any repository.
/// </summary>
internal sealed class RefreshStarsCommand : Command<RefreshStarsCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("-i|--index <PATH>")]
        [Description("Path to index.lock.json. Defaults to ./index.lock.json.")]
        [DefaultValue("index.lock.json")]
        public string Index { get; init; } = "index.lock.json";
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!File.Exists(settings.Index))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Index not found:[/] {settings.Index}");
            return 1;
        }

        var index = AssetStoreJson.Deserialize<IndexLock>(File.ReadAllText(settings.Index));
        var stars = new GitHubStars(Environment.GetEnvironmentVariable("GITHUB_TOKEN"));

        var changed = 0;
        var assets = index.Assets.Select(asset =>
        {
            var fresh = stars.Get(asset.Repo);
            if (fresh is null || fresh == asset.Stars)
            {
                return asset; // keep previous value on API failure or no change
            }

            changed++;
            return asset with { Stars = fresh };
        }).ToList();

        var updated = index with { Assets = assets, GeneratedAt = DateTimeOffset.UtcNow.ToString("o") };
        File.WriteAllText(settings.Index, AssetStoreJson.Serialize(updated));

        AnsiConsole.MarkupLineInterpolated($"[green]Refreshed stars[/] for {assets.Count} asset(s); {changed} changed.");
        return 0;
    }
}
