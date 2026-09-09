using System.Security.Cryptography;
using System.Text.Json;

namespace HaNotify;

internal sealed record Settings(string Url, string Token, string Name, string DeviceId, string WebhookId)
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaNotify");
    private static string FileName => Path.Combine(Folder, "connection.dat");
    public static Settings? Load() => !File.Exists(FileName) ? null : JsonSerializer.Deserialize<Settings>(
        ProtectedData.Unprotect(File.ReadAllBytes(FileName), null, DataProtectionScope.CurrentUser));
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FileName + ".tmp", bytes);
        File.Move(FileName + ".tmp", FileName, true);
    }
}
