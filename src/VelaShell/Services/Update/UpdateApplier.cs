using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Services.Update;

/// <summary>
/// 便携式原地升级的文件操作核心。换版分两步、可由两个进程分别执行:
/// <list type="number">
/// <item><see cref="Stage" />:把更新包完整解到暂存目录的 <c>payload/</c>,此时应用目录一个字节都没动。</item>
/// <item><see cref="SwapFromPayload" />:现有文件移入 <c>backup/</c>,<c>payload/</c> 里的文件复制到位。</item>
/// </list>
/// 旧文件挪进 <c>backup/</c> 而不是就地改名成 <c>*.old</c>,是这套设计的关键:整个备份目录一次性
/// 删除即可收尾,不必逐个文件记账;删不掉时它待在暂存目录里也碍不着任何人,应用目录始终干净。
/// 正常路径下 <see cref="SwapFromPayload" /> 由退出后才启动的外置换版进程执行
/// (见 <see cref="UpdateRunner" />),那时应用目录无任何文件被占用,覆盖必然成功;
/// 只有包里没有主程序或外置进程拉不起来时才退回本进程原地换版(Windows 允许对正在运行的
/// exe/dll 改名,只是不许删除/覆盖,三平台同一套逻辑)。
/// <para>
/// <b>新文件用复制而非移动进应用目录</b>:外置换版进程跑的就是 <c>payload/</c> 里那份完整新版应用
/// (见 <see cref="TryHandOffToExternalUpdater" />),搬走 <c>payload/</c> 的文件等于抽掉它脚下的
/// 地板 —— 换版途中才需要装载的程序集会突然找不到。复制留下的 <c>payload/</c> 由下次启动的
/// <see cref="TryFinalizeStartup" /> 清掉,顺带让"换版失败后重试"不必重新解包。
/// </para>
/// 全程只触碰更新包内列出的文件,应用目录里用户自己的文件绝不改名或删除;
/// 应用数据目录(%LocalAppData%/VelaShell)与本流程无关,永不触碰。
/// 进度记录在暂存目录的日志文件里,中途崩溃由下次启动的 <see cref="TryFinalizeStartup" />
/// 依据日志回滚或收尾。
/// </summary>
public sealed class UpdateApplier(string applicationDirectory)
{
    /// <summary>暂存目录名(位于应用目录下,保证与目标文件同卷,移动是纯元数据操作)。</summary>
    public const string StagingDirectoryName = ".velashell-update";

    /// <summary>换版失败原因的落盘文件名(位于应用目录,供下次启动后在设置页如实展示)。</summary>
    public const string ErrorFileName = ".velashell-update-error";

    /// <summary>
    /// 外置换版进程临时目录的名字前缀(位于系统临时目录,按前缀清扫遗留)。
    /// 现在的更新器就地跑在 <see cref="PayloadDirectory" /> 里、不再建临时目录,这个前缀
    /// 只用于清扫 1.1.x 及更早版本留在系统临时目录里的更新器副本。
    /// </summary>
    internal const string UpdaterDirectoryPrefix = "velashell-updater-";

    /// <summary>换版日志文件名(位于暂存目录);下载流程清理残留时须避开它。</summary>
    public const string JournalFileName = "apply.json";

    private const string PayloadDirectoryName = "payload";
    private const string BackupDirectoryName = "backup";

    /// <summary>外置换版进程临时目录的保留时长;超过此年龄的遗留目录在启动时清扫。</summary>
    private static readonly TimeSpan UpdaterDirectoryMaxAge = TimeSpan.FromHours(6);

    /// <summary>应用程序所在目录(更新的目标目录)。</summary>
    public string ApplicationDirectory { get; } =
        Path.GetFullPath(applicationDirectory ?? throw new ArgumentNullException(nameof(applicationDirectory)));

    /// <summary>下载产物、解包内容与换版日志的暂存目录。</summary>
    public string StagingDirectory => Path.Combine(ApplicationDirectory, StagingDirectoryName);

