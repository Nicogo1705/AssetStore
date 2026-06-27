// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("assetstore");

    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Validate registry entries and manifests against schemas and catalog rules.");

    config.AddCommand<BuildIndexCommand>("build-index")
        .WithDescription("Generate index.lock.json from the registry.");
});

return app.Run(args);
