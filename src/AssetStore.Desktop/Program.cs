// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using AssetStore.App;
using AssetStore.Core.Catalog;
using AssetStore.Desktop.Components;

const string Url = "http://localhost:5111";

// stride-assetstore:// launch (from the web storefront's Install/Start buttons): if an instance is
// already serving, just open the requested page in it and exit instead of failing to bind. This
// applies to ANY protocol launch (including plain stride-assetstore://open with no mapped path).
var launchPath = AssetStore.Desktop.Services.ProtocolLauncher.ParseLaunchPath(args);
var protocolLaunch = args.Any(a => a.StartsWith(
    AssetStore.Desktop.Services.ProtocolLauncher.Scheme + "://", StringComparison.OrdinalIgnoreCase));
if (protocolLaunch && AssetStore.Desktop.Services.ProtocolLauncher.IsAlreadyRunning(new Uri(Url).Port))
{
    OpenBrowser(Url + (launchPath ?? ""));
    return;
}

// Register the protocol for the current user (Windows, HKCU, best-effort).
AssetStore.Desktop.Services.ProtocolLauncher.TryRegisterWindowsScheme();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    EnvironmentName = Environments.Production, // desktop app: no dev-time static asset patching
});
builder.WebHost.UseUrls(Url);
builder.WebHost.UseStaticWebAssets(); // serve _framework + RCL assets in Production/dotnet run
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Live catalog from the public registry (offline cache falls back via CatalogLoader).
// A self-pointing HttpClient also serves the publish form's bundled catalog metadata.
var indexUrl = builder.Configuration["Catalog:IndexUrl"]
    ?? "https://raw.githubusercontent.com/Nicogo1705/AssetContainer/main/index.lock.json";
var appRepo = builder.Configuration["App:Repo"] ?? "https://github.com/Nicogo1705/AssetStore";
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(Url + "/") });
builder.Services.AddScoped<ICatalogSource>(_ => new HttpCatalogSource(new HttpClient(), new Uri(indexUrl)));
builder.Services.AddAssetStoreUi(
    builder.Configuration.GetSection("Registry").Get<AssetStore.App.Services.RegistryOptions>(),
    builder.Configuration.GetSection("App").Get<AssetStore.App.Services.AppInfo>());
builder.Services.AddScoped<AssetStore.Desktop.Services.DesktopInstaller>();
builder.Services.AddSingleton<AssetStore.Desktop.Services.ProjectStore>();

// Desktop can open registry PRs with the local git + GitHub CLI (no pasted token). Overrides the
// browser's no-op ICliPublisher registered by AddAssetStoreUi.
builder.Services.AddScoped<AssetStore.App.Services.ICliPublisher, AssetStore.Desktop.Services.GhCliPublisher>();

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();

// Presence/version beacon for the online storefront: lets nicogo1705.github.io swap its
// "Download app" button for "Open app". Read-only, non-sensitive, hence the open CORS headers.
// Chrome's Private Network Access sends an OPTIONS preflight for public→localhost requests and
// requires Access-Control-Allow-Private-Network — without it the probe silently fails.
var appVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "dev";
app.MapMethods("/api/ping", ["GET", "OPTIONS"], (HttpContext ctx) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    ctx.Response.Headers.AccessControlAllowMethods = "GET";
    return HttpMethods.IsOptions(ctx.Request.Method)
        ? Results.NoContent()
        : Results.Json(new { app = "stride-assetstore", version = appVersion });
});
app.MapRazorComponents<AssetStore.Desktop.Components.App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(ServiceCollectionExtensions).Assembly); // routable pages live in the RCL

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Friendly console banner — this window is all the user sees of the server process.
    Console.WriteLine();
    Console.WriteLine($"  Community Stride Asset Store — desktop app v{appVersion}");
    Console.WriteLine($"  Executable:     {Environment.ProcessPath ?? "(unknown)"}");
    Console.WriteLine($"  Local UI:       {Url}  (opening in your browser…)");
    Console.WriteLine($"  Online store:   {SiteUrlFromRepo(appRepo)}");
    Console.WriteLine($"  Catalog index:  {indexUrl}");

    // Where the app keeps its files — the folder to look at (or wipe) when debugging.
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrideAssetStore");
    Console.WriteLine($"  App data:       {dataDir}  (tracked projects, settings)");
    Console.WriteLine($"  Asset cache:    {AssetStore.Desktop.Services.DesktopInstaller.GlobalCacheRoot}  (shared clones, one subfolder per ref)");
    Console.WriteLine($"  Git:            {(new AssetStore.Core.Git.GitClient().IsAvailable() ? "found on PATH" : "NOT FOUND — installs will fail")}");
    if (launchPath is not null)
    {
        Console.WriteLine($"  Install link:   opening {launchPath}");
    }

    Console.WriteLine();
    Console.WriteLine("  Keep this window open while using the app — Ctrl+C to quit.");
    Console.WriteLine();
    OpenBrowser(Url + (launchPath ?? ""));
});
app.Run();

// The online storefront lives on GitHub Pages of the app repository (config-only override).
static string SiteUrlFromRepo(string repoUrl)
{
    var parts = repoUrl.TrimEnd('/').Split('/');
    return parts.Length >= 2
        ? $"https://{parts[^2].ToLowerInvariant()}.github.io/{parts[^1]}/"
        : repoUrl;
}

static void OpenBrowser(string url)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process.Start("xdg-open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
    }
    catch
    {
        // If the browser can't be launched, the user can open the URL manually.
    }
}