    /// <summary>解包后的新版完整目录树;换版即把它按相对路径移进应用目录。</summary>
    public string PayloadDirectory => Path.Combine(StagingDirectory, PayloadDirectoryName);

    /// <summary>被换下的旧版文件(按相对路径镜像);回滚的唯一依据,收尾时整个删除。</summary>
    public string BackupDirectory => Path.Combine(StagingDirectory, BackupDirectoryName);

    private string JournalPath => Path.Combine(StagingDirectory, JournalFileName);

    private string ErrorPath => Path.Combine(ApplicationDirectory, ErrorFileName);

    /// <summary>应用目录是否可写(装进 Program Files 之类的位置时为 false,只能手动更新)。</summary>
    public bool IsApplicationDirectoryWritable()
    {
        try
        {
            string probe = Path.Combine(ApplicationDirectory, $".velashell-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建(或清空后重建)暂存目录,返回其路径。</summary>
    public string PrepareStagingDirectory()
    {
        if (Directory.Exists(StagingDirectory))
        {
            Directory.Delete(StagingDirectory, true);
        }
        return EnsureStagingDirectory();
    }

    /// <summary>
    /// 确保暂存目录存在但不清空已有内容(半成品与已下载的包留给断点续传/复用),
    /// 返回其路径。下载前调用,与本次更新无关的残留由调用方按文件名清理。
    /// </summary>
    public string EnsureStagingDirectory()
    {
        DirectoryInfo dir = Directory.CreateDirectory(StagingDirectory);
        if (OperatingSystem.IsWindows())
        {
            dir.Attributes |= FileAttributes.Hidden;
        }
        return StagingDirectory;
    }

    // ———————————————————— 第一步:解包 ————————————————————

    /// <summary>
    /// 把更新包完整解到 <see cref="PayloadDirectory" /> 并写下换版日志(阶段 <c>staged</c>),
    /// 返回包内文件的相对路径清单。<b>本方法不改动应用目录里的任何文件</b> —— 磁盘不足、
    /// 包损坏、路径可疑等失败都在这里暴露,失败后应用保持原样,重试即可,不会留下半换版现场。
    /// </summary>
    public IReadOnlyList<string> Stage(string archivePath)
    {
        EnsureStagingDirectory();
        // 上一轮的解包/备份残留必须先清干净,否则回滚会拿旧备份覆盖新版本。
        // 清不掉就直接抛:此时应用目录尚未被触碰,用户可用"修复更新状态"强制重置。
        DeleteDirectory(PayloadDirectory);
        DeleteDirectory(BackupDirectory);
        Directory.CreateDirectory(PayloadDirectory);

        List<string> entries = IsZip(archivePath)
            ? ExtractZipToPayload(archivePath)
            : ExtractTarGzToPayload(archivePath);
        if (entries.Count == 0)
        {
            throw new InvalidDataException($"Update package contains no files: {archivePath}");
        }

        UpdateJournal journal = new()
        {
            Phase = UpdateJournal.PhaseStaged,
            Files = [.. entries.Select(path => new UpdateJournalFile
            {
                Path = path,
                Existed = File.Exists(Path.Combine(ApplicationDirectory, path))
            })]
        };
        WriteJournal(journal);
        return entries;
    }

    // ———————————————————— 第二步:换版 ————————————————————

    /// <summary>
    /// 按换版日志把 <see cref="PayloadDirectory" /> 里的文件复制进应用目录,被换下的旧文件
    /// 移入 <see cref="BackupDirectory" />。任一步失败即就地回滚后重新抛出异常。
    /// 调用前必须先 <see cref="Stage" />(可由另一个进程完成)。
    /// </summary>
    public void SwapFromPayload()
    {
        UpdateJournal journal = ReadJournal()
            ?? throw new InvalidOperationException("No staged update found; call Stage first.");
        if (journal.Phase == UpdateJournal.PhaseDone)
        {
            return; // 已换完,等收尾即可(外置进程重入时走这里)。
        }
        Directory.CreateDirectory(BackupDirectory);
        journal.Phase = UpdateJournal.PhaseApplying;
        WriteJournal(journal);
        try
        {
            foreach (UpdateJournalFile file in journal.Files)
            {
                string target = Path.Combine(ApplicationDirectory, file.Path);
                string staged = Path.Combine(PayloadDirectory, file.Path);
                if (!File.Exists(staged))
                {
                    throw new InvalidDataException($"Staged update file missing: {file.Path}");
                }
                if (File.Exists(target))
                {
                    string backup = Path.Combine(BackupDirectory, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup, true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // 复制而非移动:见类注释——本方法多半正跑在 payload/ 里的那份新版应用中。
                // Unix 下 File.Copy 会把源文件的权限位一并带过去,可执行位不丢。
                File.Copy(staged, target, true);
            }
            journal.Phase = UpdateJournal.PhaseDone;
            WriteJournal(journal);
        }
        catch
        {
            // 回滚后日志停在 applying。payload/ 仍是完整的(复制不消耗它),重试无需重新解包;
            // 而下次启动的 TryFinalizeStartup 再跑一遍回滚是幂等的空操作。
            Rollback(journal);
            throw;
        }
    }

    /// <summary>解包 + 换版一步到位(本进程原地换版的兜底路径,亦供测试使用)。</summary>
    public void Apply(string archivePath)
    {
        Stage(archivePath);
        SwapFromPayload();
    }

    // ———————————————————— 交接给外置换版进程 ————————————————————

    /// <summary>
    /// 把换版交给一个独立进程:<see cref="PayloadDirectory" /> 里躺着的就是一份解包完毕、
    /// 自成一体的新版应用,直接把它的主程序跑起来当更新器 —— 无需随包分发额外的可执行文件,
    /// 跑的就是用户马上要用的那个二进制。它会等本进程(<paramref name="parentProcessId" />)
    /// 退出后再换版,那时应用目录里没有任何文件被占用,覆盖必然成功,也就不会留下删不掉的残骸。
    /// </summary>
    /// <remarks>
    /// 早先的做法是把新版主程序复制一份到系统临时目录再跑 —— 那依赖"主程序单文件即完整可执行体"。
    /// 发布形态改回摊开后(为让隔离插件的 PluginHost 在磁盘上有真实可执行体,见 VelaShell.csproj),
    /// 单个 exe 离开自己的目录就跑不起来,于是改为就地在 payload/ 里启动:它身边正是完整的一套
    /// 运行时与依赖。代价是换版必须用复制而非移动(见 <see cref="SwapFromPayload" />)。
    /// </remarks>
    /// <returns>
    /// true 表示外置进程已拉起,调用方随即退出应用即可;false 表示条件不满足
    /// (包里没有主程序、进程拉不起来),调用方应退回本进程原地换版。
    /// </returns>
    public bool TryHandOffToExternalUpdater(int parentProcessId)
    {
        UpdateJournal? journal = ReadJournal();
        if (journal is not { Phase: UpdateJournal.PhaseStaged })
        {
            return false;
        }
        if (Path.GetFileName(Environment.ProcessPath) is not { Length: > 0 } launcherName)
        {
            return false;
        }
        string payloadLauncher = Path.Combine(PayloadDirectory, launcherName);
        if (!File.Exists(payloadLauncher))
        {
            return false;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // tar 包解出来已带可执行位,这里兜一道底(zip 包不带权限位)。
                File.SetUnixFileMode(payloadLauncher, File.GetUnixFileMode(payloadLauncher)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            // 先记账再拉起:进程起来后本进程随即退出,来不及再写日志。
            journal.Phase = UpdateJournal.PhaseHandoff;
            journal.UpdaterDirectory = null;
            WriteJournal(journal);
            Process.Start(new ProcessStartInfo(payloadLauncher)
            {
                WorkingDirectory = PayloadDirectory,
                UseShellExecute = false,
                ArgumentList =
                {
                    UpdateRunner.ApplyUpdateSwitch,
                    UpdateRunner.TargetSwitch, ApplicationDirectory,
                    UpdateRunner.WaitPidSwitch, parentProcessId.ToString()
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] External updater hand-off failed, falling back in-process: {ex.Message}");
            journal.Phase = UpdateJournal.PhaseStaged;
            journal.UpdaterDirectory = null;
            TryWriteJournal(journal);
            return false;
        }
    }

    // ———————————————————— 收尾与自愈 ————————————————————

    /// <summary>
    /// 启动期收尾:按日志回滚上一轮没换完的换版,或清掉换版成功后的备份;顺带清扫外置更新器
    /// 的临时目录与历史版本遗留的 <c>*.old</c>。已下载的更新包保留给断点续传/免下载复用。
    /// 全部处理干净返回 true;返回 false 表示还有占用中的残留,调用方可稍后重试。绝不抛出异常。
    /// </summary>
    public bool TryFinalizeStartup()
    {
        try
        {
            // 历史遗留 .old 与换版记账互不相干,各自成败分开算。
            bool legacyClean = TryPurgeLegacyOldFiles();
            UpdateJournal? journal = ReadJournal();
            if (journal == null)
            {
                // 无日志:暂存目录里只可能有下载产物(完整包或 .partial 半成品),
                // 保留原样供下次检查更新时断点续传或免下载复用;过期残留由
                // 下次下载按文件名清理(产物名带版本号与 RID,可精确识别)。
                return legacyClean & TrySweepUpdaterDirectories(null);
            }
            // staged/handoff:应用目录未被触碰,回滚是空操作,只需清掉解包内容;
            // applying:换到一半,按备份还原。两种情形同一套幂等逻辑。
            if (journal.Phase != UpdateJournal.PhaseDone && !Rollback(journal))
            {
                // 回滚没做完,backup 是还原旧版的唯一依据,连同日志原样留着下次启动继续 ——
                // 这里少一个判断就会把用户的旧版本删干净,还原也就无从谈起了。
                return false;
            }
            bool swapClean = TrySweepUpdaterDirectories(journal.UpdaterDirectory);
            swapClean &= TryDeleteDirectory(PayloadDirectory);
            swapClean &= TryDeleteDirectory(BackupDirectory);
            if (swapClean)
            {
                // 日志是"还有待办"的唯一凭据,必须等到手上的活全干完才销毁。
                TryDeleteFile(JournalPath);
                TryDeleteStagingIfEmpty();
            }
            return legacyClean && swapClean;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update finalize failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 强制重置更新状态(设置页“修复更新状态”):回滚未完成的换版,清掉暂存目录(连同已下载的包,
    /// 下次重新下载)、外置更新器临时目录、历史 <c>*.old</c> 与失败标记。绝不抛出异常;
    /// 返回 false 表示仍有文件被占用,关掉占用者后再试即可。
    /// </summary>
    public bool TryRepair()
    {
        try
        {
            UpdateJournal? journal = ReadJournal();
            bool rolledBack = true;
            if (journal is { Phase: not UpdateJournal.PhaseDone })
            {
                rolledBack = Rollback(journal);
            }
            bool clean = TrySweepUpdaterDirectories(journal?.UpdaterDirectory);
            clean &= TryPurgeLegacyOldFiles();
            if (!rolledBack)
            {
                // 还没还原到旧版就清空暂存目录,等于把旧版本的唯一副本一起销毁。
                // 修复的目的是恢复可用状态,不是把用户仅剩的退路也删掉。
                return false;
            }
            clean &= TryDeleteDirectory(StagingDirectory);
            ClearLastError();
            return clean;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update repair failed: {ex}");
            return false;
        }
    }

    /// <summary>上一次换版失败的原因;没有失败记录时返回 null。</summary>
    public string? ReadLastError()
    {
        try
        {
            if (!File.Exists(ErrorPath))
            {
                return null;
            }
            string text = File.ReadAllText(ErrorPath).Trim();
            return text.Length > 0 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>记下换版失败原因,供下次启动后在设置页如实展示。绝不抛出异常。</summary>
    public void WriteLastError(string message)
    {
        try
        {
            File.WriteAllText(ErrorPath, message);
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(ErrorPath, FileAttributes.Hidden);
            }
        }
        catch
        {
            // 失败标记只用于提示,写不进去不影响换版结果。
        }
    }

    /// <summary>清掉换版失败标记。</summary>
    public void ClearLastError() => TryDeleteFile(ErrorPath);

    /// <summary>
    /// 依据日志回滚:凡 <c>backup/</c> 里有备份的,删掉换入的新文件并把备份移回原位;
    /// 换版前不存在的新增文件直接删除。幂等,可重复执行。
    /// </summary>
    private bool Rollback(UpdateJournal journal)
    {
        bool clean = true;
        foreach (UpdateJournalFile file in journal.Files)
        {
            string target = Path.Combine(ApplicationDirectory, file.Path);
            string backup = Path.Combine(BackupDirectory, file.Path);
            try
            {
                if (File.Exists(backup))
                {
                    if (File.Exists(target) && !TryRemoveFile(target))
                    {
                        clean = false;
                        continue; // 位置腾不出来,备份原样留着,下次启动再试。
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(backup, target);
                }
                else if (!file.Existed && File.Exists(target))
                {
                    clean &= TryRemoveFile(target);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VelaShell] Update rollback failed for {file.Path}: {ex}");
                clean = false;
            }
        }
        return clean;
    }

    /// <summary>
    /// 清扫历史版本(旧换版机制)遗留在应用目录里的 <c>*.old</c>。只删“去掉后缀后同名文件确实存在”
    /// 的那些 —— 那是换版残骸的确凿特征,用户自己的 <c>.old</c> 文件不会被误伤。
    /// </summary>
    private bool TryPurgeLegacyOldFiles()
    {
        bool clean = true;
        try
        {
            foreach (string path in Directory.EnumerateFiles(ApplicationDirectory, "*.old", SearchOption.AllDirectories))
            {
                if (path.StartsWith(StagingDirectory, PathComparison) || !File.Exists(path[..^4]))
                {
                    continue;
                }
                clean &= TryDeleteFile(path);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Legacy .old sweep failed: {ex.Message}");
            return false;
        }
        return clean;
    }

    /// <summary>
    /// 清扫外置更新器的临时目录:日志点名的那个必删(它刚跑完),其余同前缀目录超过
    /// <see cref="UpdaterDirectoryMaxAge" /> 才删 —— 免得误伤另一个实例正在进行的换版。
    /// 新版更新器就地跑在 payload/ 里、不再建临时目录,日志里的 UpdaterDirectory 恒为 null;
    /// 这段留着是为了收拾从旧版本升上来时残留的那一份。
    /// </summary>
    private static bool TrySweepUpdaterDirectories(string? current)
    {
        bool clean = true;
        if (!string.IsNullOrEmpty(current))
        {
            // 更新器进程可能还在退出途中(它刚把我们拉起来),删不掉留给下一轮重试。
            clean &= TryDeleteDirectory(current);
        }
        try
        {
            DateTime cutoff = DateTime.UtcNow - UpdaterDirectoryMaxAge;
            foreach (string dir in Directory.EnumerateDirectories(Path.GetTempPath(), UpdaterDirectoryPrefix + "*"))
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                {
                    TryDeleteDirectory(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Updater temp sweep failed: {ex.Message}");
        }
        return clean;
    }

    // ———————————————————— 解包 ————————————————————

    private static bool IsZip(string archivePath) =>
        archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>解 zip 到 <c>payload/</c>:先读中央目录枚举(几乎免费,顺带校验路径与预估占用),再解压。</summary>
    private List<string> ExtractZipToPayload(string archivePath)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        List<string> order = [];
        Dictionary<string, ZipArchiveEntry> byPath = [with(StringComparer.OrdinalIgnoreCase)];
        long required = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (NormalizeEntryPath(entry.FullName) is not { } rel)
            {
                continue;
            }
            EnsureInsideApplicationDirectory(rel, entry.FullName);
            if (byPath.TryAdd(rel, entry))
            {
                order.Add(rel);
                required += entry.Length;
            }
        }
        EnsureDiskSpace(required);
        foreach (string rel in order)
        {
            string destination = Path.Combine(PayloadDirectory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            byPath[rel].ExtractToFile(destination, true);
        }
        return order;
    }

    /// <summary>
    /// 解 tar.gz 到 <c>payload/</c>。tar 的条目枚举本身就要整流解压,故单遍完成:
    /// 边枚举边解,不做"枚举一遍 + 解压一遍"的双倍功。可执行位由 TarEntry 解包时还原。
    /// </summary>
    private List<string> ExtractTarGzToPayload(string archivePath)
    {
        if (!archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            && !archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported update package format: {archivePath}");
        }
        // 压缩包大小的 4 倍是个保守的解压后估算;真解爆了也只是 payload 写失败,应用目录不受影响。
        EnsureDiskSpace(new FileInfo(archivePath).Length * 4);
        List<string> entries = [];
        HashSet<string> seen = [with(StringComparer.OrdinalIgnoreCase)];
        using FileStream file = File.OpenRead(archivePath);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using TarReader tar = new(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || NormalizeEntryPath(entry.Name) is not { } rel)
            {
                continue;
            }
            EnsureInsideApplicationDirectory(rel, entry.Name);
            if (!seen.Add(rel))
            {
                continue;
            }
            string destination = Path.Combine(PayloadDirectory, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, true);
            entries.Add(rel);
        }
        return entries;
    }

    /// <summary>
    /// 换版全程磁盘上会同时存在三份:应用目录里的旧版(换版时原地挪进 <c>backup/</c>,不额外占地)、
    /// 解包出来的 <c>payload/</c>,以及复制进应用目录的那份新版。相对现状净增两倍包体,
    /// 故要求两倍余量。空间不够就在解包前明确报错,而不是解到一半才炸在某个文件上。
    /// 查不到磁盘信息(异常文件系统)时放行。
    /// </summary>
    private void EnsureDiskSpace(long payloadBytes)
    {
        if (payloadBytes <= 0)
        {
            return;
        }
        long available;
        try
        {
            available = new DriveInfo(Path.GetPathRoot(ApplicationDirectory)!).AvailableFreeSpace;
        }
        catch
        {
            return;
        }
        long required = payloadBytes * 2;
        if (available < required)
        {
            throw new IOException(
                $"Not enough disk space to install the update: {required / 1024 / 1024} MB required, "
                + $"{available / 1024 / 1024} MB available on {ApplicationDirectory}.");
        }
    }

    /// <summary>
    /// 归一化包内条目路径:统一斜杠、去掉引导 "./";目录条目、空路径返回 null,绝对路径与
    /// 含 ".." 的路径抛异常,落在暂存目录内的条目忽略。
    /// </summary>
    private static string? NormalizeEntryPath(string rawPath)
    {
        string path = rawPath.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        if (path.Length == 0 || path.EndsWith('/'))
        {
            return null;
        }
        string[] segments = path.Split('/');
        if (Path.IsPathRooted(path) || segments.Any(s => s is "" or "." or ".."))
        {
            throw new InvalidDataException($"Update package contains a suspicious entry path: {rawPath}");
        }
        // 更新器自己的暂存目录不属于应用文件,包里出现同名路径直接忽略。
        if (segments[0].Equals(StagingDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.Combine(segments);
    }

    private void EnsureInsideApplicationDirectory(string relativePath, string rawPath)
    {
        string full = Path.GetFullPath(Path.Combine(ApplicationDirectory, relativePath));
        string root = ApplicationDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? ApplicationDirectory
            : ApplicationDirectory + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, PathComparison))
        {
            throw new InvalidDataException($"Update package entry escapes the application directory: {rawPath}");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // ———————————————————— 日志与文件小工具 ————————————————————

    private void WriteJournal(UpdateJournal journal)
    {
        Directory.CreateDirectory(StagingDirectory);
        string tmp = JournalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, UpdateJournalContext.Default.UpdateJournal));
        File.Move(tmp, JournalPath, true);
    }

    private void TryWriteJournal(UpdateJournal journal)
    {
        try
        {
            WriteJournal(journal);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update journal write failed: {ex.Message}");
        }
    }

    private UpdateJournal? ReadJournal()
    {
        if (!File.Exists(JournalPath))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(JournalPath), UpdateJournalContext.Default.UpdateJournal);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Update journal unreadable, discarding: {ex.Message}");
            return null;
        }
    }

    /// <summary>删除文件;删不掉时改名挪进备份目录的弃置区(被占用的映像文件可以改名,不能删)。</summary>
    private bool TryRemoveFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            // 落到这里说明文件正被占用。改名总是能成的,腾出位置让回滚继续,
            // 弃置区随备份目录一起删除。
        }
        try
        {
            string discard = Path.Combine(BackupDirectory, ".discarded", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(discard)!);
            File.Move(path, discard);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VelaShell] Could not remove {path}: {ex.Message}");
            return false;
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>暂存目录里已无任何内容(下载产物也没了)时把它一并删掉,不给应用目录留空壳。</summary>
    private void TryDeleteStagingIfEmpty()
    {
        try
        {
            if (Directory.Exists(StagingDirectory)
                && !Directory.EnumerateFileSystemEntries(StagingDirectory).Any())
            {
                Directory.Delete(StagingDirectory);
            }
        }
        catch
        {
            // 空壳目录留着无害。
        }
    }
}

/// <summary>换版日志:阶段 + 涉及的文件清单,崩溃后据此回滚或完成收尾。</summary>
public sealed class UpdateJournal
{
    /// <summary>阶段:已解包到 <c>payload/</c>,应用目录尚未被触碰。</summary>
    public const string PhaseStaged = "staged";

    /// <summary>阶段:已把换版交给外置更新器进程,应用目录仍未被触碰。</summary>
    public const string PhaseHandoff = "handoff";

    /// <summary>阶段:正在移动文件换版(此时应用目录处于半换版状态)。</summary>
    public const string PhaseApplying = "applying";

    /// <summary>阶段:换版完成,只剩清理备份。</summary>
    public const string PhaseDone = "done";

    /// <summary>当前阶段。</summary>
    public string Phase { get; set; } = PhaseStaged;

    /// <summary>本次更新涉及的文件。</summary>
    public List<UpdateJournalFile> Files { get; set; } = [];

    /// <summary>外置更新器所在的临时目录;换版完成后由重启的应用清理。未走外置路径时为 null。</summary>
    public string? UpdaterDirectory { get; set; }
}

/// <summary>换版日志中的单个文件:应用目录内的相对路径,及换版前是否已存在。</summary>
public sealed class UpdateJournalFile
{
    /// <summary>应用目录内的相对路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>换版前该文件是否已存在(不存在的是新增文件,回滚时直接删除)。</summary>
    public bool Existed { get; set; }
}

/// <summary>换版日志的 System.Text.Json 源生成上下文(单文件发布下不依赖反射)。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UpdateJournal))]
internal sealed partial class UpdateJournalContext : JsonSerializerContext;
