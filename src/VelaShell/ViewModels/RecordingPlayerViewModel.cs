using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>回放中心列表条目(设计 NceE6 左栏:主机 • 时间 • 时长)。</summary>
public sealed class RecordingItemViewModel(SessionRecording model)
{
    /// <summary>底层会话录制模型。</summary>
    public SessionRecording Model { get; } = model;

    /// <summary>列表显示名:会话标签,为空时回退到“未命名会话”文案。</summary>
    public string Label => string.IsNullOrWhiteSpace(Model.SessionLabel) ? Strings.Get("Msg_UnnamedSession") : Model.SessionLabel;

    /// <summary>起始时间的本地化短文本(月-日 时:分)。</summary>
    public string StartText => Model.StartedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");

    /// <summary>录制时长的可读文本(超过 1 小时显示时/分,否则分/秒)。</summary>
    public string DurationText
    {
        get
        {
            var span = TimeSpan.FromMilliseconds(Math.Max(0, Model.DurationMs));
            return span.TotalHours >= 1 ? span.ToString(@"h\h\ mm\m") : $"{(int)span.TotalMinutes:00}m {span.Seconds:00}s";
        }
    }

    /// <summary>录制字节大小的可读文本(B/KB/MB)。</summary>
    public string SizeText => Model.ByteSize switch
    {
        < 1024 => $"{Model.ByteSize} B",
        < 1024 * 1024 => $"{Model.ByteSize / 1024.0:0.#} KB",
        _ => $"{Model.ByteSize / 1024.0 / 1024.0:0.#} MB"
    };
}

/// <summary>
/// 会话录制回放中心(设计 NceE6):左栏录制列表,右栏终端回放 +
/// 时间轴/倍速/跳过空闲。回放把录制块按原始时间偏移(除以倍速)重放进
/// 一个只读终端控件;拖动时间轴 = 重置终端后瞬时重放至目标位置。
/// </summary>
public class RecordingPlayerViewModel : ReactiveObject
{
    /// <summary>跳过空闲:两块输出间超过该间隔时快进(保留 1 秒的停顿感)。</summary>
    private const long IdleGapCapMs = 1000;

    private static readonly int[] Speeds = [1, 2, 4, 8, 16];

    private readonly ISessionRecordingStore _store;
    private readonly ISettingsService? _settingsService;
    private readonly DispatcherTimer _timer;

    private List<RecordingChunk> _chunks = [];
    private int _nextChunkIndex;

    /// <summary>
    /// 录制加载的代次。选中项一变就自增,加载回来先比对代次,过时的结果直接丢弃。
    /// </summary>
    /// <remarks>
    /// 没有它的话:选中 A、紧接着选中 B,若 A 的读取后返回(大录制很常见),
    /// A 的块会盖掉 B 的,界面显示的是 B、放出来的却是 A。删除、清空选择、
    /// 关闭窗口期间同样可能被一个迟到的旧结果复活。与文件浏览器的
    /// <c>_navigationVersion</c> 是同一套思路。
    /// </remarks>
    private long _loadVersion;

    /// <summary>当前这次加载的取消源;新的选择到来时取消上一次,让它尽快退出而不是白读到底。</summary>
    private CancellationTokenSource? _loadCts;

    /// <summary>构造回放中心视图模型。</summary>
    /// <param name="store">会话录制存储(读取/删除录制与块)。</param>
    /// <param name="settingsService">设置服务(读写自动录制开关);可为 null。</param>
    public RecordingPlayerViewModel(ISessionRecordingStore store, ISettingsService? settingsService = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settingsService = settingsService;
        _timer = new(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, OnTick) { IsEnabled = false };
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        TogglePlayCommand = ReactiveCommand.Create(TogglePlay);
        DeleteCommand = ReactiveCommand.CreateFromTask<RecordingItemViewModel>(DeleteAsync);
        ToggleAutoRecordCommand = ReactiveCommand.CreateFromTask(ToggleAutoRecordAsync);
    }

    /// <summary>回放输出的接收端(视图把终端控件的 Feed 接进来)。</summary>
    public Action<byte[]>? FeedSink { get; set; }

    /// <summary>回放重置(选择新录制/拖动时间轴时清屏)。</summary>
    public Action? ResetSink { get; set; }

    /// <summary>录制列表(左栏数据源)。</summary>
    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = [];

