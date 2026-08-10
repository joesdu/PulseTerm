namespace VelaShell.PluginSdk;

/// <summary>
/// 标注插件包的入口类型。宿主在入口程序集中查找恰好一个带此特性并实现
/// <see cref="IVelaPlugin" /> 的公开类型;找不到或找到多个都会拒绝激活。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class VelaPluginAttribute : Attribute;
