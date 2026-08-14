using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 列举/查询结果的内部形态。之所以不直接构造 SDK 的 <see cref="RemoteFileEntry" />:
/// 那是个位置记录,而这里的构造点(桶列表、CommonPrefixes、对象、目录占位)字段各有取舍,
/// 用具名初始化器写出来才看得清"哪一列是空的、为什么空"。转换只在出口发生一次。
/// </summary>
internal sealed class S3FileEntry
{
    /// <summary>名称(不含路径)。</summary>
    public required string Name { get; init; }

    /// <summary>完整远端路径(<c>/bucket/key</c>)。</summary>
    public required string FullPath { get; init; }

    /// <summary>大小(字节);目录为 0。</summary>
    public required long Size { get; init; }

    /// <summary>权限字符串。S3 没有 POSIX 权限位,恒为空串。</summary>
    public required string Permissions { get; init; }

    /// <summary>是否为目录(桶、CommonPrefix,或以 <c>/</c> 结尾的占位对象)。</summary>
    public required bool IsDirectory { get; init; }

    /// <summary>最后修改时间(本地时区);S3 的虚构目录没有修改时间,给 <see cref="DateTime.MinValue" />。</summary>
    public required DateTime LastModified { get; init; }

    /// <summary>属主;仅在服务端返回时非空。</summary>
    public required string Owner { get; init; }

    /// <summary>属组。S3 没有"组"的概念,恒为空串 —— 不拿存储类别去填,那会让列名说谎。</summary>
    public required string Group { get; init; }

    /// <summary>
    /// 转成 SDK 条目。<see cref="DateTime.MinValue" />(= 不知道)映射成 <c>default</c>,
    /// 宿主据此让"修改时间"一列留空;换算它会在 UTC+n 时区上下溢。
    /// </summary>
    /// <returns>SDK 条目。</returns>
    public RemoteFileEntry ToRemoteEntry() =>
        new(Name, FullPath, IsDirectory, Size,
            LastModified == DateTime.MinValue ? default : new DateTimeOffset(LastModified),
            Permissions, Owner, Group);
}
