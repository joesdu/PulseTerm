using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.PluginSdk.Rpc;

/// <summary>
/// 线上一帧的载荷:请求(req)/应答(res)/通知(evt)三型。
/// 帧格式:4 字节小端长度前缀 + UTF-8 JSON 正文(见 <see cref="RpcConnection" />)。
/// 刻意不用完整 JSON-RPC 2.0 + StreamJsonRpc:本仓库零依赖纪律优先,
/// 双进程同库同版,无跨语言协商需求(决策注记见 docs/plugins/05 §实现注记)。
/// </summary>
public sealed record RpcMessage
{
    /// <summary>消息类型:"req" / "res" / "evt"。</summary>
    [JsonPropertyName("t")]
    public required string Type { get; init; }

    /// <summary>请求 id;应答携带同 id。通知无 id。</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>方法名(req/evt)。</summary>
    [JsonPropertyName("m")]
    public string? Method { get; init; }

    /// <summary>参数或返回值(JSON 原文)。</summary>
    [JsonPropertyName("p")]
    public JsonElement? Payload { get; init; }

    /// <summary>错误码(res 且失败时),见 <see cref="RpcErrorCodes" />。</summary>
    [JsonPropertyName("c")]
    public string? ErrorCode { get; init; }

    /// <summary>错误消息(res 且失败时)。宿主内部细节不下发,只给安全的 message。</summary>
    [JsonPropertyName("e")]
    public string? Error { get; init; }
}

/// <summary>统一错误码空间与异常映射。</summary>
public static class RpcErrorCodes
{
    /// <summary>能力调用引用的会话不存在或未连接。</summary>
    public const string SessionNotFound = "session-not-found";

    /// <summary>操作超时。</summary>
    public const string Timeout = "timeout";

    /// <summary>无效操作(能力不可用、状态不允许)。</summary>
    public const string InvalidOperation = "invalid-op";

    /// <summary>参数非法。</summary>
    public const string BadArguments = "bad-args";

    /// <summary>握手失败(令牌/版本不符)。</summary>
    public const string HandshakeRejected = "handshake-rejected";

    /// <summary>用户拒绝了敏感能力请求。</summary>
    public const string PermissionDenied = "permission-denied";

    /// <summary>未分类错误。</summary>
    public const string Unknown = "error";

    /// <summary>把异常折叠为线上错误码(细节不跨进程)。</summary>
    public static string FromException(Exception exception) => exception switch
    {
        PluginPermissionDeniedException => PermissionDenied,
        PluginSessionNotFoundException => SessionNotFound,
        TimeoutException => Timeout,
        OperationCanceledException => Timeout,
        ArgumentException => BadArguments,
        InvalidOperationException => InvalidOperation,
        _ => Unknown
    };

    /// <summary>把线上错误码还原为类型化异常(SDK 侧)。</summary>
    public static Exception ToException(string? code, string message) => code switch
    {
        PermissionDenied => new PluginPermissionDeniedException(message),
        SessionNotFound => new PluginSessionNotFoundException("", message),
        Timeout => new TimeoutException(message),
        BadArguments => new ArgumentException(message),
        InvalidOperation => new InvalidOperationException(message),
        _ => new RpcRemoteException(code ?? Unknown, message)
    };
}

/// <summary>对端处理请求时报告的错误(未映射到具体异常类型时的兜底)。</summary>
public sealed class RpcRemoteException(string code, string message) : Exception(message)
{
    /// <summary>线上错误码。</summary>
    public string Code { get; } = code;
}

/// <summary>连接已断开(进程退出、管道关闭);所有未决请求以此失败。</summary>
public sealed class RpcDisconnectedException(string message) : Exception(message);
