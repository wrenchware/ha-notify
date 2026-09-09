using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

namespace HaNotify;

internal sealed class HaConnection
{
    public static async Task<Settings> RegisterAsync(string address, string token, string name)
    {
        if (!Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Enter a Home Assistant http:// or https:// address without a query or fragment.");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Enter an access token and device name.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var id = Guid.NewGuid().ToString("N");
        using var response = await client.PostAsJsonAsync("api/mobile_app/registrations", new
        {
            device_id = id, app_id = "personal.ha_notify.windows", app_name = "HA Notify", app_version = "0.1.1",
            device_name = name, manufacturer = "Personal", model = "Windows PC", os_name = "Windows",
            os_version = Environment.OSVersion.Version.ToString(), supports_encryption = false,
            app_data = new { push_websocket_channel = true }
        });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Registration failed (HTTP {(int)response.StatusCode}). Check the address, token, and that the mobile_app integration is enabled.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new Settings(uri.AbsoluteUri, token, name, id, json.RootElement.GetProperty("webhook_id").GetString()!);
    }

    public static async Task RunAsync(Settings settings, Action<string> status, Action<string, string> notify, CancellationToken ct)
    {
        var delay = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                status("Connecting…");
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                var uri = new UriBuilder(new Uri(new Uri(settings.Url), "api/websocket"));
                uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(30));
                await socket.ConnectAsync(uri.Uri, handshake.Token);
                using var hello = await ReceiveAsync(socket, handshake.Token);
                if (hello.RootElement.GetProperty("type").GetString() != "auth_required") throw new IOException("Unexpected authentication response.");
                await SendAsync(socket, new { type = "auth", access_token = settings.Token }, handshake.Token);
                using var auth = await ReceiveAsync(socket, handshake.Token);
                if (auth.RootElement.GetProperty("type").GetString() != "auth_ok")
                    throw new IOException("Authentication unavailable or rejected. Check the token if this persists.");
                await SendAsync(socket, new { id = 1, type = "mobile_app/push_notification_channel", webhook_id = settings.WebhookId }, handshake.Token);
                using var subscription = await ReceiveAsync(socket, handshake.Token);
                if (!subscription.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
                    throw new IOException("Notification channel unavailable. HA may still be starting; check the Mobile App integration if this persists.");
                status("Connected • listening for notifications");
                delay = 2;
                while (!ct.IsCancellationRequested)
                {
                    using var message = await ReceiveAsync(socket, ct);
                    var root = message.RootElement;
                    if (root.GetProperty("type").GetString() != "event" || !root.TryGetProperty("event", out var payload)) continue;
                    if (!payload.TryGetProperty("message", out var body) || body.ValueKind != JsonValueKind.String) continue;
                    var title = payload.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "Home Assistant";
                    notify(title ?? "Home Assistant", body.GetString()!);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
            { status($"{(ex is IOException ? ex.Message : "Connection interrupted (" + ex.GetType().Name + ").")} Retrying in {delay}s…"); }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
            catch (OperationCanceledException) { return; }
            delay = Math.Min(delay * 2, 60);
        }
    }

    private static Task SendAsync(ClientWebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(value)), WebSocketMessageType.Text, true, ct);

    internal static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("WebSocket closed or sent non-text data.");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 1024 * 1024) throw new IOException("Notification exceeded size limit.");
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }
}
