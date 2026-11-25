using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HaNotify;

const string FridgePayload = """
    {"id":1,"type":"event","event":{
      "title":"Reorder fridge water filters",
      "message":"New fridge, no spare filters yet. Spares on hand: 0.",
      "data":{
        "tag":"fridge-water-filter-reorder",
        "clickAction":"https://www.amazon.com/gp/aws/cart/add.html?ASIN.1=B0CHWKVTH5&Quantity.1=1",
        "actions":[
          {"action":"URI","title":"Add to Amazon cart","uri":"https://www.amazon.com/gp/aws/cart/add.html?ASIN.1=B0CHWKVTH5&Quantity.1=1"},
          {"action":"URI","title":"View product","uri":"https://www.amazon.com/dp/B0CHWKVTH5"},
          {"action":"FRIDGE_FILTER_ORDERED","title":"Ordered (+3)"}
        ]}}}
    """;

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
    await Send(socket, "{\"id\":1,\"type\":\"event\",\"event\":{\"title\":\"No message\",\"data\":{\"tag\":\"skipped\"}}}");
    await Send(socket, "{\"type\":\"event\",\"event\":\"not an object\"}");
    await Send(socket, "{\"not\":\"typed\"}");
    await Send(socket, "{\"id\":1,\"type\":\"event\",\"event\":{\"title\":5,\"message\":\"Wrong types\",\"data\":{\"tag\":5,\"group\":[],\"clickAction\":\"ftp://example.com/\",\"url\":{\"a\":1},\"actions\":\"not an array\"}}}");
    await Send(socket, "{\"id\":1,\"type\":\"event\",\"event\":{\"message\":\"Data not an object\",\"data\":\"text\"}}");
    await Send(socket, "{\"id\":1,\"type\":\"event\",\"event\":{\"message\":\"Bad actions\",\"data\":{\"clickAction\":\"noAction\",\"url\":\"https://example.com/ios\",\"actions\":[1,null,{\"action\":\"NO_TITLE\"},{\"title\":\"No action\"},{\"action\":7,\"title\":\"Number\"},"
        + "{\"action\":\"URI\",\"title\":\"File\",\"uri\":\"file:///C:/Windows/notepad.exe\"},{\"action\":\"URI\",\"title\":\"Relative\",\"uri\":\"/lovelace\"},{\"action\":\"URI\",\"title\":\"Missing\"},"
        + "{\"action\":\"uri\",\"title\":\"Web\",\"uri\":\"http://example.com/a\"},{\"action\":\"A1\",\"title\":\"1\",\"uri\":\"https://example.com/ignored\"},{\"action\":\"A2\",\"title\":\"2\"},{\"action\":\"A3\",\"title\":\"3\"},{\"action\":\"A4\",\"title\":\"4\"},{\"action\":\"A5\",\"title\":\"5\"}]}}}");
    await Send(socket, FridgePayload);
    await Send(socket, "{\"id\":1,\"type\":\"event\",\"event\":{\"message\":\"clear_notification\",\"data\":{\"tag\":\"fridge-water-filter-reorder\"}}}");
    await Task.Delay(500, deadline.Token);

    var webhook = await server.GetContextAsync().WaitAsync(deadline.Token);
    Check(webhook.Request.HttpMethod == "POST", "webhook method");
    Check(webhook.Request.RawUrl == "/api/webhook/test-hook", "webhook URL");
    Check(webhook.Request.Headers["Authorization"] == null, "webhook sends no authorization header");
    Check(webhook.Request.ContentType?.StartsWith("application/json", StringComparison.Ordinal) == true, "webhook content type");
    using (var reader = new StreamReader(webhook.Request.InputStream, Encoding.UTF8))
        Check(await reader.ReadToEndAsync(deadline.Token) == "{\"type\":\"fire_event\",\"data\":{\"event_type\":\"mobile_app_notification_action\",\"event_data\":{"
            + "\"action\":\"FRIDGE_FILTER_ORDERED\",\"tag\":\"fridge-water-filter-reorder\",\"title\":\"Reorder fridge water filters\",\"message\":\"New fridge, no spare filters yet. Spares on hand: 0.\"}}}", "fire_event body");
    webhook.Response.Close();

    var minimal = await server.GetContextAsync().WaitAsync(deadline.Token);
    using (var reader = new StreamReader(minimal.Request.InputStream, Encoding.UTF8))
        Check(await reader.ReadToEndAsync(deadline.Token) == "{\"type\":\"fire_event\",\"data\":{\"event_type\":\"mobile_app_notification_action\",\"event_data\":{\"action\":\"ONLY_ACTION\"}}}", "fire_event optional fields omitted");
    minimal.Response.StatusCode = 500;
    minimal.Response.Close();
});
var settings = await HaConnection.RegisterAsync($"http://localhost:{port}", "test-token", "Test PC");
Check(settings.WebhookId == "test-hook", "saved registration");
var received = Channel.CreateUnbounded<HaNotification>();
using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
var run = HaConnection.RunAsync(settings, _ => { }, n => received.Writer.TryWrite(n), stop.Token);

