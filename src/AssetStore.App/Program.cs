// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.App;
using AssetStore.Core.Catalog;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = baseAddress });

// Where to fetch the aggregated index. Defaults to the bundled copy; override in appsettings.json.
var indexUrl = builder.Configuration["Catalog:IndexUrl"] ?? "data/index.lock.json";
builder.Services.AddScoped<ICatalogSource>(sp =>
    new HttpCatalogSource(sp.GetRequiredService<HttpClient>(), new Uri(baseAddress, indexUrl)));

builder.Services.AddAssetStoreUi();

await builder.Build().RunAsync();
