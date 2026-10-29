using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RemoteDesk.Agent.Core;

/// <summary>Константы протокола (см. protocol/PROTOCOL.md).</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int DefaultPort = 47800;
    public const string ServiceType = "_rdesk._tcp";
    public const string AgentVersion = "0.1.0";

    /// <summary>Права, которые выдаются новому телефону. В M0 — все; настройка по устройствам появится позже.</summary>
    public static readonly string[] DefaultPermissions = ["input", "power", "files", "clipboard", "screen"];

    public static class Errors
    {
        public const string BadSecret = "bad_secret";
        public const string SecretExpired = "secret_expired";
        public const string TooManyAttempts = "too_many_attempts";
        public const string NotAuthenticated = "not_authenticated";
        public const string Revoked = "revoked";
        public const string BadMessage = "bad_message";
        public const string UnsupportedVersion = "unsupported_version";
        public const string Forbidden = "forbidden";
        public const string PowerFailed = "power_failed";
    }
}

public sealed record MacInfo(string Mac, string Name, string Ip, bool Wired);

public sealed record AgentInfo(string Id, string Name, string Os, string Version, int Protocol, IReadOnlyList<MacInfo> Macs);

/// <summary>Сборка и разбор JSON-сообщений вида {"t": "...", ...}.</summary>
public static class Message
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Build(string type, params (string Key, object? Value)[] fields)
    {
        var obj = new JsonObject { ["t"] = type };
        foreach (var (key, value) in fields)
        {
            if (value is not null)
                obj[key] = JsonSerializer.SerializeToNode(value, Options);
        }
        return obj.ToJsonString(Options);
    }

    public static string Error(string code, string msg) => Build("error", ("code", code), ("msg", msg));

    /// <summary>Разбирает входящее сообщение. null — не JSON-объект или нет поля t.</summary>
    public static JsonObject? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject obj && obj["t"] is JsonValue ? obj : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Str(this JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Num(this JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    public static double? Dbl(this JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;

    public static bool? Bool(this JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
