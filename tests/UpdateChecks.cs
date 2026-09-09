using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaNotify;

internal static class UpdateChecks
{
    internal static async Task RunAsync()
    {
        var data = Encoding.UTF8.GetBytes("test installer bytes");
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var url = "https://github.com/wrenchware/ha-notify/releases/download/v0.1.10/HA-Notify-0.1.10-Setup-x64.exe";
        JsonDocument Release(string digest, string downloadUrl, bool prerelease = false, bool draft = false) => JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            tag_name = "v0.1.10", prerelease, draft,
            assets = new[] { new { name = "HA-Notify-0.1.10-Setup-x64.exe", browser_download_url = downloadUrl, digest, size = data.Length } }
        }));
        using var valid = Release("sha256:" + hash, url);
        var release = AppUpdates.ParseRelease(valid.RootElement, new Version(0, 1, 2, 0)) ?? throw new Exception("Numeric version comparison failed.");
        if (AppUpdates.ParseRelease(valid.RootElement, new Version(0, 1, 10, 0)) != null) throw new Exception("Same version offered as update.");
        if (AppUpdates.ParseRelease(valid.RootElement, new Version(0, 2, 0)) != null) throw new Exception("Downgrade offered.");
        using var prerelease = Release("sha256:" + hash, url, true);
        using var draft = Release("sha256:" + hash, url, false, true);
        if (AppUpdates.ParseRelease(prerelease.RootElement, new Version(0, 1, 0)) != null || AppUpdates.ParseRelease(draft.RootElement, new Version(0, 1, 0)) != null) throw new Exception("Unpublished/staged release offered.");
        using var untrusted = Release("sha256:" + hash, "https://example.com/setup.exe");
        ExpectInvalid(() => AppUpdates.ParseRelease(untrusted.RootElement, new Version(0, 1, 0)));
        using var missingHash = Release("", url);
        ExpectInvalid(() => AppUpdates.ParseRelease(missingHash.RootElement, new Version(0, 1, 0)));
        using var client = new HttpClient(new PayloadHandler(data));
        var path = await AppUpdates.DownloadAsync(release, new Progress<int>(), CancellationToken.None, client);
        if (!File.ReadAllBytes(path).SequenceEqual(data)) throw new Exception("Downloaded payload differs.");
        File.Delete(path);
        await ExpectDownloadFailure(release with { Sha256 = new string('0', 64) }, client);
        await ExpectDownloadFailure(release with { Size = data.Length + 1 }, client);
        await ExpectDownloadFailure(release with { Size = data.Length - 1 }, client);
        Console.WriteLine("PASS: update version comparison, release filtering, download origin, checksum validation, and download length checks.");
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid update accepted.");
    }
    private static async Task ExpectDownloadFailure(AppRelease release, HttpClient client)
    {
        try { await AppUpdates.DownloadAsync(release, new Progress<int>(), CancellationToken.None, client); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid download accepted.");
    }
    private sealed class PayloadHandler(byte[] data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }
}
