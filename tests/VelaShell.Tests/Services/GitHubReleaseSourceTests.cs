using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

/// <summary>GitHubReleaseSource 下载通路的断点续传行为(经注入的 HttpMessageHandler 打桩)。</summary>
[TestClass]
public class GitHubReleaseSourceTests : IDisposable
{
    private readonly string _dir;

    public GitHubReleaseSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"velashell_ghsource_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
        GC.SuppressFinalize(this);
    }

    private static UpdateManifest CreateManifest(string assetName, long size) =>
        UpdateManifest.Parse($$"""
            { "version": "2.0.0", "tag": "v2.0.0",
              "assets": { "test-rid": { "name": "{{assetName}}", "sha256": "unused", "size": {{size}} } } }
            """);

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_NoPartial_DownloadsWholeFileWithoutRangeHeader()
    {
        byte[] payload = Encoding.ASCII.GetBytes("full-content");
        RangeHeaderValue? seenRange = null;
        StubHandler handler = new(request =>
        {
            seenRange = request.Headers.Range;
            return FullResponse(payload);
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);
        string destination = Path.Combine(_dir, "pkg.zip");

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.IsNull(seenRange);
        Assert.AreEqual("full-content", await File.ReadAllTextAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"), "下载完成后不应残留半成品");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash, "返回的流式哈希应与内容一致");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_WithPartial_SendsRangeAndAppends()
    {
        byte[] payload = Encoding.ASCII.GetBytes("0123456789");
        string destination = Path.Combine(_dir, "pkg.zip");
        await File.WriteAllTextAsync(destination + ".partial", "01234");
        RangeHeaderValue? seenRange = null;
        StubHandler handler = new(request =>
        {
            seenRange = request.Headers.Range;
            long from = request.Headers.Range?.Ranges.First().From ?? 0;
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return response;
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);

        List<int> reported = [];
        await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination,
            new SynchronousProgress(reported.Add));

        Assert.AreEqual(5, seenRange?.Ranges.First().From);
        Assert.AreEqual("0123456789", await File.ReadAllTextAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"));
        Assert.Contains(100, reported);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_ServerIgnoresRange_RestartsFromScratch()
    {
        byte[] payload = Encoding.ASCII.GetBytes("full-content");
        string destination = Path.Combine(_dir, "pkg.zip");
        await File.WriteAllTextAsync(destination + ".partial", "wrong");
        StubHandler handler = new(_ => FullResponse(payload));
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);

        await source.DownloadAssetAsync(CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.AreEqual("full-content", await File.ReadAllTextAsync(destination));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_RangeNotSatisfiable_RetriesWholeFile()
    {
        byte[] payload = Encoding.ASCII.GetBytes("full-content");
        string destination = Path.Combine(_dir, "pkg.zip");
        // 半成品长度声称 5,但服务器上的资产已变化,Range 请求得到 416。
        await File.WriteAllTextAsync(destination + ".partial", "abcde");
        int requests = 0;
        StubHandler handler = new(request =>
        {
            requests++;
            return request.Headers.Range != null
                ? new(HttpStatusCode.RequestedRangeNotSatisfiable)
                : FullResponse(payload);
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);

        await source.DownloadAssetAsync(CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.AreEqual(2, requests);
        Assert.AreEqual("full-content", await File.ReadAllTextAsync(destination));
    }

    // ———— 分段并发下载(≥16MB 的包) ————

    /// <summary>可复现的 24MB 伪随机内容(24MB / 8MB 段宽 = 3 段并发)。</summary>
    private static byte[] LargePayload()
    {
        byte[] payload = new byte[24 * 1024 * 1024];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }
        return payload;
    }

    /// <summary>按请求的 Range 头切片返回 206 的打桩;无 Range 时返回 200 整包。线程安全,记录所有请求区间。</summary>
    private static StubHandler SlicingHandler(byte[] payload, List<(long From, long? To)> rangeLog)
    {
        return new(request =>
        {
            RangeItemHeaderValue? range = request.Headers.Range?.Ranges.First();
            lock (rangeLog)
            {
                if (range != null)
                {
                    rangeLog.Add((range.From!.Value, range.To));
                }
            }
            if (range == null)
            {
                return FullResponse(payload);
            }
            long from = range.From!.Value;
            long to = range.To ?? payload.Length - 1;
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)from..(int)(to + 1)])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, payload.Length);
            return response;
        });
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_LargeAsset_DownloadsInParallelSegments()
    {
        byte[] payload = LargePayload();
        List<(long From, long? To)> ranges = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", SlicingHandler(payload, ranges));
        string destination = Path.Combine(_dir, "pkg.zip");

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.HasCount(3, ranges, "24MB 应切成 3 段并发请求");
        Assert.AreSequenceEqual(
            [0, 8 * 1024 * 1024, 16 * 1024 * 1024], ranges.Select(r => r.From).ToArray(), SequenceOrder.InAnyOrder);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash);
        Assert.AreEqual(payload.Length, new FileInfo(destination).Length);
        byte[] downloaded = await File.ReadAllBytesAsync(destination);
        Assert.IsTrue(payload.AsSpan().SequenceEqual(downloaded), "分段拼装后的内容必须逐字节一致");
        Assert.IsFalse(File.Exists(destination + ".partial"));
        Assert.IsFalse(File.Exists(destination + ".partial.meta"), "完成后应清掉分段断点元数据");
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_SegmentedResume_SkipsCompletedSegments()
    {
        byte[] payload = LargePayload();
        const long segmentLength = 8 * 1024 * 1024;
        string destination = Path.Combine(_dir, "pkg.zip");
        string partial = destination + ".partial";
        // 伪造上次中断的现场:第一段已完成(数据在盘上),后两段未开始。
        byte[] partialBytes = new byte[payload.Length];
        Array.Copy(payload, partialBytes, segmentLength);
        await File.WriteAllBytesAsync(partial, partialBytes);
        await File.WriteAllTextAsync(partial + ".meta", $$"""
            {
              "Sha256": "unused",
              "Size": {{payload.Length}},
              "Segments": [
                { "Start": 0, "Length": {{segmentLength}}, "Done": {{segmentLength}} },
                { "Start": {{segmentLength}}, "Length": {{segmentLength}}, "Done": 0 },
                { "Start": {{2 * segmentLength}}, "Length": {{segmentLength}}, "Done": 0 }
              ]
            }
            """);
        List<(long From, long? To)> ranges = [];
        GitHubReleaseSource source = new("https://github.com/owner/repo", SlicingHandler(payload, ranges));

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.HasCount(2, ranges, "已完成的第一段不应重新请求");
        Assert.AreSequenceEqual(
            [segmentLength, 2 * segmentLength], [.. ranges.Select(r => r.From)], SequenceOrder.InAnyOrder);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash, "续传拼装后的整包哈希必须正确");
        byte[] downloaded = await File.ReadAllBytesAsync(destination);
        Assert.IsTrue(payload.AsSpan().SequenceEqual(downloaded));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadAssetAsync_LargeAssetServerIgnoresRange_FallsBackToSequential()
    {
        byte[] payload = LargePayload();
        int requests = 0;
        StubHandler handler = new(_ =>
        {
            Interlocked.Increment(ref requests);
            // 无论是否带 Range 一律回 200 整包:分段方案应作废并退回单连接。
            return FullResponse(payload);
        });
        GitHubReleaseSource source = new("https://github.com/owner/repo", handler);
        string destination = Path.Combine(_dir, "pkg.zip");

        string? hash = await source.DownloadAssetAsync(
            CreateManifest("pkg.zip", payload.Length), Asset(payload.Length), destination);

        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(payload)), hash);
        byte[] downloaded = await File.ReadAllBytesAsync(destination);
        Assert.IsTrue(payload.AsSpan().SequenceEqual(downloaded));
        Assert.IsFalse(File.Exists(destination + ".partial.meta"));
    }

    private static UpdateAsset Asset(long size) => new("pkg.zip", "unused", size);

    private static HttpResponseMessage FullResponse(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class SynchronousProgress(Action<int> handler) : IProgress<int>
    {
        public void Report(int value) => handler(value);
    }
}