    /// <summary>是否存在可回放的录制。</summary>
    public bool HasRecordings
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前选中的录制;赋值触发异步加载对应录制块。</summary>
    public RecordingItemViewModel? SelectedRecording
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            _ = LoadSelectedAsync(value);
        }
    }

    /// <summary>是否正在回放。</summary>
    public bool IsPlaying
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 当前回放位置(毫秒)。Slider TwoWay 绑定:用户拖动(非播放推进)触发 seek 重建;
    /// 播放定时器经 <see cref="SetPositionInternal" /> 更新,不触发 seek。
    /// </summary>
    public double PositionMs
    {
        get;
        set
        {
            if (Math.Abs(field - value) < 1)
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(PositionText));
            if (!_suppressSeek)
            {
                Seek((long)value);
            }
        }
    }

    /// <summary>当前录制的总时长(毫秒)。</summary>
    public double DurationMs
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>当前回放位置的时间文本。</summary>
    public string PositionText => FormatTime((long)PositionMs);

    /// <summary>总时长的时间文本。</summary>
    public string DurationText => FormatTime((long)DurationMs);

    /// <summary>倍速档位索引(对应 1/2/4/8/16x);赋值会钳制到合法范围。</summary>
    public int SpeedIndex
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 0, Speeds.Length - 1));
            this.RaisePropertyChanged(nameof(SpeedText));
        }
    }

    /// <summary>当前倍速的显示文本(如 4x)。</summary>
    public string SpeedText => $"{Speeds[SpeedIndex]}x";

    /// <summary>倍速循环:1x → 2x → 4x → 8x → 16x → 1x。</summary>
    public void CycleSpeed() => SpeedIndex = (SpeedIndex + 1) % Speeds.Length;

    /// <summary>是否跳过空闲:两块输出间隔过长时快进(默认开启)。</summary>
    public bool SkipIdle
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>自动录制(即 Security.RecordProductionSessions);改动立即保存设置。</summary>
    public bool AutoRecordEnabled
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>自动录制开关的状态文本(开/关)。</summary>
    public string AutoRecordText => AutoRecordEnabled ? Strings.Get("Msg_AutoRecordOn") : Strings.Get("Msg_AutoRecordOff");

    /// <summary>播放/暂停按钮的文本(随回放状态切换)。</summary>
    public string PlayButtonText => IsPlaying ? Strings.Get("Msg_Pause") : Strings.Get("Msg_Play");

    /// <summary>回放区标题(显示当前录制名与起始时间)。</summary>
    public string PlaybackTitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = Strings.Get("Msg_SelectRecordingToPlay");

    /// <summary>状态栏提示文本(加载/错误/空列表等)。</summary>
    public string Status
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>刷新录制列表命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>播放/暂停切换命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePlayCommand { get; }

    /// <summary>删除指定录制命令。</summary>
    public ReactiveCommand<RecordingItemViewModel, RxVoid> DeleteCommand { get; }

    /// <summary>切换自动录制开关命令。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleAutoRecordCommand { get; }

    /// <summary>
    /// 录制数据的占用快照(条数 / 逻辑体积 / 磁盘占用),供清理入口展示。
    /// 统计要遍历整个数据库目录,丢到后台线程,别让几 GB 的目录枚举卡住界面。
    /// </summary>
    public Task<RecordingStorageUsage> GetStorageUsageAsync() => Task.Run(() => _store.GetStorageUsageAsync());

    /// <summary>
    /// 执行清理并刷新列表。<paramref name="keepDays" /> 语义见
    /// <see cref="ISessionRecordingStore.ReclaimSpaceAsync" />(0 = 一条不留,
    /// <see cref="int.MaxValue" /> = 只回收已删除录制占的空间)。
    /// <para>
    /// 整个回收扔到后台线程:引擎的锁未被占用时那些 await 会同步走完,搬运几 GB 数据
    /// 就直接在 UI 线程上跑,界面一动不动。回来后再刷列表(await 会回到 UI 线程)。
    /// </para>
    /// </summary>
    public async Task<RecordingCleanupResult> CleanupAsync(int keepDays)
    {
        Pause();
        SelectedRecording = null;
        RecordingCleanupResult result = await Task.Run(() => _store.ReclaimSpaceAsync(keepDays));
        await RefreshAsync();
        return result;
    }

    /// <summary>把状态栏文本换成给定内容(清理结果回显)。</summary>
    public void SetStatus(string status) => Status = status;

    /// <summary>初始化:加载录制列表并读取自动录制设置。</summary>
    public async Task InitializeAsync()
    {
        await RefreshAsync();
        if (_settingsService is not null)
        {
            try
            {
                AutoRecordEnabled = (await _settingsService.GetSettingsAsync()).Security.RecordProductionSessions;
                this.RaisePropertyChanged(nameof(AutoRecordText));
            }
            catch
            {
                // 读取失败保持默认展示。
            }
        }
    }

    /// <summary>导出为 asciicast v2(asciinema 通用格式,可被 asciinema-player 等工具回放)。</summary>
    /// <remarks>
    /// 三处曾经的坑,改之前先读:
    /// <list type="number">
    /// <item>
    /// <b>数字不能走字符串插值。</b>原先是 <c>$"[{ms / 1000.0:0.000}, …]"</c>,在以逗号作小数点的
    /// 区域设置(fr-FR、de-DE、ru-RU…)下会写出 <c>[1,234, "o", …]</c> —— 那是个四元素数组,
    /// 整个文件在任何播放器里都解析不了。交给 <see cref="JsonSerializer" /> 写,它恒用不变文化。
    /// </item>
    /// <item>
    /// <b>解码必须跨块连续。</b>原先每块各自 <c>Encoding.UTF8.GetString</c>,而块边界是按
    /// 600ms/64KB 切的,和字符边界毫无关系 —— 一个"中"字的三个字节被切成 2+1 时,两块各自
    /// 解出一个 U+FFFD,原字彻底丢失。<see cref="Decoder" /> 会把不完整的尾字节留到下一块。
    /// </item>
    /// <item>
    /// <b>尺寸取录制里记的真实值</b>,不再写死 120×32(见 <see cref="SessionRecording.Columns" />)。
    /// </item>
    /// </list>
    /// </remarks>
    public string BuildAsciicast()
    {
        RecordingItemViewModel? item = SelectedRecording;
        var builder = new StringBuilder();
        builder.AppendLine(JsonSerializer.Serialize(new AsciicastHeader(
            2,
            item?.Model.Columns ?? SessionRecording.DefaultColumns,
            item?.Model.Rows ?? SessionRecording.DefaultRows,
            // Kind 未必是 Utc(经存储往返后常为 Unspecified),直接构造 DateTimeOffset 会按
            // 本地时区换算,导出的开始时刻会偏掉整整一个时区。
            item is null
                ? 0
                : new DateTimeOffset(DateTime.SpecifyKind(item.Model.StartedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            item?.Label)));

        Decoder decoder = new UTF8Encoding(false).GetDecoder();
        char[] chars = [];
        foreach (RecordingChunk chunk in _chunks)
        {
            int max = decoder.GetCharCount(chunk.Data, 0, chunk.Data.Length, flush: false);
            if (chars.Length < max)
            {
                chars = new char[max];
            }
            int count = decoder.GetChars(chunk.Data, 0, chunk.Data.Length, chars, 0, flush: false);
            // 整行一次序列化:时间是 double、类型是 "o"、数据是字符串,三者的转义与格式
            // 全交给 JSON 写入器,不再自己拼。
            builder.AppendLine(JsonSerializer.Serialize<object[]>(
                [Math.Round(chunk.OffsetMs / 1000.0, 3), "o", new string(chars, 0, count)]));
        }
        return builder.ToString();
    }

    /// <summary>asciicast v2 的头部行。字段名即格式规定的名字,不能改。</summary>
    private sealed record AsciicastHeader(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("timestamp")] long Timestamp,
        [property: JsonPropertyName("title")] string? Title);

    /// <summary>是否已选中录制且存在可回放的录制块。</summary>
    public bool HasSelection => SelectedRecording is not null && _chunks.Count > 0;

    private async Task RefreshAsync()
    {
        try
        {
            List<SessionRecording> recordings = await _store.ListRecordingsAsync();
            Recordings.Clear();
            foreach (SessionRecording recording in recordings)
            {
                Recordings.Add(new(recording));
            }
            Status = recordings.Count > 0 ? "" : Strings.Get("Msg_NoRecordings");
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_LoadRecordingListFailed", ex.Message);
        }
        HasRecordings = Recordings.Count > 0;
    }

    private async Task LoadSelectedAsync(RecordingItemViewModel? item)
    {
        long version = Interlocked.Increment(ref _loadVersion);

        // 上一次加载还在读就让它尽快退出。只 Cancel、不 Dispose —— 拥有它的那次调用会在
        // 自己的 finally 里释放;在这里释放会让它正在用的 token 变成已释放对象。
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _loadCts, cts);
        try
        {
            previous?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 上一次已经收尾完毕,无需取消。
        }

        Pause();
        _chunks = [];
        _nextChunkIndex = 0;
        PositionMs = 0;
        // 清空选择/切换录制的这一刻 HasSelection 就变成了 false,得通知出去,
        // 否则播放与导出按钮会停在上一份录制的可用状态上。
        this.RaisePropertyChanged(nameof(HasSelection));
        if (item is null)
        {
            PlaybackTitle = Strings.Get("Msg_SelectRecordingToPlay");
            DurationMs = 0;
            Status = "";
            cts.Dispose();
            return;
        }
        PlaybackTitle = Strings.Format("Msg_PlaybackTitle", item.Label, item.StartText);
        try
        {
            List<RecordingChunk> chunks = await _store.GetChunksAsync(item.Model.Id, cts.Token);
            if (version != Volatile.Read(ref _loadVersion))
            {
                return; // 期间又选了别的:这份结果已经没人要了。
            }
            _chunks = chunks;
            DurationMs = Math.Max(item.Model.DurationMs, _chunks.Count > 0 ? _chunks[^1].OffsetMs : 0);
            ResetSink?.Invoke();
            Status = _chunks.Count > 0 ? "" : Strings.Get("Msg_RecordingEmpty");
            this.RaisePropertyChanged(nameof(HasSelection));
        }
        catch (OperationCanceledException)
        {
            // 被后一次选择取代,静默退出。
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref _loadVersion))
            {
                Status = Strings.Format("Msg_LoadRecordingFailed", ex.Message);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _loadCts, null, cts);
            cts.Dispose();
        }
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (_chunks.Count == 0)
        {
            return;
        }

        // 播到结尾后再点播放 = 从头再来:位置必须一并归零,
        // 否则下一帧立即判定“已到结尾”又暂停(只重置终端不够)。
        if (PositionMs >= DurationMs && DurationMs > 0)
        {
            SetPositionInternal(0);
            Seek(0);
        }
        IsPlaying = true;
        this.RaisePropertyChanged(nameof(PlayButtonText));
        _timer.Start();
    }

    private void Pause()
    {
        _timer.Stop();
        IsPlaying = false;
        this.RaisePropertyChanged(nameof(PlayButtonText));
    }

    /// <summary>
    /// 窗口关闭时必须调用:运行中的 DispatcherTimer 被调度器强引用,不停表会让
    /// 已关闭窗口的 VM 连同整段录制字节以 30Hz 空转存活到播放位置走完为止。
    /// </summary>
    public void StopPlayback() => Pause();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_chunks.Count == 0)
        {
            Pause();
            return;
        }
        double advance = 33.0 * Speeds[SpeedIndex];
        double next = PositionMs + advance;

        // 跳过空闲:若下一块输出还很远,直接快进到它前 1 秒处(保留停顿感)。
        if (SkipIdle && _nextChunkIndex < _chunks.Count)
        {
            long upcoming = _chunks[_nextChunkIndex].OffsetMs;
            if (upcoming - next > IdleGapCapMs)
            {
                next = upcoming - IdleGapCapMs;
            }
        }

        SetPositionInternal(Math.Min(next, DurationMs));
        while (_nextChunkIndex < _chunks.Count && _chunks[_nextChunkIndex].OffsetMs <= PositionMs)
        {
            FeedSink?.Invoke(_chunks[_nextChunkIndex].Data);
            _nextChunkIndex++;
        }
        if (PositionMs >= DurationMs)
        {
            Pause();
        }
    }

    /// <summary>播放推进专用:更新位置但不触发 seek 重建。</summary>
    private void SetPositionInternal(double value)
    {
        _suppressSeek = true;
        try
        {
            PositionMs = value;
        }
        finally
        {
            _suppressSeek = false;
        }
    }

    private bool _suppressSeek;

    private void Seek(long targetMs)
    {
        if (_suppressSeek || _chunks.Count == 0)
        {
            return;
        }

        // 终端状态不可增量回退:重置后把目标位置之前的所有块瞬时重放。
        ResetSink?.Invoke();
        _nextChunkIndex = 0;
        while (_nextChunkIndex < _chunks.Count && _chunks[_nextChunkIndex].OffsetMs <= targetMs)
        {
            FeedSink?.Invoke(_chunks[_nextChunkIndex].Data);
            _nextChunkIndex++;
        }
    }

    private async Task DeleteAsync(RecordingItemViewModel item)
    {
        try
        {
            if (ReferenceEquals(SelectedRecording, item))
            {
                Pause();
                SelectedRecording = null;
            }
            await _store.DeleteRecordingAsync(item.Model.Id);
            Recordings.Remove(item);
            HasRecordings = Recordings.Count > 0;
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_DeleteFailed", ex.Message);
        }
    }

    private async Task ToggleAutoRecordAsync()
    {
        if (_settingsService is null)
        {
            return;
        }
        try
        {
            AppSettings settings = await _settingsService.GetSettingsAsync();
            settings.Security.RecordProductionSessions = !settings.Security.RecordProductionSessions;
            await _settingsService.SaveSettingsAsync(settings);
            AutoRecordEnabled = settings.Security.RecordProductionSessions;
            this.RaisePropertyChanged(nameof(AutoRecordText));
            Status = AutoRecordEnabled ? Strings.Get("Msg_AutoRecordEnabledHint") : Strings.Get("Msg_AutoRecordDisabledHint");
        }
        catch (Exception ex)
        {
            Status = Strings.Format("Msg_ToggleFailed", ex.Message);
        }
    }

    private static string FormatTime(long ms)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }
}
