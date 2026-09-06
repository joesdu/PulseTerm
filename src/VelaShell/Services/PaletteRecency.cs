using System.Diagnostics;
using VelaShell.Core.Data;

namespace VelaShell.Services;

/// <summary>命令面板里一条结果的使用痕迹。</summary>
public sealed class PaletteUsage
{
    /// <summary>条目标识(命令 id 或 <c>session:{guid}</c>)。</summary>
    public string Id { get; set; } = "";

    /// <summary>累计执行次数。</summary>
    public int Count { get; set; }

    /// <summary>上次执行时间(UTC)。</summary>
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>
/// 命令面板的"最近使用"记录:让常用的命令与会话在同分时排到前面。
/// </summary>
/// <remarks>
/// <para>
/// 存在 <see cref="IAppDataStore" /> 而不是 <c>AppSettings</c>:这是**使用痕迹**,
/// 不是配置。混进设置里会跟着配置导出/同步跑到别的机器上去,而"我在这台机器上常用什么"
/// 换台机器根本不成立。
/// </para>
/// <para>
/// 内存字典是唯一的读路径(打分在每次按键时跑,不能碰盘);落盘是后台的、失败即忽略 ——
/// 丢一条使用痕迹的代价远小于因为写盘失败而让面板卡住或报错。
/// </para>
/// </remarks>
public sealed class PaletteRecency(IAppDataStore? store)
{
    private const string Collection = "palette_recency";

    private readonly Dictionary<string, PaletteUsage> _usage = new(StringComparer.Ordinal);

    /// <summary>从存储载入既有痕迹;失败时静默退化为空记录(面板照常可用,只是没有加权)。</summary>
    public async Task LoadAsync()
    {
        if (store is null)
        {
            return;
        }
        try
        {
            foreach (PaletteUsage entry in await store.GetAllAsync<PaletteUsage>(Collection).ConfigureAwait(false))
            {
                if (entry.Id is { Length: > 0 })
                {
                    _usage[entry.Id] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PaletteRecency] 载入使用痕迹失败,本次不做最近使用加权:{ex.Message}");
        }
    }

    /// <summary>取某条目的使用痕迹;没用过返回 (0, null)。</summary>
    /// <param name="id">条目标识。</param>
    /// <returns>累计次数与上次使用时间。</returns>
    public (int Count, DateTime? LastUsedUtc) Get(string id) =>
        _usage.TryGetValue(id, out PaletteUsage? entry)
            ? (entry.Count, entry.LastUsedUtc)
            : (0, null);

    /// <summary>记一次使用:内存立刻生效,落盘走后台。</summary>
    /// <param name="id">条目标识。</param>
    public void Touch(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        if (!_usage.TryGetValue(id, out PaletteUsage? entry))
        {
            entry = new() { Id = id };
            _usage[id] = entry;
        }
        entry.Count++;
        entry.LastUsedUtc = DateTime.UtcNow;

        if (store is null)
        {
            return;
        }
        PaletteUsage snapshot = new() { Id = entry.Id, Count = entry.Count, LastUsedUtc = entry.LastUsedUtc };
        _ = Task.Run(async () =>
        {
            try
            {
                await store.UpsertAsync(Collection, snapshot.Id, snapshot).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PaletteRecency] 写入使用痕迹失败:{ex.Message}");
            }
        });
    }
}
