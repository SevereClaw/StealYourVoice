using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace StealYourVoice;

/// <summary>
/// Self-updater: checks GitHub Releases, downloads the new exe next to the current one,
/// then swaps them (a running exe can be renamed on Windows) and restarts.
/// </summary>
public sealed class Updater
{
    private const string Api = "https://api.github.com/repos/SevereClaw/StealYourVoice/releases/latest";
    private const string AssetName = "StealYourVoice.exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public Version? Latest { get; private set; }
    public string? ReleaseUrl { get; private set; }
    public bool Ready => _downloadedPath != null && File.Exists(_downloadedPath);

    private string? _downloadedPath;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("StealYourVoice-Updater");
        return c;
    }

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>Remove leftovers of the previous update.</summary>
    public static void CleanupOld()
    {
        var old = ExePath + ".old";
        for (int i = 0; i < 10; i++)
        {
            try { if (File.Exists(old)) File.Delete(old); return; }
            catch { System.Threading.Thread.Sleep(300); }
        }
    }

    /// <summary>Checks for a newer release and downloads it. Returns true if an update is ready to apply.</summary>
    public async Task<bool> CheckAndDownloadAsync(IProgress<int>? progress = null)
    {
        Latest = null;
        try
        {
            if (string.IsNullOrEmpty(ExePath) || !ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(Api));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            ReleaseUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return false;
            Latest = latest;
            if (Normalize(latest) <= Normalize(Current)) return false;

            string? url = null;
            long size = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() == AssetName)
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.GetProperty("size").GetInt64();
                }
            }
            if (url == null) return false;

            var target = ExePath + ".new";
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(target);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (size > 0) progress?.Report((int)(done * 100 / size));
                }
            }

            if (size > 0 && new FileInfo(target).Length != size)
            {
                File.Delete(target);
                return false;
            }

            _downloadedPath = target;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Swap executables and start the new one. Returns false if it could not (e.g. no write access).</summary>
    public bool ApplyAndRestart()
    {
        if (!Ready) return false;
        var exe = ExePath;
        var old = exe + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Move(_downloadedPath!, exe);
            }
            catch
            {
                File.Move(old, exe); // roll back
                throw;
            }

            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
