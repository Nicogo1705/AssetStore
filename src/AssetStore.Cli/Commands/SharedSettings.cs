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
    [Description("Directory holding local asset checkouts. Defaults to the container's parent.")]
    public string? Workspace { get; init; }
}
