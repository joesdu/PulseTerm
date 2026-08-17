using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// 仅供测试使用的环回 S3 服务器:把对象存在内存里,实现客户端跑通
/// 「列桶 → 列对象(含分页与公共前缀)→ 上传/下载(含 Range)→ 分片上传 → 服务端复制 →
/// 单个/批量删除」所需的最小接口集。
/// <para>
/// **为什么自己写**:CI 与开发机不保证能起 MinIO 容器,而只用 Mock 验证不了这套后端里
/// 真正容易错的东西 —— 平的键空间能不能被折叠成目录树、分页令牌接得对不对、
/// 分片上传的 ETag 回传格式对不对。
/// </para>
/// <para>
/// **它会真的校验 SigV4 签名。** 这是刻意的:签名向量测试(<see cref="SigV4VerifierTests" />)
/// 只能证明"给定输入算得对",证明不了"客户端配置(端点 / 区域 / 寻址方式 / 凭据)
/// 真的被正确送上了线"。这些配错时,真实服务端只会回一句
/// <c>SignatureDoesNotMatch</c>,而这里能当场指出来。
/// </para>
/// </summary>
internal sealed class LoopbackS3Server : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TcpListener _listener;

    /// <summary>桶名 → (对象键 → 内容)。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StoredObject>> _buckets = new(StringComparer.Ordinal);

    /// <summary>uploadId → (分片号 → 内容)。</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>> _uploads = new(StringComparer.Ordinal);

    private int _uploadCounter;

    public LoopbackS3Server(string accessKey, string secretKey, string region = "us-east-1")
    {
        AccessKey = accessKey;
        SecretKey = secretKey;
        Region = region;
        _listener = new(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>监听端口(随机分配)。</summary>
    public int Port { get; }

    public string AccessKey { get; }

    public string SecretKey { get; }

    public string Region { get; }

    /// <summary>收到过的请求(方法 + 原始目标),供测试断言实际打了哪些接口。</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>对象键 → 写入时收到的 Content-Type(单次 PUT 与发起分片上传都会记)。</summary>
    public ConcurrentDictionary<string, string> ContentTypes { get; } = new(StringComparer.Ordinal);

    /// <summary>签名校验失败的次数;正常路径下必须始终为 0。</summary>
    public int SignatureFailures;

    /// <summary>
    /// 被拒请求的原因明细。签名对不上时光有一个 403 没法排查 ——
    /// 这里留下算出来的规范请求与两边的签名,失败的测试可以直接把它打出来。
    /// </summary>
    public ConcurrentQueue<string> Rejections { get; } = new();

    /// <summary>
    /// 对这些 HTTP 方法一律回 403 AccessDenied,用来复现「同一个对象 GET 放行、HEAD 被拒」
    /// 的授权(把对象设成公共读、或端点前面挂了 CDN 时很常见)。
    /// <para>
    /// HEAD 的响应体会像真实服务端那样被丢掉 —— 正是「403 却没有任何错误细节」的由来。
    /// </para>
    /// </summary>
    public HashSet<string> DeniedMethods { get; } = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// 只拒绝**带 Authorization 头**的读请求,预签名(凭证在查询串里)照常放行 ——
    /// 复现"直接下载不给、预签名下载给"的桶。这类授权现实里存在:桶策略可以只放行预签名形式,
    /// 端点前挂的 CDN / 网关也常把 Authorization 头剥掉或改写。
    /// </summary>
    public bool DenyDirectReads { get; set; }

    /// <summary>预置一个桶。</summary>
    public void AddBucket(string bucket) =>
        _buckets.GetOrAdd(bucket, _ => new(StringComparer.Ordinal));

    /// <summary>预置一个对象。</summary>
    public void AddObject(string bucket, string key, byte[] content)
    {
        AddBucket(bucket);
        _buckets[bucket][key] = new(content, DateTimeOffset.UtcNow);
    }

    /// <summary>预置一个文本对象。</summary>
    public void AddObject(string bucket, string key, string content) =>
        AddObject(bucket, key, Encoding.UTF8.GetBytes(content));

    /// <summary>读回一个对象的内容;不存在时返回 null。</summary>
    public byte[]? GetObject(string bucket, string key) =>
        _buckets.TryGetValue(bucket, out ConcurrentDictionary<string, StoredObject>? objects) &&
        objects.TryGetValue(key, out StoredObject stored)
            ? stored.Content
            : null;

    /// <summary>某个桶里当前的全部键。</summary>
    public IReadOnlyCollection<string> Keys(string bucket) =>
        _buckets.TryGetValue(bucket, out ConcurrentDictionary<string, StoredObject>? objects) ? [.. objects.Keys] : [];

    /// <summary>桶是否存在。</summary>
    public bool HasBucket(string bucket) => _buckets.ContainsKey(bucket);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // 停止监听时的竞态无人可报。
        }
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using NetworkStream stream = client.GetStream();
                HttpRequest? request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }
                Requests.Enqueue($"{request.Method} {request.Target}");
                HttpResponse response = !VerifySignature(request)
                    ? Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "The request signature we calculated does not match.")
                    : DeniedMethods.Contains(request.Method) ||
                      (DenyDirectReads && !IsPresigned(request) && request.Method is "GET" or "HEAD")
                        ? Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied.")
                        : Route(request);
                await WriteResponseAsync(stream, response, request.Method == "HEAD").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 客户端提前断开:测试里属正常收尾。
            }
        }
    }

    // ---- HTTP ---------------------------------------------------------------

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var header = new MemoryStream();
        byte[] one = new byte[1];
        int matched = 0;
        // 逐字节读到 CRLFCRLF —— 头部很小,这里图的是不会多读走一个字节的请求体。
        while (matched < 4)
        {
            int read = await stream.ReadAsync(one).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }
            header.WriteByte(one[0]);
            matched = one[0] == (matched % 2 == 0 ? (byte)'\r' : (byte)'\n') ? matched + 1 : one[0] == '\r' ? 1 : 0;
        }

        string[] lines = Encoding.UTF8.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }
        Dictionary<string, string> headers = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        // 请求体有三种到达形式,真实 S3 三种都收:
        //  1. 普通 Content-Length;
        //  2. HTTP 分块传输(Transfer-Encoding: chunked);
        //  3. aws-chunked —— AWSSDK 上传时的默认形态:每块前面带一行
        //     `<十六进制长度>;chunk-signature=<签名>`,末尾是一个 0 长度块。
        //     它与 (2) 可以叠加,因此要先解 HTTP 分块、再解 aws-chunk 框。
        byte[] body;
        bool httpChunked = headers.TryGetValue("Transfer-Encoding", out string? encoding) &&
                           encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        if (httpChunked)
        {
            body = await ReadHttpChunkedAsync(stream).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Content-Length", out string? lengthText) &&
                 int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) &&
                 length > 0)
        {
            body = new byte[length];
            await stream.ReadExactlyAsync(body).ConfigureAwait(false);
        }
        else
        {
            body = [];
        }

        bool awsChunked = (headers.TryGetValue("Content-Encoding", out string? contentEncoding) &&
                           contentEncoding.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase)) ||
                          IsStreamingPayload(headers);
        if (awsChunked)
        {
            body = DecodeAwsChunked(body);
        }

        string target = requestLine[1];
        int question = target.IndexOf('?');
        return new()
        {
            Method = requestLine[0],
            Target = target,
            RawPath = question < 0 ? target : target[..question],
            RawQuery = question < 0 ? string.Empty : target[(question + 1)..],
            Headers = headers,
            Body = body,
        };
    }

    /// <summary>
    /// 负载哈希是否为「流式签名」占位符。这类请求的请求体是分块加签的,
    /// 服务端无法用一个整体 SHA-256 去核对 —— 真实 S3 走的是逐块签名校验,
    /// 这里作为测试服务器只跳过负载校验,**请求头的签名仍然照验**。
    /// </summary>
    private static bool IsStreamingPayload(Dictionary<string, string> headers) =>
        headers.TryGetValue("x-amz-content-sha256", out string? hash) &&
        hash.StartsWith("STREAMING-", StringComparison.Ordinal);

    /// <summary>读取 HTTP 分块传输的请求体。</summary>
    private static async Task<byte[]> ReadHttpChunkedAsync(NetworkStream stream)
    {
        var body = new MemoryStream();
        while (true)
        {
            string sizeLine = await ReadLineAsync(stream).ConfigureAwait(false);
            // 分块扩展(`;name=value`)在长度之后,截掉再解析。
            int semicolon = sizeLine.IndexOf(';');
            string sizeText = (semicolon < 0 ? sizeLine : sizeLine[..semicolon]).Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size == 0)
            {
                break;
            }
            byte[] chunk = new byte[size];
            await stream.ReadExactlyAsync(chunk).ConfigureAwait(false);
            body.Write(chunk);
            await ReadLineAsync(stream).ConfigureAwait(false); // 块尾的 CRLF
        }
        return body.ToArray();
    }

    /// <summary>
    /// 去掉 aws-chunked 的帧:每块形如 <c>&lt;hex&gt;;chunk-signature=...\r\n&lt;数据&gt;\r\n</c>,
    /// 以一个 0 长度块结束。不是这个形状时原样返回(说明根本没加帧)。
    /// </summary>
    private static byte[] DecodeAwsChunked(byte[] framed)
    {
        var body = new MemoryStream();
        int offset = 0;
        while (offset < framed.Length)
        {
            int lineEnd = IndexOfCrLf(framed, offset);
            if (lineEnd < 0)
            {
                return offset == 0 ? framed : body.ToArray();
            }
            string header = Encoding.ASCII.GetString(framed, offset, lineEnd - offset);
            int semicolon = header.IndexOf(';');
            string sizeText = (semicolon < 0 ? header : header[..semicolon]).Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size))
            {
                // 不是 aws-chunked 的形状:原样当作请求体。
                return offset == 0 ? framed : body.ToArray();
            }
            offset = lineEnd + 2;
            if (size == 0)
            {
                break;
            }
            if (offset + size > framed.Length)
            {
                break;
            }
            body.Write(framed, offset, size);
            offset += size + 2; // 跳过数据与其后的 CRLF
        }
        return body.ToArray();
    }

    private static int IndexOfCrLf(byte[] buffer, int start)
    {
        for (int i = start; i + 1 < buffer.Length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var line = new MemoryStream();
        byte[] one = new byte[1];
        byte previous = 0;
        while (await stream.ReadAsync(one).ConfigureAwait(false) == 1)
        {
            if (previous == '\r' && one[0] == '\n')
            {
                byte[] raw = line.ToArray();
                return Encoding.ASCII.GetString(raw, 0, Math.Max(0, raw.Length - 1));
            }
            line.WriteByte(one[0]);
            previous = one[0];
        }
        return Encoding.ASCII.GetString(line.ToArray());
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, bool headOnly)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {(int)response.Status} {response.Status}\r\n");
        builder.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
        foreach ((string name, string value) in response.Headers)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }
        // 每个响应关连接:测试里不需要复用,也省掉一套 keep-alive 状态机。
        builder.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(builder.ToString())).ConfigureAwait(false);
        // HEAD 要如实报 Content-Length 但不能带体 —— 客户端正是靠这个头拿对象大小的。
        if (!headOnly && response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body).ConfigureAwait(false);
        }
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 按收到的**原始**请求行与头重算一遍 SigV4,并与 <c>Authorization</c> 里的签名比对。
    /// 客户端签的和发的不是同一份时,这里会当场失败。
    /// </summary>
    /// <summary>该请求是否走的预签名(SigV4 查询串认证)而不是 Authorization 头。</summary>
    private static bool IsPresigned(HttpRequest request) =>
        request.RawQuery.Contains("X-Amz-Signature=", StringComparison.Ordinal);

    /// <summary>
    /// 校验预签名 URL 的签名。与头认证的差别只有三处:凭证在查询串里、
    /// 规范查询串要**剔除 X-Amz-Signature 自身**、负载哈希固定是 UNSIGNED-PAYLOAD。
    /// </summary>
    private bool VerifyPresigned(HttpRequest request)
    {
        Dictionary<string, string> query = ParseQuery(request.RawQuery);
        // ParseQuery 刻意保留原始(仍编码的)值 —— 头认证那条路要用它拼规范查询串。
        // 这里要的是解码后的语义值:凭证里的 '/' 在 URL 里是 %2F,不还原就切不出五段。
        string credential = Decode("X-Amz-Credential");
        string signedHeaders = Decode("X-Amz-SignedHeaders");
        string signature = Decode("X-Amz-Signature");
        string amzDate = Decode("X-Amz-Date");

        string[] credentialParts = credential.Split('/');
        if (credentialParts.Length != 5 || !string.Equals(credentialParts[0], AccessKey, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SignatureFailures);
            Rejections.Enqueue($"presigned credential unusable on {request.Method} {request.Target}: '{credential}'");
            return false;
        }

        Dictionary<string, string> headers = [with(StringComparer.Ordinal)];
        foreach (string name in signedHeaders.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            headers[name] = request.Headers.TryGetValue(name, out string? value) ? value : string.Empty;
        }

        string canonicalQuery = string.Join('&', request.RawQuery
                                                        .Split('&', StringSplitOptions.RemoveEmptyEntries)
                                                        .Where(p => !p.StartsWith("X-Amz-Signature=", StringComparison.Ordinal))
                                                        .Select(p => p.Contains('=', StringComparison.Ordinal) ? p : p + "=")
                                                        .Order(StringComparer.Ordinal));

        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            request.Method, request.RawPath, canonicalQuery, headers, SigV4Verifier.UnsignedPayload, out _);
        string expected = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, credentialParts[1], credentialParts[2]),
            SigV4Verifier.CreateStringToSign(amzDate, string.Join('/', credentialParts.Skip(1)), canonicalRequest));
        if (!string.Equals(expected, signature, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SignatureFailures);
            Rejections.Enqueue(
                $"presigned signature mismatch on {request.Method} {request.Target}\n" +
                $"  expected={expected}\n  actual  ={signature}\n  canonical=<<{canonicalRequest}>>");
            return false;
        }
        return true;

        string Decode(string name) =>
            query.TryGetValue(name, out string? value) ? Uri.UnescapeDataString(value) : string.Empty;
    }

    private bool VerifySignature(HttpRequest request)
    {
        if (IsPresigned(request))
        {
            return VerifyPresigned(request);
        }
        if (!request.Headers.TryGetValue("Authorization", out string? authorization) ||
            !authorization.StartsWith(SigV4Verifier.Algorithm, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SignatureFailures);
            return false;
        }

        string? credential = Part(authorization, "Credential=");
        string? signedHeaders = Part(authorization, "SignedHeaders=");
        string? signature = Part(authorization, "Signature=");
        if (credential is null || signedHeaders is null || signature is null)
        {
            Interlocked.Increment(ref SignatureFailures);
            return false;
        }

        // Credential = AccessKey/date/region/s3/aws4_request
        string[] credentialParts = credential.Split('/');
        if (credentialParts.Length != 5 || !string.Equals(credentialParts[0], AccessKey, StringComparison.Ordinal))
        {
            Interlocked.Increment(ref SignatureFailures);
            return false;
        }
        string scope = string.Join('/', credentialParts.Skip(1));

        Dictionary<string, string> headers = [with(StringComparer.Ordinal)];
        foreach (string name in signedHeaders.Split(';'))
        {
            headers[name] = request.Headers.TryGetValue(name, out string? value) ? value : string.Empty;
        }

        // 规范查询串:客户端本就该发已排序的形式,这里仍重排一次 —— 顺序不一致会被抓出来。
        // 规范查询串把无值参数写成 `name=`。客户端可能在线路上发裸的 `?delete`
        // 却按 `delete=` 签名,真实 S3 会先归一化再校验,这里照做。
        string canonicalQuery = request.RawQuery.Length == 0
            ? string.Empty
            : string.Join('&', request.RawQuery
                                      .Split('&', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(p => p.Contains('=', StringComparison.Ordinal) ? p : p + "=")
                                      .Order(StringComparer.Ordinal));

        string payloadHash = request.Headers.TryGetValue("x-amz-content-sha256", out string? hash)
            ? hash
            : SigV4Verifier.EmptyPayloadHash;

        string canonicalRequest = SigV4Verifier.CreateCanonicalRequest(
            request.Method, request.RawPath, canonicalQuery, headers, payloadHash, out _);
        string expected = SigV4Verifier.CalculateSignature(
            SigV4Verifier.DeriveSigningKey(SecretKey, credentialParts[1], credentialParts[2]),
            SigV4Verifier.CreateStringToSign(
                request.Headers.GetValueOrDefault("x-amz-date", string.Empty), scope, canonicalRequest));

        // 负载哈希也要真的核对:客户端声称的 sha256 与实际发来的体对不上同样是签名问题。
        // 例外是流式签名(STREAMING-*):那种请求逐块加签,没有整体哈希可对,
        // 只校验请求头签名。
        bool payloadOk = payloadHash == SigV4Verifier.UnsignedPayload ||
                         payloadHash.StartsWith("STREAMING-", StringComparison.Ordinal) ||
                         string.Equals(payloadHash, SigV4Verifier.HashHex(request.Body), StringComparison.Ordinal);
        if (!string.Equals(expected, signature, StringComparison.Ordinal) || !payloadOk)
        {
            Interlocked.Increment(ref SignatureFailures);
            return false;
        }
        return true;

        static string? Part(string authorization, string prefix)
        {
            int start = authorization.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }
            start += prefix.Length;
            int end = authorization.IndexOf(',', start);
            return end < 0 ? authorization[start..] : authorization[start..end];
        }
    }

    // ---- 路由 ---------------------------------------------------------------

    private HttpResponse Route(HttpRequest request)
    {
        string path = Uri.UnescapeDataString(request.RawPath).TrimStart('/');
        Dictionary<string, string> query = ParseQuery(request.RawQuery);
        int slash = path.IndexOf('/');
        string bucket = slash < 0 ? path : path[..slash];
        string key = slash < 0 ? string.Empty : path[(slash + 1)..];

        if (bucket.Length == 0)
        {
            return request.Method == "GET" ? ListBuckets() : Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", "Not supported.");
        }

        // 桶级接口
        if (key.Length == 0)
        {
            return request.Method switch
            {
                "GET" when query.ContainsKey("location") => Xml($"<LocationConstraint>{Region}</LocationConstraint>"),
                "GET" => ListObjects(bucket, query),
                "HEAD" => _buckets.ContainsKey(bucket)
                    ? Ok([])
                    : Error(HttpStatusCode.NotFound, "NoSuchBucket", "The specified bucket does not exist."),
                "PUT" => CreateBucket(bucket),
                "DELETE" => DeleteBucket(bucket),
                "POST" when query.ContainsKey("delete") => DeleteObjects(bucket, request.Body),
                _ => Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", "Not supported."),
            };
        }

        // 对象级接口
        if (!_buckets.TryGetValue(bucket, out ConcurrentDictionary<string, StoredObject>? objects))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The specified bucket does not exist.");
        }
        if (request.Method is "PUT" or "POST" &&
            request.Headers.TryGetValue("Content-Type", out string? writeContentType) &&
            !query.ContainsKey("uploadId"))
        {
            ContentTypes[key] = writeContentType;
        }
        return request.Method switch
        {
            "POST" when query.ContainsKey("uploads") => CreateMultipartUpload(bucket, key),
            "POST" when query.TryGetValue("uploadId", out string? id) => CompleteMultipartUpload(objects, key, id),
            "DELETE" when query.TryGetValue("uploadId", out string? id) => AbortMultipartUpload(id),
            "PUT" when query.TryGetValue("uploadId", out string? id) => UploadPart(id, query, request.Body),
            "PUT" when request.Headers.ContainsKey("x-amz-copy-source") => CopyObject(objects, key, request.Headers["x-amz-copy-source"]),
            "PUT" => PutObject(objects, key, request.Body),
            "GET" => GetObject(objects, key, request.Headers.GetValueOrDefault("Range")),
            "HEAD" => HeadObject(objects, key),
            "DELETE" => DeleteObject(objects, key),
            _ => Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", "Not supported."),
        };
    }

    private HttpResponse ListBuckets()
    {
        var xml = new StringBuilder("<ListAllMyBucketsResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Owner><ID>test</ID><DisplayName>test</DisplayName></Owner><Buckets>");
        foreach (string name in _buckets.Keys.Order(StringComparer.Ordinal))
        {
            xml.Append(CultureInfo.InvariantCulture, $"<Bucket><Name>{name}</Name><CreationDate>2026-01-01T00:00:00.000Z</CreationDate></Bucket>");
        }
        xml.Append("</Buckets></ListAllMyBucketsResult>");
        return Xml(xml.ToString());
    }

    /// <summary>
    /// 按前缀与分隔符列举。分页刻意做成**每页最多 max-keys**(测试会把它压到 1 或 2),
    /// 好让续传令牌那条路径真的被走到 —— 一次性返回全部的话,分页 bug 永远测不出来。
    /// </summary>
    private HttpResponse ListObjects(string bucket, Dictionary<string, string> query)
    {
        if (!_buckets.TryGetValue(bucket, out ConcurrentDictionary<string, StoredObject>? objects))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The specified bucket does not exist.");
        }
        string prefix = Decode(query.GetValueOrDefault("prefix", string.Empty));
        string delimiter = Decode(query.GetValueOrDefault("delimiter", string.Empty));
        string after = Decode(query.GetValueOrDefault("continuation-token", query.GetValueOrDefault("marker", string.Empty)));
        int maxKeys = int.TryParse(query.GetValueOrDefault("max-keys"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 1000;
        // 只在客户端**请求**了 encoding-type=url 时才编码键名并声明 EncodingType ——
        // 真实 S3 正是这个行为。无条件编码会让不请求编码的客户端拿到一串 %XX 当作键名。
        bool encodeKeys = string.Equals(query.GetValueOrDefault("encoding-type"), "url", StringComparison.OrdinalIgnoreCase);

        List<string> matching =
        [
            .. objects.Keys
                      .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                      .Where(k => after.Length == 0 || string.CompareOrdinal(k, after) > 0)
                      .Order(StringComparer.Ordinal)
        ];

        List<string> contents = [];
        SortedSet<string> commonPrefixes = [with(StringComparer.Ordinal)];
        string? nextToken = null;
        int emitted = 0;
        foreach (string k in matching)
        {
            if (delimiter.Length > 0)
            {
                int index = k.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
                if (index >= 0)
                {
                    string common = k[..(index + delimiter.Length)];
                    if (commonPrefixes.Add(common))
                    {
                        emitted++;
                    }
                    if (emitted >= maxKeys)
                    {
                        nextToken = k;
                        break;
                    }
                    continue;
                }
            }
            contents.Add(k);
            emitted++;
            if (emitted >= maxKeys)
            {
                nextToken = k;
                break;
            }
        }
        bool truncated = nextToken is not null && matching.Count > matching.IndexOf(nextToken) + 1;

        var xml = new StringBuilder("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        xml.Append(CultureInfo.InvariantCulture, $"<Name>{bucket}</Name>");
        if (encodeKeys)
        {
            xml.Append("<EncodingType>url</EncodingType>");
        }
        xml.Append(CultureInfo.InvariantCulture, $"<IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>");
        if (truncated)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<NextContinuationToken>{MaybeEncode(nextToken, encodeKeys)}</NextContinuationToken>");
        }
        foreach (string k in contents)
        {
            StoredObject stored = objects[k];
            // 刻意写成一整条内插串:用 `+` 把几段内插串拼起来会先求值成 string,
            // 于是 Append(IFormatProvider, ...) 那个重载选不中。
            xml.Append(CultureInfo.InvariantCulture,
                $"<Contents><Key>{MaybeEncode(k, encodeKeys)}</Key><LastModified>{stored.LastModified:yyyy-MM-ddTHH:mm:ss.fffZ}</LastModified><ETag>&quot;{ETagOf(stored.Content)}&quot;</ETag><Size>{stored.Content.Length}</Size><StorageClass>STANDARD</StorageClass><Owner><ID>test</ID><DisplayName>tester</DisplayName></Owner></Contents>");
        }
        foreach (string common in commonPrefixes)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<CommonPrefixes><Prefix>{MaybeEncode(common, encodeKeys)}</Prefix></CommonPrefixes>");
        }
        xml.Append("</ListBucketResult>");
        return Xml(xml.ToString());
    }

    private HttpResponse CreateBucket(string bucket) =>
        _buckets.TryAdd(bucket, new(StringComparer.Ordinal))
            ? Ok([])
            : Error(HttpStatusCode.Conflict, "BucketAlreadyOwnedByYou", "The bucket already exists.");

    private HttpResponse DeleteBucket(string bucket)
    {
        if (!_buckets.TryGetValue(bucket, out ConcurrentDictionary<string, StoredObject>? objects))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The specified bucket does not exist.");
        }
        if (!objects.IsEmpty)
        {
            return Error(HttpStatusCode.Conflict, "BucketNotEmpty", "The bucket you tried to delete is not empty.");
        }
        _buckets.TryRemove(bucket, out _);
        return new() { Status = HttpStatusCode.NoContent, Body = [] };
    }

    private HttpResponse DeleteObjects(string bucket, byte[] body)
    {
        if (!_buckets.TryGetValue(bucket, out ConcurrentDictionary<string, StoredObject>? objects))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The specified bucket does not exist.");
        }
        var xml = new StringBuilder("<DeleteResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        foreach (string key in ExtractKeys(Encoding.UTF8.GetString(body)))
        {
            objects.TryRemove(key, out _);
            xml.Append(CultureInfo.InvariantCulture, $"<Deleted><Key>{key}</Key></Deleted>");
        }
        xml.Append("</DeleteResult>");
        return Xml(xml.ToString());
    }

    private static HttpResponse PutObject(ConcurrentDictionary<string, StoredObject> objects, string key, byte[] body)
    {
        objects[key] = new(body, DateTimeOffset.UtcNow);
        return new()
        {
            Status = HttpStatusCode.OK,
            Body = [],
            Headers = { ["ETag"] = $"\"{ETagOf(body)}\"" },
        };
    }

    private HttpResponse CopyObject(ConcurrentDictionary<string, StoredObject> objects, string key, string copySource)
    {
        string source = Uri.UnescapeDataString(copySource).TrimStart('/');
        int slash = source.IndexOf('/');
        if (slash < 0 ||
            !_buckets.TryGetValue(source[..slash], out ConcurrentDictionary<string, StoredObject>? sourceObjects) ||
            !sourceObjects.TryGetValue(source[(slash + 1)..], out StoredObject stored))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");
        }
        objects[key] = new(stored.Content, DateTimeOffset.UtcNow);
        return Xml($"<CopyObjectResult><ETag>&quot;{ETagOf(stored.Content)}&quot;</ETag></CopyObjectResult>");
    }

    private static HttpResponse GetObject(ConcurrentDictionary<string, StoredObject> objects, string key, string? range)
    {
        if (!objects.TryGetValue(key, out StoredObject stored))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");
        }
        if (range is { Length: > 0 } && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            string spec = range[6..];
            int dash = spec.IndexOf('-');
            if (dash > 0 && long.TryParse(spec[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out long from) &&
                from < stored.Content.Length)
            {
                byte[] slice = stored.Content[(int)from..];
                return new()
                {
                    Status = HttpStatusCode.PartialContent,
                    Body = slice,
                    Headers =
                    {
                        ["ETag"] = $"\"{ETagOf(stored.Content)}\"",
                        ["Content-Range"] = $"bytes {from}-{stored.Content.Length - 1}/{stored.Content.Length}",
                    },
                };
            }
        }
        return new()
        {
            Status = HttpStatusCode.OK,
            Body = stored.Content,
            Headers = { ["ETag"] = $"\"{ETagOf(stored.Content)}\"" },
        };
    }

    private static HttpResponse HeadObject(ConcurrentDictionary<string, StoredObject> objects, string key) =>
        objects.TryGetValue(key, out StoredObject stored)
            ? new()
            {
                Status = HttpStatusCode.OK,
                Body = stored.Content, // 只用于填 Content-Length,HEAD 不会真的写出去
                Headers =
                {
                    ["ETag"] = $"\"{ETagOf(stored.Content)}\"",
                    ["Last-Modified"] = stored.LastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture),
                    ["x-amz-storage-class"] = "STANDARD",
                },
            }
            : Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");

    private static HttpResponse DeleteObject(ConcurrentDictionary<string, StoredObject> objects, string key)
    {
        objects.TryRemove(key, out _);
        return new() { Status = HttpStatusCode.NoContent, Body = [] };
    }

    private HttpResponse CreateMultipartUpload(string bucket, string key)
    {
        string uploadId = $"upload-{Interlocked.Increment(ref _uploadCounter)}";
        _uploads[uploadId] = new();
        return Xml(
            "<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            $"<Bucket>{bucket}</Bucket><Key>{key}</Key><UploadId>{uploadId}</UploadId>" +
            "</InitiateMultipartUploadResult>");
    }

    private HttpResponse UploadPart(string uploadId, Dictionary<string, string> query, byte[] body)
    {
        if (!_uploads.TryGetValue(uploadId, out ConcurrentDictionary<int, byte[]>? parts))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.");
        }
        if (!int.TryParse(query.GetValueOrDefault("partNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidArgument", "partNumber is required.");
        }
        parts[number] = body;
        return new()
        {
            Status = HttpStatusCode.OK,
            Body = [],
            Headers = { ["ETag"] = $"\"{ETagOf(body)}\"" },
        };
    }

    private HttpResponse CompleteMultipartUpload(ConcurrentDictionary<string, StoredObject> objects, string key, string uploadId)
    {
        if (!_uploads.TryRemove(uploadId, out ConcurrentDictionary<int, byte[]>? parts))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.");
        }
        // 按分片号拼接 —— 并发上传的到达顺序是乱的,顺序错了这里就会拼出一个坏对象。
        var merged = new MemoryStream();
        foreach (int number in parts.Keys.Order())
        {
            merged.Write(parts[number]);
        }
        byte[] content = merged.ToArray();
        objects[key] = new(content, DateTimeOffset.UtcNow);
        return Xml(
            "<CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
            $"<Key>{key}</Key><ETag>&quot;{ETagOf(content)}&quot;</ETag>" +
            "</CompleteMultipartUploadResult>");
    }

    private HttpResponse AbortMultipartUpload(string uploadId)
    {
        _uploads.TryRemove(uploadId, out _);
        return new() { Status = HttpStatusCode.NoContent, Body = [] };
    }

    // ---- 小工具 -------------------------------------------------------------

    private static Dictionary<string, string> ParseQuery(string rawQuery)
    {
        Dictionary<string, string> query = [with(StringComparer.Ordinal)];
        if (rawQuery.Length == 0)
        {
            return query;
        }
        foreach (string pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals < 0)
            {
                query[pair] = string.Empty;
            }
            else
            {
                query[pair[..equals]] = pair[(equals + 1)..];
            }
        }
        return query;
    }

    private static string Decode(string value) => value.Length == 0 ? value : Uri.UnescapeDataString(value);

    /// <summary>只有客户端请求了 url 编码时才编码;否则原样输出(需转义 XML 元字符)。</summary>
    private static string MaybeEncode(string? value, bool encode) =>
        value is null ? string.Empty : encode ? SigV4Verifier.UriEncode(value, true) : System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static IEnumerable<string> ExtractKeys(string xml)
    {
        const string open = "<Key>";
        const string close = "</Key>";
        int index = 0;
        while (true)
        {
            int start = xml.IndexOf(open, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }
            start += open.Length;
            int end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                yield break;
            }
            yield return System.Net.WebUtility.HtmlDecode(xml[start..end]);
            index = end + close.Length;
        }
    }

    /// <summary>
    /// 非分片对象的 ETag 就是内容的 MD5 —— 这不是实现细节:AWSSDK 下载时会拿它
    /// 逐字节校验收到的内容,给一个别的哈希会让每次下载都以「hash not equal」失败。
    /// </summary>
    private static string ETagOf(byte[] content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(content));

    private static HttpResponse Ok(byte[] body) => new() { Status = HttpStatusCode.OK, Body = body };

    private static HttpResponse Xml(string xml) =>
        new()
        {
            Status = HttpStatusCode.OK,
            Body = Encoding.UTF8.GetBytes(xml),
            Headers = { ["Content-Type"] = "application/xml" },
        };

    private static HttpResponse Error(HttpStatusCode status, string code, string message) =>
        new()
        {
            Status = status,
            Body = Encoding.UTF8.GetBytes(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{message}</Message><RequestId>test</RequestId></Error>"),
            Headers = { ["Content-Type"] = "application/xml" },
        };

    private readonly record struct StoredObject(byte[] Content, DateTimeOffset LastModified);

    private sealed class HttpRequest
    {
        public required string Method { get; init; }

        public required string Target { get; init; }

        /// <summary>请求行里**原样**的路径(未解码);签名校验必须用它。</summary>
        public required string RawPath { get; init; }

        /// <summary>请求行里**原样**的查询串(未解码)。</summary>
        public required string RawQuery { get; init; }

        public required Dictionary<string, string> Headers { get; init; }

        public required byte[] Body { get; init; }
    }

    private sealed class HttpResponse
    {
        public required HttpStatusCode Status { get; init; }

        public required byte[] Body { get; init; }

        public Dictionary<string, string> Headers { get; } = [with(StringComparer.OrdinalIgnoreCase)];
    }
}
