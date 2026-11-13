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
    private const string ZipName = "StealYourVoice-win-x64.zip";

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

            // prefer the zip: the exe inside is the same, but the download is less than half the size
            string? url = null, zipUrl = null;
            long size = 0, zipSize = 0;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                if (name == AssetName)
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.GetProperty("size").GetInt64();
                }
                else if (name == ZipName)
                {
                    zipUrl = a.GetProperty("browser_download_url").GetString();
                    zipSize = a.GetProperty("size").GetInt64();
                }
            }
            if (url == null && zipUrl == null) return false;

            var target = ExePath + ".new";
            bool viaZip = zipUrl != null;
            var file = viaZip ? ExePath + ".new.zip" : target;
            long expect = viaZip ? zipSize : size;

            using (var resp = await Http.GetAsync(viaZip ? zipUrl : url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(file);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (expect > 0) progress?.Report((int)(done * 100 / expect));
                }
            }

            if (expect > 0 && new FileInfo(file).Length != expect)
            {
                File.Delete(file);
                return false;
            }

            if (viaZip)
            {
                try
                {
                    using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
                    {
                        var entry = zip.GetEntry(AssetName);
                        if (entry == null) return false;
                        entry.ExtractToFile(target, true);
                        if (new FileInfo(target).Length != entry.Length) { File.Delete(target); return false; }
                    }
                }
                finally { try { File.Delete(file); } catch { /* ignore */ } }
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
