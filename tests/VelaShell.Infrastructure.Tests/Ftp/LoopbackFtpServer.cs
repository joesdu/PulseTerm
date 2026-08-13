using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Infrastructure.Tests.Ftp;

/// <summary>
/// 仅供测试使用的环回 FTP 服务器:把一个临时目录当作 FTP 根,实现 RFC 959 里
/// 客户端跑通「登录 → 列目录 → 上传/下载/改名/删除」所需的最小命令集。
/// <para>
/// 为什么自己写:CI 与开发机不保证能拉到 vsftpd 镜像,而只用 Mock 验证不了
/// 「FluentFTP 到底能不能跟一个真服务器把 PASV 数据连接跑通、LIST 输出能不能被解析成
/// <c>RemoteFileInfo</c>」—— 那正是这套后端最容易出错、也最值得守住的部分。
/// </para>
/// <para>
/// 刻意**不**在 FEAT 里通告 MLSD:强制客户端走 Unix 风格 LIST 解析,那是真实老服务器上的
/// 常见路径,也是权限/属主字符串真正被解析出来的路径。
/// </para>
/// </summary>
internal sealed class LoopbackFtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = [];
    private readonly Lock _sync = new();

    public LoopbackFtpServer(string rootDirectory)
    {
        Root = rootDirectory;
        Directory.CreateDirectory(Root);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>FTP 根目录在本地磁盘上的位置。</summary>
    public string Root { get; }

    /// <summary>监听端口(随机分配,避免与本机既有服务冲突)。</summary>
    public int Port { get; }

    /// <summary>最近一次登录使用的用户名(用于断言匿名登录)。</summary>
    public string? LastUser { get; private set; }

    /// <summary>最近一次登录使用的口令。</summary>
    public string? LastPassword { get; private set; }

    /// <summary>已接受的控制连接数(用于断言连接池确实开了多条连接)。</summary>
    public int AcceptedConnections;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref AcceptedConnections);
            Task session = Task.Run(() => HandleClientAsync(client));
            lock (_sync)
            {
                _clients.Add(session);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            var state = new SessionState();
            NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 VelaShell loopback FTP").ConfigureAwait(false);
            while (!_cts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return;
                }
                if (line is null)
                {
                    return;
                }
                int space = line.IndexOf(' ');
                string command = (space < 0 ? line : line[..space]).ToUpperInvariant();
                string argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();
                if (!await ExecuteAsync(command, argument, state, writer).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    /// <summary>执行一条命令;返回 false 表示会话结束(QUIT)。</summary>
    private async Task<bool> ExecuteAsync(string command, string argument, SessionState state, StreamWriter writer)
    {
        switch (command)
        {
            case "USER":
                LastUser = argument;
                await writer.WriteLineAsync("331 Need password").ConfigureAwait(false);
                return true;
            case "PASS":
                LastPassword = argument;
                await writer.WriteLineAsync("230 Logged in").ConfigureAwait(false);
                return true;
            case "SYST":
                await writer.WriteLineAsync("215 UNIX Type: L8").ConfigureAwait(false);
                return true;
            case "FEAT":
                // 只通告 SIZE / REST / UTF8:不给 MLSD,逼客户端走 Unix LIST 解析。
                await writer.WriteLineAsync("211-Features:").ConfigureAwait(false);
                await writer.WriteLineAsync(" SIZE").ConfigureAwait(false);
                await writer.WriteLineAsync(" REST STREAM").ConfigureAwait(false);
                await writer.WriteLineAsync(" UTF8").ConfigureAwait(false);
                await writer.WriteLineAsync("211 End").ConfigureAwait(false);
                return true;
            case "OPTS":
            case "TYPE":
            case "NOOP":
                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                return true;
            case "PWD":
            case "XPWD":
                await writer.WriteLineAsync($"257 \"{state.WorkingDirectory}\" is current directory").ConfigureAwait(false);
                return true;
            case "CWD":
                state.WorkingDirectory = Normalize(state, argument);
                await writer.WriteLineAsync(Directory.Exists(Local(state.WorkingDirectory))
                    ? "250 Directory changed"
                    : "550 No such directory").ConfigureAwait(false);
                return true;
            case "REST":
                state.RestartOffset = long.TryParse(argument, out long offset) ? offset : 0;
                await writer.WriteLineAsync("350 Restarting").ConfigureAwait(false);
                return true;
            case "PASV":
                await OpenPassiveAsync(state, writer, extended: false).ConfigureAwait(false);
                return true;
            case "EPSV":
                await OpenPassiveAsync(state, writer, extended: true).ConfigureAwait(false);
                return true;
            case "LIST":
            case "NLST":
                await SendListingAsync(state, argument, writer, command == "NLST").ConfigureAwait(false);
                return true;
            case "SIZE":
            {
                string path = Local(Normalize(state, argument));
                await writer.WriteLineAsync(File.Exists(path)
                    ? $"213 {new FileInfo(path).Length}"
                    : "550 Not found").ConfigureAwait(false);
                return true;
            }
            case "MDTM":
            {
                string path = Local(Normalize(state, argument));
                await writer.WriteLineAsync(File.Exists(path)
                    ? $"213 {File.GetLastWriteTimeUtc(path):yyyyMMddHHmmss}"
                    : "550 Not found").ConfigureAwait(false);
                return true;
            }
            case "RETR":
                await SendFileAsync(state, argument, writer).ConfigureAwait(false);
                return true;
            case "STOR":
            case "APPE":
                await ReceiveFileAsync(state, argument, writer, append: command == "APPE").ConfigureAwait(false);
                return true;
            case "DELE":
            {
                string path = Local(Normalize(state, argument));
                if (File.Exists(path))
                {
                    File.Delete(path);
                    await writer.WriteLineAsync("250 Deleted").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                }
                return true;
            }
            case "MKD":
            case "XMKD":
            {
                string remote = Normalize(state, argument);
                Directory.CreateDirectory(Local(remote));
                await writer.WriteLineAsync($"257 \"{remote}\" created").ConfigureAwait(false);
                return true;
            }
            case "RMD":
            case "XRMD":
            {
                string path = Local(Normalize(state, argument));
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    await writer.WriteLineAsync("250 Removed").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
                }
                return true;
            }
            case "RNFR":
                state.RenameFrom = Local(Normalize(state, argument));
                await writer.WriteLineAsync("350 Ready for destination").ConfigureAwait(false);
                return true;
            case "RNTO":
            {
                string target = Local(Normalize(state, argument));
                if (state.RenameFrom is { } source && (File.Exists(source) || Directory.Exists(source)))
                {
                    if (Directory.Exists(source))
                    {
                        Directory.Move(source, target);
                    }
                    else
                    {
                        File.Move(source, target, true);
                    }
                    await writer.WriteLineAsync("250 Renamed").ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteLineAsync("550 Rename failed").ConfigureAwait(false);
                }
                state.RenameFrom = null;
                return true;
            }
            case "SITE":
                // SITE CHMOD:真实服务器上是可选命令,这里接受并忽略,足以验证调用链路。
                await writer.WriteLineAsync("200 OK").ConfigureAwait(false);
                return true;
            case "QUIT":
                await writer.WriteLineAsync("221 Bye").ConfigureAwait(false);
                return false;
            default:
                await writer.WriteLineAsync("500 Unknown command").ConfigureAwait(false);
                return true;
        }
    }

    private async Task OpenPassiveAsync(SessionState state, StreamWriter writer, bool extended)
    {
        state.CloseData();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        state.DataListener = listener;
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        if (extended)
        {
            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{port}|)").ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync(
                $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})").ConfigureAwait(false);
        }
    }

    private async Task SendListingAsync(SessionState state, string argument, StreamWriter writer, bool namesOnly)
    {
        // 客户端有时会给 LIST 带上 -a / -l 这类参数,得剥掉再当路径用。
        string cleaned = string.Join(' ', argument.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(static token => !token.StartsWith('-')));
        string remote = Normalize(state, cleaned);
        string local = Local(remote);
        if (!Directory.Exists(local))
        {
            await writer.WriteLineAsync("550 No such directory").ConfigureAwait(false);
            return;
        }
        var payload = new StringBuilder();
        foreach (string directory in Directory.GetDirectories(local))
        {
            var info = new DirectoryInfo(directory);
            payload.Append(namesOnly ? info.Name : FormatEntry(info.Name, 4096, info.LastWriteTime, isDirectory: true)).Append("\r\n");
        }
        foreach (string file in Directory.GetFiles(local))
        {
            var info = new FileInfo(file);
            payload.Append(namesOnly ? info.Name : FormatEntry(info.Name, info.Length, info.LastWriteTime, isDirectory: false)).Append("\r\n");
        }
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString());
            await data.WriteAsync(bytes).ConfigureAwait(false);
        }
        state.CloseData();
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    private async Task SendFileAsync(SessionState state, string argument, StreamWriter writer)
    {
        string path = Local(Normalize(state, argument));
        if (!File.Exists(path))
        {
            await writer.WriteLineAsync("550 Not found").ConfigureAwait(false);
            return;
        }
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        await using (FileStream source = File.OpenRead(path))
        {
            if (state.RestartOffset > 0 && state.RestartOffset <= source.Length)
            {
                source.Seek(state.RestartOffset, SeekOrigin.Begin);
            }
            await source.CopyToAsync(data).ConfigureAwait(false);
        }
        state.RestartOffset = 0;
        state.CloseData();
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    private async Task ReceiveFileAsync(SessionState state, string argument, StreamWriter writer, bool append)
    {
        string path = Local(Normalize(state, argument));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await writer.WriteLineAsync("150 Opening data connection").ConfigureAwait(false);
        await using (Stream data = await state.AcceptDataAsync().ConfigureAwait(false))
        await using (FileStream target = append || state.RestartOffset > 0
                         ? new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None)
                         : new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await data.CopyToAsync(target).ConfigureAwait(false);
        }
        state.RestartOffset = 0;
        state.CloseData();
        await writer.WriteLineAsync("226 Transfer complete").ConfigureAwait(false);
    }

    /// <summary>Unix 风格的 LIST 行:权限、链接数、属主、属组、大小、时间、名称。</summary>
    private static string FormatEntry(string name, long length, DateTime modified, bool isDirectory)
    {
        string permissions = isDirectory ? "drwxr-xr-x" : "-rw-r--r--";
        string timestamp = modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{permissions}   1 deploy   staff {length,12} {timestamp} {name}";
    }

    /// <summary>把客户端给的相对/绝对路径规整成以 / 开头的远端绝对路径。</summary>
    private static string Normalize(SessionState state, string path)
    {
        string candidate = string.IsNullOrWhiteSpace(path)
            ? state.WorkingDirectory
            : path.StartsWith('/') ? path : $"{state.WorkingDirectory.TrimEnd('/')}/{path}";
        var segments = new List<string>();
        foreach (string segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>远端绝对路径 → 本地磁盘路径(始终限制在根目录内)。</summary>
    private string Local(string remote) =>
        Path.GetFullPath(Path.Combine(Root, remote.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

    private sealed class SessionState
    {
        public string WorkingDirectory { get; set; } = "/";
        public long RestartOffset { get; set; }
        public string? RenameFrom { get; set; }
        public TcpListener? DataListener { get; set; }

        public async Task<Stream> AcceptDataAsync()
        {
            TcpListener listener = DataListener ?? throw new InvalidOperationException("No PASV/EPSV issued.");
            TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            return new DataStream(client);
        }

        public void CloseData()
        {
            DataListener?.Stop();
            DataListener = null;
        }
    }

    /// <summary>数据连接:释放流即关闭连接(FTP 用连接关闭表示传输结束)。</summary>
    private sealed class DataStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
