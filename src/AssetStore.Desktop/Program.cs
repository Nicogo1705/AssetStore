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
builder.Services.AddSingleton<AssetStore.Desktop.Services.SelfUpdater>();

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

// Local-only window controls for the UI's top-bar buttons (the console is the app's only window).
app.MapPost("/console/toggle", () => Results.Json(new { visible = ConsoleWindow.Toggle() }));
// Nav attention dots: things that deserve the user's eye (outdated assets, broken refs).
// Computed on demand — the layout asks once per session, in the background.
app.MapGet("/api/attention", async (
    AssetStore.Desktop.Services.ProjectStore store,
    AssetStore.Desktop.Services.DesktopInstaller installer,
    ICatalogSource source) =>
{
    try
    {
        var index = await source.LoadAsync();
        var catalog = index.Assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var cache = installer.ListCachedAssets(catalog);
        var assetsAttention = cache.Count(c => c.Status is "outdated" or "broken");

        var projectsAttention = 0;
        foreach (var project in store.List().Where(p => p.Exists))
        {
            var view = installer.Analyze(project.Path, catalog);
            projectsAttention += view.Projects.SelectMany(n => n.Assets)
                .Count(a => a.Status is "outdated" or "broken" or "missing");
            projectsAttention += view.Dangling.Count;
        }

        return Results.Json(new { projects = projectsAttention, assets = assetsAttention });
    }
    catch
    {
        return Results.Json(new { projects = 0, assets = 0 });
    }
});
app.MapPost("/app/self-update", (string tag, AssetStore.Desktop.Services.SelfUpdater updater) =>
    Results.Json(new { started = updater.TryStart(tag) }));
app.MapGet("/app/self-update/status", (AssetStore.Desktop.Services.SelfUpdater updater) =>
    Results.Json(new { stage = updater.Stage, percent = updater.Percent, error = updater.Error, target = updater.TargetDir }));
app.MapPost("/app/quit", (IHostApplicationLifetime lifetime) =>
{
    // Graceful stop: flushes and exits the whole process — the guaranteed kill path
    // even when the console window is hidden.
    _ = Task.Run(async () => { await Task.Delay(200); lifetime.StopApplication(); });
    return Results.Json(new { stopping = true });
});

// Console window closed by the user (X / Alt+F4) → same clean shutdown as ⏻.
ConsoleWindow.OnConsoleClosing = () =>
{
    app.Lifetime.StopApplication();
    app.Lifetime.ApplicationStopped.WaitHandle.WaitOne(TimeSpan.FromSeconds(4));
};

app.Lifetime.ApplicationStarted.Register(() =>
{
    // Friendly banner — buffered, and echoed into the on-demand console window.
    ConsoleWindow.Log("");
    ConsoleWindow.Log($"  Community Stride Asset Store — desktop app v{appVersion}");
    ConsoleWindow.Log($"  Executable:     {Environment.ProcessPath ?? "(unknown)"}");
    ConsoleWindow.Log($"  Local UI:       {Url}  (opening in your browser…)");
    ConsoleWindow.Log($"  Online store:   {SiteUrlFromRepo(appRepo)}");
    ConsoleWindow.Log($"  Catalog index:  {indexUrl}");

    // Where the app keeps its files — the folder to look at (or wipe) when debugging.
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StrideAssetStore");
    ConsoleWindow.Log($"  App data:       {dataDir}  (tracked projects, settings)");
    ConsoleWindow.Log($"  Asset cache:    {AssetStore.Desktop.Services.DesktopInstaller.GlobalCacheRoot}  (shared clones, one subfolder per ref)");
    ConsoleWindow.Log($"  Git:            {(new AssetStore.Core.Git.GitClient().IsAvailable() ? "found on PATH" : "NOT FOUND — installs will fail")}");
    if (launchPath is not null)
    {
        ConsoleWindow.Log($"  Install link:   opening {launchPath}");
    }

    var startupMs = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
    ConsoleWindow.Log($"  Started in:     {startupMs} ms");
    ConsoleWindow.Log("");
    ConsoleWindow.Log("  Toggle this console with the 🖥 button in the app's top bar; quit with ⏻.");
    ConsoleWindow.Log("  Closing this window (X / Alt+F4) quits the whole app.");
    ConsoleWindow.Log("");
    OpenBrowser(Url + (launchPath ?? ""));

    // No window by default; reopens at start only if it was open last session.
    ConsoleWindow.ApplySavedState();

    // Catalog stats + update check in the background — the banner never waits on the network.
    _ = Task.Run(async () =>
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("stride-assetstore-desktop");

        try
        {
            var clock = Stopwatch.StartNew();
            var index = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(indexUrl);
            clock.Stop();
            var count = index.TryGetProperty("assets", out var assets) ? assets.GetArrayLength() : 0;
            var generated = index.TryGetProperty("generatedAt", out var g) ? g.GetString() : null;
            ConsoleWindow.Log($"  Catalog:        {count} asset(s), generated {generated ?? "?"} — fetched in {clock.ElapsedMilliseconds} ms");
        }
        catch
        {
            ConsoleWindow.Log("  Catalog:        offline — the app will use its cached copy.");
        }

        try
        {
            var parts = appRepo.TrimEnd('/').Split('/');
            var json = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"https://api.github.com/repos/{parts[^2]}/{parts[^1]}/releases/latest");
            var latestTag = json.GetProperty("tag_name").GetString() ?? "";
            var latest = latestTag.TrimStart('v', 'V');
            if (Version.TryParse(latest, out var l) && Version.TryParse(appVersion, out var current) && l > current)
            {
                Console.WriteLine($"  ⬆ Update available: v{appVersion} → {latestTag} — {appRepo.TrimEnd('/')}/releases/latest");
            }
        }
        catch
        {
            // Offline or rate-limited — the banner simply stays without the update line.
        }
    });
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

