// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Spectre.Console.Cli;

namespace AssetStore.Cli.Commands;

/// <summary>Options common to every command.</summary>
internal class SharedSettings : CommandSettings
{
    [CommandOption("-c|--container <PATH>")]
    [Description("Path to the AssetContainer repository. Defaults to the current or child folder.")]
    public string? Container { get; init; }

    [CommandOption("-w|--workspace <PATH>")]
    [Description("Directory holding local asset checkouts (local source). Defaults to the container's parent.")]
    public string? Workspace { get; init; }

    [CommandOption("-s|--source <KIND>")]
    [Description("Asset source: 'local' (sibling checkouts) or 'git' (clone repos). Defaults to local.")]
    [DefaultValue("local")]
    public string Source { get; init; } = "local";

    [CommandOption("--cache <PATH>")]
    [Description("Cache directory for cloned repos (git source). Defaults to a temp folder.")]
    public string? Cache { get; init; }
}
