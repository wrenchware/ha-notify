using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HaNotify;

await UpdateChecks.RunAsync();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start();
var port = ((IPEndPoint)probe.LocalEndpoint).Port;
probe.Stop();
using var server = new HttpListener();
server.Prefixes.Add($"http://localhost:{port}/");
server.Start();
var serverTask = Task.Run(async () =>
{
    var registration = await server.GetContextAsync().WaitAsync(deadline.Token);
    Check(registration.Request.RawUrl == "/api/mobile_app/registrations", "registration URL");
    Check(registration.Request.Headers["Authorization"] == "Bearer test-token", "authorization");
    using var body = await JsonDocument.ParseAsync(registration.Request.InputStream, cancellationToken: deadline.Token);
    Check(body.RootElement.GetProperty("app_data").GetProperty("push_websocket_channel").GetBoolean(), "push capability");
    var response = Encoding.UTF8.GetBytes("{\"webhook_id\":\"test-hook\"}");
    registration.Response.ContentType = "application/json";
    await registration.Response.OutputStream.WriteAsync(response, deadline.Token);
    registration.Response.Close();
    // Simulate a restart: established channel closes, then HA rejects authentication,
    // then the mobile_app channel is not loaded yet, then delivery recovers.
    for (var attempt = 0; attempt < 3; attempt++)
    {
        var restarting = await server.GetContextAsync().WaitAsync(deadline.Token);
        using var restartingSocket = (await restarting.AcceptWebSocketAsync(null)).WebSocket;
        await Send(restartingSocket, "{\"type\":\"auth_required\"}");
        using var restartingAuth = await Read(restartingSocket);
        if (attempt == 1)
        {
            await Send(restartingSocket, "{\"type\":\"auth_invalid\",\"message\":\"Unavailable\"}");
            continue;
        }
        await Send(restartingSocket, "{\"type\":\"auth_ok\"}");
        using var restartingSubscription = await Read(restartingSocket);
        Check(restartingSubscription.RootElement.GetProperty("webhook_id").GetString() == "test-hook", "registration reused after restart");
        await Send(restartingSocket, attempt == 0
            ? "{\"id\":1,\"type\":\"result\",\"success\":true}"
            : "{\"id\":1,\"type\":\"result\",\"success\":false,\"error\":{\"code\":\"unknown_command\"}}");
        if (attempt == 0) await restartingSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Restarting", deadline.Token);
    }
    var request = await server.GetContextAsync().WaitAsync(deadline.Token);
    using var socket = (await request.AcceptWebSocketAsync(null)).WebSocket;
    await Send(socket, "{\"type\":\"auth_required\"}");
    using var auth = await Read(socket);
    Check(auth.RootElement.GetProperty("access_token").GetString() == "test-token", "WebSocket authentication");
    await Send(socket, "{\"type\":\"auth_ok\"}");
    using var subscribe = await Read(socket);
    Check(subscribe.RootElement.GetProperty("webhook_id").GetString() == "test-hook", "subscription registration");
    await Send(socket, "{\"id\":1,\"type\":\"result\",\"success\":true}");
    var payload = Encoding.UTF8.GetBytes("{\"id\":1,\"type\":\"event\",\"event\":{\"title\":\"Door & window\",\"message\":\"Hello 🏠\"}}");
    await socket.SendAsync(new ArraySegment<byte>(payload, 0, payload.Length - 5), WebSocketMessageType.Text, false, deadline.Token);
    await socket.SendAsync(new ArraySegment<byte>(payload, payload.Length - 5, 5), WebSocketMessageType.Text, true, deadline.Token);
    await Task.Delay(500, deadline.Token);
});
var settings = await HaConnection.RegisterAsync($"http://localhost:{port}", "test-token", "Test PC");
Check(settings.WebhookId == "test-hook", "saved registration");
var received = new TaskCompletionSource<(string, string)>();
using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
var run = HaConnection.RunAsync(settings, _ => { }, (title, message) => received.TrySetResult((title, message)), stop.Token);
var notification = await received.Task.WaitAsync(deadline.Token);
Check(notification == ("Door & window", "Hello 🏠"), "fragmented notification and Unicode");
stop.Cancel();
await run.WaitAsync(deadline.Token);
await serverTask;
try { await HaConnection.RegisterAsync("file:///test", "token", "PC"); throw new Exception("Invalid scheme accepted"); }
catch (InvalidOperationException) { }
Console.WriteLine("PASS: restart recovery through disconnect, rejected authentication and subscription; registration reuse; fragmented Unicode delivery; cancellation; URL validation.");

void Check(bool value, string label) { if (!value) throw new Exception("Failed: " + label); }
async Task Send(WebSocket socket, string text) => await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, deadline.Token);
async Task<JsonDocument> Read(WebSocket socket)
{
    var buffer = new byte[8192];
    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
    return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
}

namespace HaNotify
{
    internal sealed record Settings(string Url, string Token, string Name, string DeviceId, string WebhookId);
}