/// <summary>
/// The app's on-demand console window (Windows). The process is a WinExe — no console
/// exists at startup; the UI's 🖥 button allocates a real one (AllocConsole) and replays
/// the buffered log, closing frees it (FreeConsole). No hiding/minimizing involved, so it
/// behaves the same under conhost and Windows Terminal. The open/closed state is persisted
/// and re-applied on the next start. ⏻ /app/quit stops the process with or without console.
/// </summary>
static class ConsoleWindow
{
    private static readonly object Gate = new();
    private static readonly List<string> Buffer = [];

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StrideAssetStore", "console.json");

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private delegate bool CtrlHandlerRoutine(uint ctrlType);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(CtrlHandlerRoutine handler, bool add);

    // Kept in a field so the GC never collects the delegate the OS is holding.
    private static CtrlHandlerRoutine? _ctrlHandler;

    /// <summary>Invoked when the user closes the console window (X / Alt+F4). Windows always
    /// terminates the process after a console close — this hook lets the host stop gracefully
    /// (flush, save) inside the ~5s grace period instead of dying mid-write.</summary>
    public static Action? OnConsoleClosing;

    /// <summary>Buffers a banner/status line and echoes it when the console is open.
    /// On non-Windows the process keeps its normal stdout, so lines always print there.</summary>
    public static void Log(string line)
    {
        lock (Gate)
        {
            Buffer.Add(line);
            try
            {
                if (!OperatingSystem.IsWindows() || GetConsoleWindow() != IntPtr.Zero)
                {
                    Console.WriteLine(line);
                }
            }
            catch
            {
                // Writing must never take the app down.
            }
        }
    }

    /// <summary>Opens or closes the console window; returns the new open state.</summary>
    public static bool Toggle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false; // no console window concept to manage — stdout is the terminal's
        }

        lock (Gate)
        {
            if (GetConsoleWindow() != IntPtr.Zero)
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                FreeConsole();
                Save(open: false);
                return false;
            }

            if (!AllocConsole())
            {
                return false;
            }

            // Fresh consoles come up in the OEM codepage (CP850) — UTF-8 text turns into
            // mojibake ("ÔÇö" for —) without this.
            var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            Console.Title = "Community Stride Asset Store — console";
            // Closing an allocated console always terminates the process (no veto possible) —
            // so make it a CLEAN quit: the handler runs the graceful shutdown during the
            // close grace period. CTRL_CLOSE_EVENT = 2; Ctrl+C/Break keep default handling.
            _ctrlHandler ??= ctrlType =>
            {
                if (ctrlType == 2)
                {
                    OnConsoleClosing?.Invoke();
                }
                return false;
            };
            SetConsoleCtrlHandler(_ctrlHandler, true);

            foreach (var line in Buffer)
            {
                Console.WriteLine(line);
            }

            Save(open: true);
            return true;
        }
    }

    /// <summary>Reopens the console at startup when it was open last session (default: closed).</summary>
    public static void ApplySavedState()
    {
        try
        {
            if (OperatingSystem.IsWindows()
                && File.Exists(StateFile)
                && File.ReadAllText(StateFile).Contains("\"open\":true", StringComparison.OrdinalIgnoreCase))
            {
                Toggle();
            }
        }
        catch
        {
            // Unreadable state — stay windowless, the default.
        }
    }

    private static void Save(bool open)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, $"{{\"open\":{(open ? "true" : "false")}}}");
        }
        catch
        {
            // Not persisting is harmless.
        }
    }
}
