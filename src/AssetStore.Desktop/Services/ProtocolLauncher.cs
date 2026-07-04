// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Diagnostics;
using System.Net.Sockets;

namespace AssetStore.Desktop.Services;

/// <summary>
/// <c>stride-assetstore://</c> protocol support — the bridge between the web storefront and the
/// desktop app: the web Install button opens <c>stride-assetstore://install?id=…</c>, which lands
/// here. Windows-only registration for now (HKCU, no admin); other platforms still work by opening
/// the app manually.
/// </summary>
public static class ProtocolLauncher
{
    public const string Scheme = "stride-assetstore";

    private static readonly HashSet<string> AllowedQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "mode", "ref",
    };

    /// <summary>
    /// Maps a protocol invocation to an app-relative path, or null when the process wasn't started
    /// by one. Only known actions and whitelisted query keys survive (the URL comes from outside).
    /// </summary>
    public static string? ParseLaunchPath(string[] args)
    {
        var raw = args.FirstOrDefault(a => a.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase));
        if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var action = uri.Authority.ToLowerInvariant();
        var query = SanitizeQuery(uri.Query);
        return action switch
        {
            "install" when query.Length > 0 => $"/projects/install?{query}",
            "asset" when query.Length > 0 => $"/asset?{query}",
            _ => null,
        };
    }

    private static string SanitizeQuery(string query)
    {
        var parts = new List<string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (AllowedQueryKeys.Contains(key))
            {
                parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
            }
        }

        return string.Join('&', parts);
    }

    /// <summary>True when another instance of the app already serves on the local port — then the
    /// protocol launch just opens a browser tab there instead of starting a second server.</summary>
    public static bool IsAlreadyRunning(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort registration of the scheme for the current user (HKCU — no admin prompt),
    /// refreshed on every start so the command follows the exe when the app is moved/updated.
    /// </summary>
    public static void TryRegisterWindowsScheme()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            var root = $@"HKCU\Software\Classes\{Scheme}";
            Reg(root, "/ve", "/d", "URL:Community Stride Asset Store", "/f");
            Reg(root, "/v", "URL Protocol", "/d", "", "/f");
            Reg($@"{root}\shell\open\command", "/ve", "/d", $"\"{exe}\" \"%1\"", "/f");
        }
        catch
        {
            // cosmetic convenience — the app works without the protocol
        }
    }

    private static void Reg(params string[] args)
    {
        var info = new ProcessStartInfo("reg") { CreateNoWindow = true, UseShellExecute = false };
        info.ArgumentList.Add("add");
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info);
        process?.WaitForExit(5000);
    }
}