var notification = await Next();
Check(notification.Title == "Door & window" && notification.Message == "Hello 🏠", "fragmented notification and Unicode");
Check(notification.Tag == null && notification.Group == null && notification.ClickUri == null && notification.Actions.Count == 0 && !notification.IsClear, "plain notification has no extras");

var wrongTypes = await Next();
Check(wrongTypes.Message == "Wrong types" && wrongTypes.Title == HaNotification.DefaultTitle, "wrong-typed title falls back, events without a message are skipped");
Check(wrongTypes.Tag == null && wrongTypes.Group == null && wrongTypes.ClickUri == null && wrongTypes.Actions.Count == 0, "wrong-typed data ignored");
var noData = await Next();
Check(noData.Message == "Data not an object" && noData.Actions.Count == 0, "non-object data ignored");

var badActions = await Next();
Check(badActions.ClickUri == "https://example.com/ios", "unusable clickAction falls back to url");
Check(badActions.Actions.SequenceEqual([new HaAction("uri", "Web", "http://example.com/a"), new HaAction("A1", "1", null), new HaAction("A2", "2", null), new HaAction("A3", "3", null), new HaAction("A4", "4", null)]),
    "bad actions dropped, URI case-insensitive, custom actions carry no URI, five kept");

var fridge = await Next();
Check(fridge.Title == "Reorder fridge water filters" && fridge.Message == "New fridge, no spare filters yet. Spares on hand: 0.", "payload text");
Check(fridge.Tag == "fridge-water-filter-reorder" && fridge.Group == null, "payload tag");
Check(fridge.ClickUri == "https://www.amazon.com/gp/aws/cart/add.html?ASIN.1=B0CHWKVTH5&Quantity.1=1", "payload clickAction");
Check(fridge.Actions.SequenceEqual([
    new HaAction("URI", "Add to Amazon cart", "https://www.amazon.com/gp/aws/cart/add.html?ASIN.1=B0CHWKVTH5&Quantity.1=1"),
    new HaAction("URI", "View product", "https://www.amazon.com/dp/B0CHWKVTH5"),
    new HaAction("FRIDGE_FILTER_ORDERED", "Ordered (+3)", null)]), "payload actions");

var clear = await Next();
Check(clear.IsClear && clear.Tag == "fridge-water-filter-reorder", "clear_notification");
stop.Cancel();
await run.WaitAsync(deadline.Token);

// Clicks survive the round trip through toast arguments, and only safe values come back.
var viewClick = fridge.ClickFor(fridge.Actions[1], true);
Check(viewClick == new NotificationClick(null, "https://www.amazon.com/dp/B0CHWKVTH5", null, null, null), "URI click carries only the URI");
Check(NotificationClick.Decode(viewClick.Encode()) == viewClick, "URI click round trip");
var orderedClick = fridge.ClickFor(fridge.Actions[2], true);
var orderedEncoded = orderedClick.Encode();
Check(orderedEncoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "click encoding needs no escaping in toast arguments");
Check(NotificationClick.Decode(orderedEncoded) == new NotificationClick("FRIDGE_FILTER_ORDERED", null, fridge.Tag, fridge.Title, fridge.Message), "custom click round trip");
Check(fridge.ClickFor(fridge.Actions[2], false) == new NotificationClick("FRIDGE_FILTER_ORDERED", null, fridge.Tag, null, null), "custom click without text");
var unicode = new NotificationClick("A;B=C%", null, "tag 🏠", "\"Title\" & <more>", "Line 1\nLine 2");
Check(NotificationClick.Decode(unicode.Encode()) == unicode, "click round trip with special characters");
Check(noData.ClickFor(new HaAction("A", "B", null), true).Title == null, "default title is not echoed back to HA");
Check(NotificationClick.Decode(new NotificationClick(null, "file:///C:/Windows/notepad.exe", null, null, null).Encode()) == null, "unsafe URI rejected on click");
Check(NotificationClick.Decode(new NotificationClick("A", "javascript:alert(1)", null, null, null).Encode()) == new NotificationClick("A", null, null, null, null), "unsafe URI removed from custom click");
foreach (var garbage in new[] { null, "", "not base64!", "bm90IGpzb24", "bnVsbA", "e30" })
    Check(NotificationClick.Decode(garbage) == null, "unreadable click arguments ignored");

