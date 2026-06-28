// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace AssetStore.Cli.Commands;

/// <summary>Validates every registry entry and manifest, printing a report.</summary>
internal sealed class ValidateCommand : Command<ValidateSettings>
{
    protected override int Execute(CommandContext context, ValidateSettings settings, CancellationToken cancellation)
    {
        var container = CommandHelpers.ResolveContainer(settings.Container);
        AnsiConsole.MarkupLineInterpolated($"[grey]Container:[/] {container}");
        AnsiConsole.MarkupLineInterpolated($"[grey]Source:[/] {settings.Source}");

        var scope = new HashSet<string>(settings.Only, StringComparer.OrdinalIgnoreCase);
        if (scope.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]Scope:[/] {string.Join(", ", settings.Only)}");
        }

        var index = CommandHelpers.CreateBuilder(container, settings).Build(DateTimeOffset.UtcNow.ToString("o"));

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Asset");
        table.AddColumn("Status");
        table.AddColumn("Stride");
        table.AddColumn("Deps");

        foreach (var asset in index.Assets)
        {
            table.AddRow(
                Markup.Escape(asset.Id),
                StatusMarkup(asset.ValidationStatus),
                Markup.Escape(asset.Latest.DetectedStrideVersion ?? "-"),
                Markup.Escape(asset.Latest.ResolvedDependencies.Count.ToString()));
        }

        AnsiConsole.Write(table);
        PrintMessages(index.Assets);

        // When --only is given (PR CI), judge the change on those assets alone; others are
        // shown for context but never fail the run (e.g. an intentionally-broken demo asset).
        var judged = scope.Count > 0
            ? index.Assets.Where(a => scope.Contains(a.Id)).ToList()
            : index.Assets;

        var missing = scope.Where(id => index.Assets.All(a => !string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var id in missing)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗ Requested asset '{id}' is not in the registry.[/]");
        }

        var failed = judged.Count(a => a.ValidationStatus is "error" or "unavailable");
        if (failed > 0 || missing.Count > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]✗ {failed + missing.Count} asset(s) failed validation.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(scope.Count > 0 ? "[green]✓ Changed asset(s) valid.[/]" : "[green]✓ All assets valid.[/]");
        return 0;
    }

    private static void PrintMessages(IReadOnlyList<IndexedAsset> assets)
    {
        foreach (var asset in assets.Where(a => a.ValidationMessages.Count > 0))
        {
            AnsiConsole.MarkupLineInterpolated($"\n[bold]{asset.Id}[/]");
            foreach (var message in asset.ValidationMessages)
            {
                AnsiConsole.MarkupLineInterpolated($"  {message}");
            }
        }
    }

    private static string StatusMarkup(string status) => status switch
    {
        "ok" => "[green]ok[/]",
        "warning" => "[yellow]warning[/]",
        "error" => "[red]error[/]",
        _ => "[red]unavailable[/]",
    };
}
