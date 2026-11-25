using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaNotify;

internal sealed record HaAction(string Action, string Title, string? Uri);

internal sealed record HaNotification(string Title, string Message, string? Tag, string? Group, string? ClickUri, IReadOnlyList<HaAction> Actions)
{
    public const string DefaultTitle = "Home Assistant";
    public const string DefaultGroup = "home-assistant";
    private const int MaxActions = 5;
    private const int MaxToastKeyLength = 64;

    public bool IsClear => Message == "clear_notification";

    // The one place a toast's identity is worked out. Show and clear must agree, because Windows matches on both values.
    public (string Tag, string Group)? ToastIdentity() =>
        Tag == null ? null : (ToastKey(Tag), ToastKey(Group ?? DefaultGroup));

    // Returns null when the event has no string message. Any other missing or wrong-typed field is skipped.
    public static HaNotification? Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return null;
        var data = payload.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        return new HaNotification(
            Text(payload, "title") ?? DefaultTitle,
            message.GetString()!,
            Text(data, "tag"),
            Text(data, "group"),
            WebUri(Text(data, "clickAction")) ?? WebUri(Text(data, "url")),
            ParseActions(data));
    }

    // Windows limits toast tags and groups to 64 characters. Hashing keeps longer keys stable, so replace and clear still work.
    public static string ToastKey(string key) => key.Length <= MaxToastKeyLength ? key : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string? WebUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri.AbsoluteUri : null;

    public NotificationClick ClickFor(HaAction action, bool includeText) => action.Uri != null
        ? new NotificationClick(null, action.Uri, null, null, null)
        : new NotificationClick(action.Action, null, Tag, includeText && Title != DefaultTitle ? Title : null, includeText ? Message : null);

    private static List<HaAction> ParseActions(JsonElement data)
    {
        var actions = new List<HaAction>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("actions", out var items) || items.ValueKind != JsonValueKind.Array) return actions;
        foreach (var item in items.EnumerateArray())
        {
            if (actions.Count == MaxActions) break;
            var action = Text(item, "action");
            var title = Text(item, "title");
            if (action == null || title == null) continue;
            if (!action.Equals("URI", StringComparison.OrdinalIgnoreCase)) actions.Add(new HaAction(action, title, null));
            else if (WebUri(Text(item, "uri")) is { } uri) actions.Add(new HaAction(action, title, uri));
        }
        return actions;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}

// Everything a toast click needs. It lives in the toast's arguments so a click still works after the app restarts.
internal sealed record NotificationClick(string? Action, string? Uri, string? Tag, string? Title, string? Message)
{
    public const string ArgumentName = "click";
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this, Options));

    // Returns null for anything unreadable or with nothing to do. The URI is checked again here because arguments come from outside the process.
    public static NotificationClick? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var click = JsonSerializer.Deserialize<NotificationClick>(Base64Url.DecodeFromChars(value), Options);
            if (click == null) return null;
            click = click with { Uri = HaNotification.WebUri(click.Uri) };
            return click.Uri == null && string.IsNullOrEmpty(click.Action) ? null : click;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
}