Check(HaNotification.ToastKey("short") == "short" && HaNotification.ToastKey(new string('x', 64)).Length == 64, "short toast keys unchanged");
var longKey = HaNotification.ToastKey(new string('x', 65));
Check(longKey.Length == 64 && longKey == HaNotification.ToastKey(new string('x', 65)) && longKey != HaNotification.ToastKey(new string('y', 65)), "long toast keys hashed stably");

// Show and clear must target the same toast, for the same tag and group, after any hashing.
var tallTag = new string('x', 80);
foreach (var (label, data, expected) in new[]
{
    ("short tag", "{\"tag\":\"ha-notify-test-5\"}", ("ha-notify-test-5", HaNotification.DefaultGroup)),
    ("long tag", "{\"tag\":\"" + tallTag + "\"}", (HaNotification.ToastKey(tallTag), HaNotification.DefaultGroup)),
    ("tag with group", "{\"tag\":\"ha-notify-test-5\",\"group\":\"kitchen\"}", ("ha-notify-test-5", "kitchen"))
})
{
    var shown = ParseEvent("v2", data);
    var cleared = ParseEvent("clear_notification", data);
    Check(!shown.IsClear && cleared.IsClear, $"{label}: show and clear recognised");
    Check(shown.ToastIdentity() == expected, $"{label}: show identity");
    Check(cleared.ToastIdentity() == shown.ToastIdentity(), $"{label}: clear targets the toast show created");
    Check(cleared.ToastIdentity()!.Value.Tag.Length <= 64 && cleared.ToastIdentity()!.Value.Group.Length <= 64, $"{label}: identity fits Windows limits");
}
Check(ParseEvent("clear_notification", "{}").ToastIdentity() == null && ParseEvent("plain", "{}").ToastIdentity() == null, "untagged notifications have no identity");
Check(ParseEvent("clear_notification", "{\"tag\":\"a\"}").ToastIdentity() != ParseEvent("v2", "{\"tag\":\"a\",\"group\":\"kitchen\"}").ToastIdentity(), "tag-only clear of a grouped toast needs the group lookup");

await HaConnection.SendActionAsync(settings, "FRIDGE_FILTER_ORDERED", fridge.Tag, fridge.Title, fridge.Message, deadline.Token);
try { await HaConnection.SendActionAsync(settings, "ONLY_ACTION", null, null, null, deadline.Token); throw new Exception("HTTP failure accepted"); }
catch (IOException ex) { Check(ex.Message.Contains("500") && !ex.Message.Contains("test-hook"), "action failure reports status without webhook id"); }
await serverTask;
try { await HaConnection.RegisterAsync("file:///test", "token", "PC"); throw new Exception("Invalid scheme accepted"); }
catch (InvalidOperationException) { }

// Proxy bypass: saved settings without BypassProxy default to system proxy; BypassProxy=true skips it for registration, WebSocket, and webhooks.
var legacySettings = JsonSerializer.Deserialize<Settings>("{\"Url\":\"http://ha.local/\",\"Token\":\"t\",\"Name\":\"PC\",\"DeviceId\":\"1\",\"WebhookId\":\"w\"}")!;
Check(!legacySettings.BypassProxy, "legacy settings default to using system proxy");

