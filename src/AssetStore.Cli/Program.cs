// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

// When output is redirected (CI logs, a file, a pipe to tee), emit plain text instead of
// ANSI colour codes — otherwise escape sequences leak into captured logs and PR comments.
if (Console.IsOutputRedirected)
{
    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
    });
}

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("assetstore");

    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Validate registry entries and manifests against schemas and catalog rules.");

    config.AddCommand<BuildIndexCommand>("build-index")
        .WithDescription("Generate index.lock.json from the registry.");

    config.AddCommand<RefreshStarsCommand>("refresh-stars")
        .WithDescription("Refresh only GitHub star counts in an existing index (no cloning).");
});

return app.Run(args);
