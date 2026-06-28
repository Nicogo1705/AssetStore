// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.ComponentModel;
using Spectre.Console.Cli;

namespace AssetStore.Cli.Commands;

/// <summary>Options for the <c>validate</c> command.</summary>
internal sealed class ValidateSettings : SharedSettings
{
    [CommandOption("--only <ID>")]
    [Description("Only fail on these asset ids (repeatable). Used by PR CI to judge a change on the assets it touches, not the whole registry. Other assets are still listed but never fail the run.")]
    public string[] Only { get; init; } = [];
}
