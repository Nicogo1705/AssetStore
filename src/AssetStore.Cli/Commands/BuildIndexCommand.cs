// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using AssetStore.Core.Models;
using AssetStore.Core.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AssetStore.Cli.Commands;

/// <summary>Builds <c>index.lock.json</c> from the registry.</summary>
internal sealed class BuildIndexCommand : Command<BuildIndexCommand.Settings>
{
    internal sealed class Settings : SharedSettings
    {
        [CommandOption("-o|--out <PATH>")]
        [Description("Output path for the index. Defaults to <container>/index.lock.json.")]
        public string? Output { get; init; }

        [CommandOption("--incremental")]
        [Description("Only re-fetch assets whose tracked ref moved (ls-remote); reuse the rest from the existing index.")]
        public bool Incremental { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var container = CommandHelpers.ResolveContainer(settings.Container);
        var output = settings.Output ?? Path.Combine(container, "index.lock.json");
        var builder = CommandHelpers.CreateBuilder(container, settings);
        var now = DateTimeOffset.UtcNow.ToString("o");

        IndexLock index;
        if (settings.Incremental)
        {
            var previous = File.Exists(output) ? AssetStoreJson.Deserialize<IndexLock>(File.ReadAllText(output)) : null;
            index = builder.BuildIncremental(previous, now);
        }
        else
        {
            index = builder.Build(now);
        }

        File.WriteAllText(output, AssetStoreJson.Serialize(index));

        var ok = index.Assets.Count(a => a.ValidationStatus == "ok");
        var warn = index.Assets.Count(a => a.ValidationStatus == "warning");
        var bad = index.Assets.Count(a => a.ValidationStatus is "error" or "unavailable");

        AnsiConsole.MarkupLineInterpolated($"[green]Wrote[/] {output}");
        AnsiConsole.MarkupLineInterpolated(
            $"{index.Assets.Count} asset(s): [green]{ok} ok[/], [yellow]{warn} warning[/], [red]{bad} error/unavailable[/].");

        // Building the index is a success once it is written: per-asset error/unavailable
        // statuses are recorded IN the index (the UI surfaces them) and must not fail the job,
        // otherwise a single broken/offline asset would block publishing every other one.
        // Use `validate` (which does fail on errors) to gate individual pull requests.
        return 0;
    }
}
