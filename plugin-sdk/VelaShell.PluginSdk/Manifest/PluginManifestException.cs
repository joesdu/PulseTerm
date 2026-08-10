namespace VelaShell.PluginSdk.Manifest;

/// <summary>插件清单缺失、无法解析或未通过校验。<see cref="Exception.Message" /> 给出可读的拒绝原因。</summary>
public sealed class PluginManifestException(string message, Exception? innerException = null)
    : Exception(message, innerException);
