// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using AssetStore.App.Services;

namespace AssetStore.Desktop.Services;

/// <summary>
/// In-place self-update: downloads the release archive for the current OS, extracts it into a
/// versioned folder NEXT TO the current install folder (e.g. <c>..\AssetStore-win-x64-1.3.8\</c>),
/// launches the new build after a short delay (so this instance releases the port first) and
/// stops this process. The UI banner polls <see cref="Stage"/>/<see cref="Percent"/> for its
/// progress bar. Singleton — one update at a time.
/// </summary>
public sealed class SelfUpdater(AppInfo app, IHostApplicationLifetime lifetime)
{
    private int _running;

    /// <summary>idle | downloading | extracting | restarting | error.</summary>
    public string Stage { get; private set; } = "idle";

    public double Percent { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Folder the new build was extracted into.</summary>
    public string? TargetDir { get; private set; }

    /// <summary>Kicks off the update in the background; no-ops when one is already running.</summary>
    public bool TryStart(string tag)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Stage is not "error";
        }

        Stage = "downloading";
        Percent = 0;
        Error = null;
        _ = Task.Run(() => RunAsync(tag));
        return true;
    }

    private async Task RunAsync(string tag)
    {
        try
        {
            var build = DesktopBuilds.Current()
                ?? throw new InvalidOperationException("No published build matches this OS/architecture.");
            var version = tag.TrimStart('v', 'V');
            var url = $"{app.Repo.TrimEnd('/')}/releases/download/{tag}/{build.AssetName}";

            var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var parent = Path.GetDirectoryName(exeDir) ?? exeDir;
            TargetDir = Path.Combine(parent, $"AssetStore-{build.Rid}-{version}");

            // ── Download (0–80%) ──
            var isZip = build.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            var archive = Path.Combine(Path.GetTempPath(), $"AssetStore-{build.Rid}-{version}{(isZip ? ".zip" : ".tar.gz")}");
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("stride-assetstore-desktop");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(archive);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0)
                    {
                        Percent = done * 80.0 / total;
                    }
                }
            }

            // ── Extract (80–95%) ──
            Stage = "extracting";
            Percent = 85;
            if (Directory.Exists(TargetDir))
            {
                Directory.Delete(TargetDir, recursive: true);
            }
            Directory.CreateDirectory(TargetDir);
            if (isZip)
            {
                ZipFile.ExtractToDirectory(archive, TargetDir, overwriteFiles: true);
            }
            else
            {
                await using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, TargetDir, overwriteFiles: true);
            }
            File.Delete(archive);

            var exeName = OperatingSystem.IsWindows() ? "AssetStore.Desktop.exe" : "AssetStore.Desktop";
            var newExe = Directory.EnumerateFiles(TargetDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"{exeName} not found in the downloaded build.");

            // ── Hand over (95–100%) ──
            Stage = "restarting";
            Percent = 100;
            // The new instance can only bind the port once this one is gone → delayed launch.
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{newExe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = TargetDir,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("/bin/sh",
                    $"-c \"sleep 2; nohup '{newExe}' >/dev/null 2>&1 &\"")
                {
                    UseShellExecute = false,
                    WorkingDirectory = TargetDir,
                });
            }

            await Task.Delay(700); // let the banner's poll read the final state
            lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            Stage = "error";
            Error = ex.Message;
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
