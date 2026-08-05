namespace VelaShell.ViewModels;

/// <summary>
/// 冲突策略为“询问”时,用户对单个同名文件的处置选择。批量传输(拖入上千文件的文件夹)
/// 时新增 <see cref="OverwriteAll" />/<see cref="SkipAll" />:一次决定沿用到本批次其余所有
/// 冲突,免去逐文件弹窗(WinSCP/FileZilla 式的“全部覆盖/全部跳过”)。
/// </summary>
public enum FileConflictResolution
{
    /// <summary>覆盖此文件。</summary>
    Overwrite,

    /// <summary>跳过此文件。</summary>
    Skip,

    /// <summary>覆盖此文件,并对本批次其余所有冲突一律覆盖(不再逐个询问)。</summary>
    OverwriteAll,

    /// <summary>跳过此文件,并对本批次其余所有冲突一律跳过(不再逐个询问)。</summary>
    SkipAll
}
