using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Services.Update;

/// <summary>
/// 以 GitHub Releases 为更新源:每个 Release 附带 CI 生成的 latest.json 清单。
/// stable 通道优先走固定重定向地址 releases/latest/download/latest.json(不占 API 配额、
/// 不受限流);该地址只认非预发布 Release,404(例如 beta 阶段全是预发布)时回退到
/// REST API 列表取最新可用发布。preview 通道直接走 API 列表(预发布也算)。
/// </summary>
public sealed class GitHubReleaseSource : IUpdateSource
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly HttpClient _http;

    /// <summary>以仓库地址(https://github.com/&lt;owner&gt;/&lt;repo&gt;)构造;<paramref name="handler" /> 供测试注入。</summary>
    public GitHubReleaseSource(string repositoryUrl, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        Uri uri = new(repositoryUrl.TrimEnd('/'));
        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || segments[0].Length == 0 || segments[1].Length == 0)
        {
            throw new ArgumentException($"Not a GitHub repository URL: {repositoryUrl}", nameof(repositoryUrl));
        }
        _owner = segments[0];
        _repo = segments[1];
        _http = handler != null ? new(handler) : new();
        // 下载走同一个 client,大包需要宽裕的整体超时;单步(清单/API)另用 CTS 收紧。
        _http.Timeout = TimeSpan.FromMinutes(15);
        // GitHub REST API 要求 User-Agent,缺失直接 403。
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VelaShell-Updater", "1"));
    }

    /// <inheritdoc />
    public async Task<UpdateManifest?> GetLatestManifestAsync(bool includePreRelease, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        if (!includePreRelease)
        {
            string url = $"https://github.com/{_owner}/{_repo}/releases/latest/download/latest.json";
            using HttpResponseMessage response = await _http.GetAsync(url, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return UpdateManifest.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            }
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
            }
            // 404:尚无正式版(或老版本 Release 没有清单),回退 API 列表。
        }
        string? tag = await FindLatestTagViaApiAsync(includePreRelease, cts.Token);
        if (tag == null)
        {
            return null;
        }
        string manifestUrl = $"https://github.com/{_owner}/{_repo}/releases/download/{Uri.EscapeDataString(tag)}/latest.json";
        using HttpResponseMessage manifestResponse = await _http.GetAsync(manifestUrl, cts.Token);
        if (manifestResponse.StatusCode == HttpStatusCode.NotFound)
        {
            // 最新 Release 没带清单(升级更新机制前发布的老版本),视为无可用更新。
            return null;
        }
        manifestResponse.EnsureSuccessStatusCode();
        return UpdateManifest.Parse(await manifestResponse.Content.ReadAsStringAsync(cts.Token));
    }

    /// <summary>
    /// 经 REST API 取最新可用 Release 的标签:preview 通道取第一个非草稿;
    /// stable 通道取第一个非草稿非预发布,一个都没有时(beta 阶段)放宽为第一个非草稿。
    /// </summary>
    private async Task<string?> FindLatestTagViaApiAsync(bool includePreRelease, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string? firstNonDraft = null;
        foreach (JsonElement release in doc.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
            {
                continue;
            }
            string? tag = release.GetProperty("tag_name").GetString();
            firstNonDraft ??= tag;
            if (includePreRelease || !release.GetProperty("prerelease").GetBoolean())
            {
                return tag;
            }
        }
        return firstNonDraft;
    }

    /// <summary>并发分段数上限;更多连接对 CDN 不友好且收益递减。</summary>
    private const int MaxSegments = 4;

    /// <summary>单段最小字节数;不足两段起步线(2×此值)的包走单连接顺序下载。</summary>
    private const long MinSegmentBytes = 8 * 1024 * 1024;

    private const int CopyBufferSize = 81920;

    /// <inheritdoc />
    /// <remarks>
    /// 流式写盘 + HTTP Range 断点续传,SHA-256 边下边算(返回值即整包哈希,调用方
    /// 无需重读文件校验)。大包(≥16MB)拆成最多 <see cref="MaxSegments" /> 段并发下载
    /// (CDN 对单连接限速时成倍提速),段进度持久化在 <c>*.partial.meta</c>,断点按段恢复,
    /// 哈希由后台任务追赶已完成的连续前缀、与下载重叠进行;小包或服务器不认 Range 时
    /// 退回单连接顺序下载(以半成品文件长度为断点)。数据全程先落在
    /// <c>destinationPath + ".partial"</c>,完整后才改名为正式文件名。
    /// </remarks>
    public async Task<string?> DownloadAssetAsync(
        UpdateManifest manifest,
        UpdateAsset asset,
        string destinationPath,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string url = $"https://github.com/{_owner}/{_repo}/releases/download/"
            + $"{Uri.EscapeDataString(manifest.Tag)}/{Uri.EscapeDataString(asset.Name)}";
        string partialPath = destinationPath + ".partial";
        string metaPath = partialPath + ".meta";
        if (asset.Size >= MinSegmentBytes * 2)
        {
            string? hash = await DownloadSegmentedAsync(url, asset, destinationPath, partialPath, metaPath, progress, cancellationToken);
            if (hash != null)
            {
                return hash;
            }
            // 服务器不按区间响应,退回单连接顺序下载(此时分段半成品已被丢弃)。
        }
        else if (File.Exists(metaPath))
        {
            // 资产低于分段线却带着分段元数据(残留),半成品格式与顺序下载不兼容,重下。
            TryDelete(partialPath);
            TryDelete(metaPath);
        }
        return await DownloadSequentialAsync(url, asset, destinationPath, partialPath, progress, cancellationToken);
    }

    /// <summary>单连接顺序下载:以半成品文件长度为断点,边收边写盘边算哈希。</summary>
    private async Task<string> DownloadSequentialAsync(
        string url,
        UpdateAsset asset,
        string destinationPath,
        string partialPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        long resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (asset.Size > 0 && resumeFrom >= asset.Size)
        {
            // 半成品不小于完整包,内容必然不对,整包重下。
            resumeFrom = 0;
        }

        HttpResponseMessage response = await SendDownloadRequestAsync(url, resumeFrom, cancellationToken);
        if (resumeFrom > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 区间无效(资产可能被重发过),丢弃半成品整包重下。
            response.Dispose();
            resumeFrom = 0;
            response = await SendDownloadRequestAsync(url, 0, cancellationToken);
        }
        string hash;
        using (response)
        {
            response.EnsureSuccessStatusCode();
            bool resumed = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed)
            {
                resumeFrom = 0;
            }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (resumed)
            {
                // 续传:已落盘的前缀要先进哈希,这次读盘换来的是整包不必重下。
                await HashExistingPrefixAsync(partialPath, resumeFrom, sha, cancellationToken);
            }
            long total = resumeFrom + (response.Content.Headers.ContentLength ?? Math.Max(asset.Size - resumeFrom, 0));
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream target = new(
                partialPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize, useAsync: true);
            byte[] buffer = new byte[CopyBufferSize];
            long copied = resumeFrom;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.AppendData(buffer, 0, read);
                copied += read;
                if (total > 0 && progress != null)
                {
                    int percent = (int)(copied * 100 / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(Math.Min(percent, 100));
                    }
                }
            }
            hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        File.Move(partialPath, destinationPath, true);
        progress?.Report(100);
        return hash;
    }

    /// <summary>
    /// 多段并发下载。全部段完成时返回整包 SHA-256;服务器不按区间响应(非 206)时
    /// 返回 null,由调用方退回单连接方案。取消或网络失败时段进度已落盘,下次按段续传。
    /// </summary>
    private async Task<string?> DownloadSegmentedAsync(
        string url,
        UpdateAsset asset,
        string destinationPath,
        string partialPath,
        string metaPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        List<DownloadSegment> segments = LoadOrCreateSegments(asset, partialPath, metaPath);
        await using (FileStream preallocate = new(partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            if (preallocate.Length != asset.Size)
            {
                preallocate.SetLength(asset.Size);
            }
        }

        object gate = new();
        int lastPercent = -1;
        long saveWatermark = segments.Sum(s => s.Done);
        bool rangeUnsupported = false;
        using SemaphoreSlim hashSignal = new(0);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void SaveMeta()
        {
            lock (gate)
            {
                string tmp = metaPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(
                    new SegmentedDownloadMeta { Sha256 = asset.Sha256, Size = asset.Size, Segments = segments },
                    SegmentedDownloadMetaContext.Default.SegmentedDownloadMeta));
                File.Move(tmp, metaPath, true);
            }
        }

        void ReportProgress(long doneTotal)
        {
            if (progress == null)
            {
                return;
            }
            int percent = (int)(doneTotal * 100 / asset.Size);
            bool changed = false;
            lock (gate)
            {
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    changed = true;
                }
            }
            if (changed)
            {
                progress.Report(Math.Min(percent, 100));
            }
        }

        async Task DownloadSegment(DownloadSegment segment)
        {
            if (segment.Done >= segment.Length)
            {
                return;
            }
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(segment.Start + segment.Done, segment.Start + segment.Length - 1);
                using HttpResponseMessage response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    rangeUnsupported = true;
                    throw new OperationCanceledException("Server does not honor Range requests.");
                }
                await using Stream source = await response.Content.ReadAsStreamAsync(abort.Token);
                // bufferSize: 1 关闭 FileStream 用户态缓冲:各段写入必须立刻进 OS 页缓存,
                // 否则并行读同一文件的哈希任务会读到未写入的洞(哈希错 → 整包报废)。
                await using FileStream target = new(
                    partialPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1, useAsync: true);
                target.Position = segment.Start + segment.Done;
                byte[] buffer = new byte[CopyBufferSize];
                long remaining = segment.Length - segment.Done;
                int read;
                while (remaining > 0
                    && (read = await source.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), abort.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), abort.Token);
                    long doneTotal;
                    lock (gate)
                    {
                        segment.Done += read;
                        doneTotal = segments.Sum(s => s.Done);
                    }
                    remaining -= read;
                    hashSignal.Release();
                    ReportProgress(doneTotal);
                    bool save = false;
                    lock (gate)
                    {
                        if (doneTotal - saveWatermark >= 4 * 1024 * 1024)
                        {
                            saveWatermark = doneTotal;
                            save = true;
                        }
                    }
                    if (save)
                    {
                        SaveMeta();
                    }
                }
                if (remaining > 0)
                {
                    throw new IOException($"Segment [{segment.Start}, +{segment.Length}) of {url} ended prematurely.");
                }
            }
            catch
            {
                // 任一段失败即中止其余段;本段已写入的进度在 finally 落盘,下次续传。
                abort.Cancel();
                throw;
            }
            finally
            {
                SaveMeta();
            }
        }

        SaveMeta();
        Task<string> hashTask = HashFrontierAsync(partialPath, segments, asset.Size, gate, hashSignal, abort.Token);
        Task[] workers = [.. segments.Select(DownloadSegment)];
        try
        {
            await Task.WhenAll(workers);
            string hash = await hashTask;
            TryDelete(metaPath);
            File.Move(partialPath, destinationPath, true);
            progress?.Report(100);
            return hash;
        }
        catch (Exception ex)
        {
            abort.Cancel();
            try
            {
                await Task.WhenAll(workers);
            }
            catch
            {
                // 只等收尾;每段的进度已在各自 finally 落盘。
            }
            try
            {
                await hashTask;
            }
            catch
            {
                // 哈希任务随下载一并终止。
            }
            if (rangeUnsupported)
            {
                TryDelete(partialPath);
                TryDelete(metaPath);
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            // WhenAll 先浮出的可能是被连带取消的兄弟段;把真正的失败原因找出来上抛。
            Exception cause = workers
                .Where(w => w.Exception != null)
                .SelectMany(w => w.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException) ?? ex;
            ExceptionDispatchInfo.Capture(cause).Throw();
            throw; // 不可达,让编译器确信所有路径都有出口。
        }
    }

    /// <summary>
    /// 与分段下载重叠执行的流式哈希:顺序读取半成品文件中"已完成的连续前缀",
    /// 前沿未推进时等待段任务的信号。写入方已关缓冲直写 OS,读写经页缓存保持一致,
    /// 这里同样关掉读缓冲以免预读到前沿之外的陈旧空洞。
    /// </summary>
    private static async Task<string> HashFrontierAsync(
        string partialPath,
        List<DownloadSegment> segments,
        long total,
        object gate,
        SemaphoreSlim hashSignal,
        CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream stream = new(
            partialPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1, useAsync: true);
        byte[] buffer = new byte[CopyBufferSize];
        long hashed = 0;
        while (hashed < total)
        {
            long frontier;
            lock (gate)
            {
                frontier = ContiguousFrontier(segments, total);
            }
            if (frontier <= hashed)
            {
                await hashSignal.WaitAsync(cancellationToken);
                continue;
            }
            while (hashed < frontier)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, frontier - hashed)), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Partial download file truncated while hashing.");
                }
                sha.AppendData(buffer, 0, read);
                hashed += read;
            }
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>各段按 Start 升序;返回从文件头起已连续完成的字节数。</summary>
    private static long ContiguousFrontier(List<DownloadSegment> segments, long total)
    {
        foreach (DownloadSegment segment in segments)
        {
            if (segment.Done < segment.Length)
            {
                return segment.Start + segment.Done;
            }
        }
        return total;
    }

    /// <summary>
    /// 读取上次的分段断点(资产指纹与半成品完整性都对得上才复用),否则丢弃残留、
    /// 按包大小切出等宽的新段(最后一段吸收余数)。
    /// </summary>
    private static List<DownloadSegment> LoadOrCreateSegments(UpdateAsset asset, string partialPath, string metaPath)
    {
        if (File.Exists(metaPath) && File.Exists(partialPath))
        {
            try
            {
                SegmentedDownloadMeta? meta = JsonSerializer.Deserialize(
                    File.ReadAllText(metaPath), SegmentedDownloadMetaContext.Default.SegmentedDownloadMeta);
                if (meta != null
                    && meta.Sha256.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)
                    && meta.Size == asset.Size
                    && new FileInfo(partialPath).Length == asset.Size
                    && SegmentsAreValid(meta.Segments, asset.Size))
                {
                    return meta.Segments;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VelaShell] Segmented download meta unreadable, restarting: {ex.Message}");
            }
        }
        TryDelete(partialPath);
        TryDelete(metaPath);
        int count = (int)Math.Min(MaxSegments, asset.Size / MinSegmentBytes);
        long baseLength = asset.Size / count;
        List<DownloadSegment> segments = [];
        long start = 0;
        for (int i = 0; i < count; i++)
        {
            long length = i == count - 1 ? asset.Size - start : baseLength;
            segments.Add(new DownloadSegment { Start = start, Length = length, Done = 0 });
            start += length;
        }
        return segments;
    }

    /// <summary>校验断点里的段表:从 0 起首尾相接铺满整包,进度不越界。</summary>
    private static bool SegmentsAreValid(List<DownloadSegment> segments, long totalSize)
    {
        long expectedStart = 0;
        foreach (DownloadSegment segment in segments)
        {
            if (segment.Start != expectedStart || segment.Length <= 0
                || segment.Done < 0 || segment.Done > segment.Length)
            {
                return false;
            }
            expectedStart += segment.Length;
        }
        return segments.Count > 0 && expectedStart == totalSize;
    }

    /// <summary>把已落盘的半成品前缀喂进哈希器(顺序续传时用)。</summary>
    private static async Task HashExistingPrefixAsync(
        string path, long length, IncrementalHash sha, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = length;
        int read;
        while (remaining > 0
            && (read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        string url, long resumeFrom, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 残留清不掉不阻断流程,后续下载/启动清理兜底。
        }
    }
}

/// <summary>分段下载的断点元数据(<c>*.partial.meta</c>):目标资产指纹 + 各段进度。</summary>
public sealed class SegmentedDownloadMeta
{
    /// <summary>目标资产的 SHA-256(与清单一致);对不上说明半成品属于别的发布,作废。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>目标资产的字节大小。</summary>
    public long Size { get; set; }

    /// <summary>按 Start 升序的段表。</summary>
    public List<DownloadSegment> Segments { get; set; } = [];
}

/// <summary>分段下载中的单个字节区间及其进度。</summary>
public sealed class DownloadSegment
{
    /// <summary>段起始偏移(含)。</summary>
    public long Start { get; set; }

    /// <summary>段长度(字节)。</summary>
    public long Length { get; set; }

    /// <summary>已下载的字节数(从段头起连续)。</summary>
    public long Done { get; set; }
}

/// <summary>断点元数据的 System.Text.Json 源生成上下文(单文件发布下不依赖反射)。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SegmentedDownloadMeta))]
internal sealed partial class SegmentedDownloadMetaContext : JsonSerializerContext;
