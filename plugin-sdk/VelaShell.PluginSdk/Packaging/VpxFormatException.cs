namespace VelaShell.PluginSdk.Packaging;

/// <summary>
/// <c>.vpx</c> 容器格式错误:魔数不符(多半是把裸 zip 改了后缀)、头部损坏、
/// 载荷长度或摘要对不上、签名无效。消息面向使用者可读。
/// </summary>
public sealed class VpxFormatException : Exception
{
    /// <summary>以消息构造。</summary>
    public VpxFormatException(string message) : base(message)
    {
    }

    /// <summary>以消息与内层异常构造。</summary>
    public VpxFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
