namespace VelaShell.PluginSdk;

/// <summary>用户拒绝了插件的敏感能力请求(如终端回写)。插件应体面降级,不要重复骚扰。</summary>
public sealed class PluginPermissionDeniedException(string message) : InvalidOperationException(message);
