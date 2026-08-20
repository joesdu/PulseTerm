using VelaShell.PluginSdk.Packaging;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>插件包发布者信任状态及其可人工核对的公钥指纹。</summary>
public sealed record PluginPackageTrustInfo(VpxSignatureState State, string? PublisherFingerprint);
