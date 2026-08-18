using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 桶配置的 JSON 序列化。
/// <para>
/// **为什么用 JSON 作为配置的中立表示**:S3 的二十多种桶配置各有一套嵌套结构,
/// 若为每一种都在 Core 里造一份 DTO 再手写双向映射,代价是上千行纯搬运代码,
/// 而且每次 AWS 加字段都要跟着改 —— 更糟的是**漏掉的字段会在写回时被静默清空**
/// (读进来渲染不出的字段,序列化回去就没了),那是会真的改坏用户配置的。
/// </para>
/// <para>
/// 直接序列化 SDK 的模型对象则天然完整:读到什么就渲染什么,没渲染的字段原样保留。
/// 界面上的结构化表单只是这份 JSON 的一个视图,两者读的是同一份数据。
/// </para>
/// </summary>
internal static class S3ConfigJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions(indented: true);
    private static readonly JsonSerializerOptions ParseOptions = CreateOptions(indented: false);

    /// <summary>把一个 SDK 配置对象序列化成便于阅读与编辑的 JSON。</summary>
    public static string Serialize<T>(T? value) => value is null ? string.Empty : JsonSerializer.Serialize(value, Options);

    /// <summary>把编辑后的 JSON 还原成 SDK 配置对象。</summary>
    public static T? Deserialize<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, ParseOptions);

    /// <summary>把任意 JSON 文本重新缩进(桶策略是服务端直接给的原始字符串,通常压成一行)。</summary>
    public static string Prettify(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Options);
        }
        catch (JsonException)
        {
            // 服务端给的不是合法 JSON 时原样呈现,总比丢掉强。
            return json;
        }
    }

    /// <summary>把一个单值包装成 <c>{ "名字": 值 }</c>,让只有一个字段的配置也有稳定的文档形状。</summary>
    public static string Wrap(string propertyName, string? value)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (value is null)
            {
                writer.WriteNull(propertyName);
            }
            else
            {
                writer.WriteString(propertyName, value);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>从 <see cref="Wrap" /> 产出的文档里读回那个单值;读不到时返回 null。</summary>
    public static string? Unwrap(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions(bool indented) =>
        new()
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // 策略文档里常见 < > & 等字符,默认的 HTML 转义会把它们写成 <,
            // 用户看到的就不再是自己写进去的那份内容了。
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true,
            Converters = { new ConstantClassConverterFactory() },
        };

    /// <summary>
    /// AWSSDK 的「常量类」(<see cref="ConstantClass" />:S3StorageClass、VersionStatus 等)
    /// 是带 <c>Value</c> 属性的类而不是枚举。默认序列化会写成 <c>{"Value":"Enabled"}</c>,
    /// 既难读也难改;这里统一按字符串收发。
    /// </summary>
    private sealed class ConstantClassConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeof(ConstantClass).IsAssignableFrom(typeToConvert);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ConstantClassConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class ConstantClassConverter<T> : JsonConverter<T> where T : ConstantClass
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            // 容忍旧格式 {"Value":"..."},免得用户手上已经存下的 JSON 片段突然读不回来。
            string? value = reader.TokenType == JsonTokenType.StartObject
                ? ReadValueProperty(ref reader)
                : reader.GetString();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            MethodInfo? find = typeToConvert.GetMethod("FindValue", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            return find is null ? null : (T?)find.Invoke(null, [value]);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value?.Value);

        private static string? ReadValueProperty(ref Utf8JsonReader reader)
        {
            string? found = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName &&
                    string.Equals(reader.GetString(), "Value", StringComparison.OrdinalIgnoreCase))
                {
                    reader.Read();
                    found = reader.GetString();
                }
            }
            return found;
        }
    }
}
