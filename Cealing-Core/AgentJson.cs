using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cealing_Core;

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // 枚举按 camelCase 字符串序列化（"builtin"/"external"），而不是默认的数字。
        // 这样手写的配置 JSON（计划 Task 11 的手工验证就是这么写的）和排障时的日志都可读。
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    // System.Text.Json 遇到坏 JSON 是抛 JsonException，不是返回 null。
    // 协议层和调用方都按「解不出来 = null」处理（null → "malformed request"/"malformed config"），
    // 所以这里把 JsonException 收敛成 default。
    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
