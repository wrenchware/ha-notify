using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HaNotify;

internal sealed record AppRelease(Version Version, Uri InstallerUrl, string Sha256, long Size);

internal static class AppUpdates
{
    internal static Version CurrentVersion => typeof(AppUpdates).Assembly.GetName().Version!;
    internal static string VersionLabel => CurrentVersion.ToString(3);
    private const long MaximumSize = 300L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HA-Notify/" + VersionLabel);
        return client;
    }

    internal static async Task<AppRelease?> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Client.GetAsync("https://api.github.com/repos/wrenchware/ha-notify/releases/latest", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return ParseRelease(json.RootElement, CurrentVersion);
    }

    internal static AppRelease? ParseRelease(JsonElement root, Version current)
    {
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var number = tag.TrimStart('v');
        if (!Regex.IsMatch(number, @"^\d+\.\d+\.\d+$") || !Version.TryParse(number, out var version)) return null;
        var normalizedCurrent = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        if (version <= normalizedCurrent) return null;
        var name = $"HA-Notify-{number}-Setup-x64.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var expectedUrl = $"https://github.com/wrenchware/ha-notify/releases/download/{tag}/{name}";
            if (asset.GetProperty("browser_download_url").GetString() != expectedUrl)
                throw new InvalidDataException("The update download address is invalid.");
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (!Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("The update is missing a valid checksum.");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumSize) throw new InvalidDataException("The update size is invalid.");
            return new AppRelease(version, new Uri(expectedUrl), digest[7..], size);
        }
        throw new InvalidDataException("The latest release does not contain a Windows installer.");
    }

    internal static async Task<string> DownloadAsync(AppRelease release, IProgress<int> progress, CancellationToken ct, HttpClient? client = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "HaNotify", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "HA-Notify-Setup.exe");
        try
        {
            using var response = await (client ?? Client).GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            int count;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += count;
                if (total > release.Size || total > MaximumSize) throw new InvalidDataException("The update download size does not match.");
                await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                progress.Report((int)(total * 100 / release.Size));
            }
            if (total != release.Size) throw new InvalidDataException("The update download is incomplete.");
            output.Position = 0;
            var hash = await SHA256.HashDataAsync(output, timeout.Token);
            VerifyHash(hash, release.Sha256);
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    internal static void VerifyHash(byte[] actual, string expected)
    {
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
            throw new InvalidDataException("The update checksum does not match. Please try again.");
    }
}
