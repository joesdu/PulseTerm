namespace VelaShell.Services;

/// <summary>
/// Unix 风格远程路径的拼接与取父目录。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>FileBrowserViewModel</c> 拆出来的一簇(Q-01)。远程路径**永远是 <c>/</c> 分隔**,
/// 与本机是不是 Windows 无关 —— 用 <see cref="Path" /> 那一套去处理它,在 Windows 上会拼出
/// <c>/var\log</c> 这种对端不认识的东西。单开一个类型也是为了让这件事在调用点一眼可见。
/// </para>
/// <para>
/// 插件协议(S3、WebDAV)的"路径"同样走这套 —— 它们的键分隔符也是 <c>/</c>。
/// </para>
/// </remarks>
public static class RemotePath
{
    /// <summary>把目录与文件名拼成远程路径。</summary>
    /// <param name="directory">目录(可以是根 <c>/</c>)。</param>
    /// <param name="name">文件或子目录名。</param>
    /// <returns>拼好的路径。</returns>
    public static string Combine(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    /// <summary>
    /// 取父目录;已在根目录时仍返回根。
    /// </summary>
    /// <remarks>
    /// 根目录返回自身而不是 null:调用点是"往上一级"的导航,在根上按它应当原地不动,
    /// 而不是要求每个调用点各判一次空。
    /// </remarks>
    /// <param name="path">远程路径。</param>
    /// <returns>父目录路径。</returns>
    public static string Parent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed[..lastSlash];
    }
}