var proxyListener = new TcpListener(IPAddress.Loopback, 0);
proxyListener.Start();
var proxyPort = ((IPEndPoint)proxyListener.LocalEndpoint).Port;
var proxyTask = Task.Run(async () =>
{
    for (var i = 0; i < 2; i++)
    {
        using var client = await proxyListener.AcceptTcpClientAsync(deadline.Token);
        await using var stream = client.GetStream();
        var buf = new byte[1024];
        _ = await stream.ReadAsync(buf, deadline.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
    }
});
var bypassServerTask = Task.Run(async () =>
{
    var reg = await server.GetContextAsync().WaitAsync(deadline.Token);
    reg.Response.ContentType = "application/json";
    await reg.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("{\"webhook_id\":\"bypass-hook\"}"), deadline.Token);
    reg.Response.Close();

    var wsReq = await server.GetContextAsync().WaitAsync(deadline.Token);
    using var ws = (await wsReq.AcceptWebSocketAsync(null)).WebSocket;
    await Send(ws, "{\"type\":\"auth_required\"}");
    using var _auth = await Read(ws);
    await Send(ws, "{\"type\":\"auth_ok\"}");
    using var _sub = await Read(ws);
    await Send(ws, "{\"id\":1,\"type\":\"result\",\"success\":true}");
    await Send(ws, "{\"id\":1,\"type\":\"event\",\"event\":{\"message\":\"Direct\"}}");
    await Task.Delay(300, deadline.Token);

    var hook = await server.GetContextAsync().WaitAsync(deadline.Token);
    hook.Response.Close();
});
var previousProxy = HttpClient.DefaultProxy;
var trapProxy = new TrapProxy(new Uri($"http://127.0.0.1:{proxyPort}"));
HttpClient.DefaultProxy = trapProxy;
try
{
    try { await HaConnection.RegisterAsync($"http://localhost:{port}", "test-token", "Test PC", bypassProxy: false); throw new Exception("Blocked proxy accepted"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("403"), "system proxy mode routes registration through proxy"); }
    var callsAfterReg = trapProxy.Calls;
    Check(callsAfterReg > 0, "system proxy was consulted for registration");

    using var wsProxyStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    var wsProxyHit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var wsProxySettings = new Settings($"http://localhost:{port}/", "test-token", "Test PC", "id", "hook", BypassProxy: false);
    var wsProxyRun = HaConnection.RunAsync(wsProxySettings, s => { if (s.Contains("Retrying", StringComparison.Ordinal)) wsProxyHit.TrySetResult(); }, _ => { }, wsProxyStop.Token);
    await wsProxyHit.Task.WaitAsync(deadline.Token);
    wsProxyStop.Cancel();
    await wsProxyRun.WaitAsync(deadline.Token);
    await proxyTask;
    Check(trapProxy.Calls > callsAfterReg, "system proxy was consulted for WebSocket handshake");

    var callsBeforeBypass = trapProxy.Calls;
    var bypassSettings = await HaConnection.RegisterAsync($"http://localhost:{port}", "test-token", "Test PC", bypassProxy: true);
    Check(bypassSettings.BypassProxy && bypassSettings.WebhookId == "bypass-hook", "bypass registration succeeded");
    var directReceived = Channel.CreateUnbounded<HaNotification>();
    using var directStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
    var directRun = HaConnection.RunAsync(bypassSettings, _ => { }, n => directReceived.Writer.TryWrite(n), directStop.Token);
    var directNotification = await directReceived.Reader.ReadAsync(deadline.Token).AsTask().WaitAsync(deadline.Token);
    Check(directNotification.Message == "Direct", "bypass WebSocket received notification");
    directStop.Cancel();
    await directRun.WaitAsync(deadline.Token);
    await HaConnection.SendActionAsync(bypassSettings, "PING", null, null, null, deadline.Token);
    await bypassServerTask;
    Check(trapProxy.Calls == callsBeforeBypass, "no-proxy mode never consulted system proxy");
}
finally
{
    HttpClient.DefaultProxy = previousProxy;
    proxyListener.Stop();
}

Console.WriteLine("PASS: restart recovery through disconnect, rejected authentication and subscription; registration reuse; fragmented Unicode delivery; notification parsing; click round trip; show and clear identity; action webhook; cancellation; URL validation; proxy bypass.");

HaNotification ParseEvent(string message, string data)
{
    using var json = JsonDocument.Parse("{\"message\":\"" + message + "\",\"data\":" + data + "}");
    return HaNotification.Parse(json.RootElement)!;
}
async Task<HaNotification> Next() => await received.Reader.ReadAsync(deadline.Token).AsTask().WaitAsync(deadline.Token);

void Check(bool value, string label) { if (!value) throw new Exception("Failed: " + label); }
async Task Send(WebSocket socket, string text) => await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, deadline.Token);
async Task<JsonDocument> Read(WebSocket socket)
{
    var buffer = new byte[8192];
    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
    return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
}

sealed class TrapProxy(Uri proxyUri) : IWebProxy
{
    public int Calls;
    public ICredentials? Credentials { get; set; }
    public Uri? GetProxy(Uri destination) { Interlocked.Increment(ref Calls); return proxyUri; }
    public bool IsBypassed(Uri host) => false;
}

namespace HaNotify
{
    internal sealed record Settings(string Url, string Token, string Name, string DeviceId, string WebhookId, bool BypassProxy = false);
}
