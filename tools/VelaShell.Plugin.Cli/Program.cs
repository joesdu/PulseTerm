using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Manifest;
using VelaShell.PluginSdk.Packaging;

namespace VelaShell.Plugin.Cli;

/// <summary>
/// <c>vela-plugin</c>:插件作者的命令行工具。所有与包格式、清单规则相关的逻辑都直接调用
/// <c>VelaShell.PluginSdk</c> —— 与宿主装包走同一份实现,不存在"工具认、宿主不认"的缝。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex) when (ex is VpxFormatException or PluginManifestException or CliException)
        {
            Error(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Error(ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }
        string[] rest = args[1..];
        return args[0] switch
        {
            "pack" => Pack(rest),
            "validate" => Validate(rest),
            "info" => Info(rest),
            "unpack" => Unpack(rest),
            "keygen" => KeyGen(rest),
            "sign" => Sign(rest),
            "verify" => Verify(rest),
            "install" => Install(rest),
            "dev-link" => DevLink(rest, remove: false),
            "dev-unlink" => DevLink(rest, remove: true),
            "--version" or "-v" => PrintVersion(),
            _ => throw new CliException($"Unknown command '{args[0]}'. Run `vela-plugin help` for usage.")
        };
    }

    // ---- 命令 -------------------------------------------------------------

    /// <summary>把插件产物目录打成 .vpx。</summary>
    private static int Pack(string[] args)
    {
        var options = CliOptions.Parse(args);
        string source = Path.GetFullPath(options.Positional.FirstOrDefault() ?? ".");
        PluginManifest manifest = LoadManifest(source);
        RequireEntry(source, manifest);

        string fileName = $"{manifest.Id}-{manifest.Version}{VpxContainer.FileExtension}";
        string output = options.Get("--output", "-o") is { } requested
            // 目录 → 用约定文件名;否则当成完整路径。MSBuild 的 PackVpx 传的就是目录。
            ? Path.GetFullPath(Directory.Exists(requested)
                               || !requested.EndsWith(VpxContainer.FileExtension, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(requested, fileName)
                : requested)
            : Path.GetFullPath(Path.Combine(source, "..", fileName));

        using ECDsa? key = LoadPrivateKey(options.Get("--key", "-k"));
        VpxContainer.Pack(source, output, new()
        {
            Mask = !options.Has("--no-mask"),
            SigningKey = key
        });
        VpxPackageInfo info = VpxContainer.ReadInfo(output);
        Console.WriteLine($"Packed {manifest.Id} v{manifest.Version}");
        Console.WriteLine($"  -> {output}");
        Console.WriteLine($"     payload {info.PayloadLength} bytes, sha256 {info.PayloadSha256}");
        Console.WriteLine($"     {(info.Signature is null ? "unsigned" : "signed by " + VpxContainer.PublicKeyFingerprint(info.Signature.PublicKey))}");
        return 0;
    }

    /// <summary>校验清单(目录或 plugin.json 路径均可)。</summary>
    private static int Validate(string[] args)
    {
        string target = Path.GetFullPath(CliOptions.Parse(args).Positional.FirstOrDefault() ?? ".");
        string directory = Directory.Exists(target) ? target : Path.GetDirectoryName(target)!;
        PluginManifest manifest = LoadManifest(directory);
        RequireEntry(directory, manifest);
        Console.WriteLine($"OK  {manifest.Id} v{manifest.Version} ({manifest.DisplayName})");
        Console.WriteLine($"    entry      {manifest.Entry}");
        Console.WriteLine($"    hostMode   {manifest.HostMode}");
        Console.WriteLine($"    apiLevel   {manifest.ApiLevel} (this SDK: {VelaPluginApi.Level})");
        Console.WriteLine($"    author     {manifest.Author ?? manifest.Publisher ?? "(not set)"}");
        if (manifest.ApiLevel > VelaPluginApi.Level)
        {
            Warn($"apiLevel {manifest.ApiLevel} is newer than this SDK ({VelaPluginApi.Level}); hosts built on this SDK will refuse to load the plugin.");
        }
        if (manifest.Author is null && manifest.Publisher is null)
        {
            Warn("neither \"author\" nor \"publisher\" is set - the plugin manager page will show no author.");
        }
        return 0;
    }

    /// <summary>打印一个 .vpx 的头部信息与签名状态。</summary>
    private static int Info(string[] args)
    {
        string package = RequirePackagePath(args);
        VpxPackageInfo info = VpxContainer.ReadInfo(package);
        Console.WriteLine($"{Path.GetFileName(package)}");
        Console.WriteLine($"  format     v{info.FormatVersion}");
        Console.WriteLine($"  flags      {info.Flags}");
        Console.WriteLine($"  payload    {info.PayloadLength} bytes");
        Console.WriteLine($"  sha256     {info.PayloadSha256}");
        VpxSignatureState signature = VpxContainer.VerifySignature(info);
        Console.WriteLine($"  signature  {(signature == VpxSignatureState.Trusted ? "Valid" : signature.ToString())}"
                          + (info.Signature is { } s ? $" ({VpxContainer.PublicKeyFingerprint(s.PublicKey)})" : ""));
        // 顺带把清单读出来:光有摘要看不出这是哪个插件。
        using Stream payload = VpxContainer.OpenPayload(package);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        if (archive.GetEntry(PluginManifestReader.FileName) is { } entry)
        {
            using StreamReader reader = new(entry.Open());
            PluginManifest manifest = PluginManifestReader.Parse(reader.ReadToEnd());
            Console.WriteLine($"  plugin     {manifest.Id} v{manifest.Version} ({manifest.DisplayName})");
            Console.WriteLine($"  author     {manifest.Author ?? manifest.Publisher ?? "(not set)"}");
        }
        return 0;
    }

    /// <summary>解开一个 .vpx 到目录(排障用)。</summary>
    private static int Unpack(string[] args)
    {
        var options = CliOptions.Parse(args);
        string package = RequirePackagePath(args);
        string destination = Path.GetFullPath(options.Positional.ElementAtOrDefault(1)
                                              ?? Path.GetFileNameWithoutExtension(package));
        using Stream payload = VpxContainer.OpenPayload(package);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        Directory.CreateDirectory(destination);
        ExtractZipSafely(archive, destination);
        Console.WriteLine($"Unpacked to {destination}");
        return 0;
    }

    /// <summary>生成一对 P-256 签名密钥。</summary>
    private static int KeyGen(string[] args)
    {
        var options = CliOptions.Parse(args);
        string path = Path.GetFullPath(options.Get("--output", "-o") ?? "velashell-plugin-key.pem");
        if (File.Exists(path) && !options.Has("--force"))
        {
            throw new CliException($"'{path}' already exists. Pass --force to overwrite (the old key becomes unusable, " +
                                   "and packages signed with it can no longer be updated under the same identity).");
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        }
        else
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            using var writer = new StreamWriter(stream);
            writer.Write(key.ExportPkcs8PrivateKeyPem());
        }
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        Console.WriteLine($"Private key written to {path}  (keep it secret, keep it backed up)");
        Console.WriteLine($"Public key (base64 SPKI): {publicKey}");
        Console.WriteLine($"Fingerprint: {VpxContainer.PublicKeyFingerprint(publicKey)}");
        return 0;
    }

    /// <summary>给一个已有的 .vpx 补签名(重写容器)。</summary>
    private static int Sign(string[] args)
    {
        var options = CliOptions.Parse(args);
        string package = RequirePackagePath(args);
        using ECDsa key = LoadPrivateKey(options.Get("--key", "-k"))
                          ?? throw new CliException("Signing needs a private key: --key <key.pem> (create one with `vela-plugin keygen`).");
        string output = Path.GetFullPath(options.Get("--output", "-o") ?? package);

        // 先整包读进内存再写:输出可能就是输入本身(原地签名)。
        byte[] payload;
        using (Stream stream = VpxContainer.OpenPayload(package))
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            payload = buffer.ToArray();
        }
        using (var source = new MemoryStream(payload))
        using (FileStream destination = File.Create(output))
        {
            VpxContainer.Write(destination, source, new() { SigningKey = key });
        }
        Console.WriteLine($"Signed {output}");
        Console.WriteLine($"  fingerprint: {VpxContainer.PublicKeyFingerprint(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))}");
        return 0;
    }

    /// <summary>验证一个 .vpx 的完整性与签名。</summary>
    private static int Verify(string[] args)
    {
        var options = CliOptions.Parse(args);
        string package = RequirePackagePath(args);
        // OpenPayload 本身就会校验摘要:能打开就说明内容未损坏。
        VpxPackageInfo info;
        using (Stream _ = VpxContainer.OpenPayload(package, out info))
        {
        }
        string? expectedKey = options.Get("--key", "-k");
        VpxSignatureState state = expectedKey is null
            ? VpxContainer.VerifySignature(info)
            : VpxContainer.VerifySignature(info, [expectedKey]);
        Console.WriteLine($"payload  OK ({info.PayloadLength} bytes, sha256 {info.PayloadSha256})");
        Console.WriteLine($"signature {(expectedKey is null && state == VpxSignatureState.Trusted ? "Valid (publisher identity not checked)" : state.ToString())}");
        return state is VpxSignatureState.Invalid or VpxSignatureState.Untrusted ? 1 : 0;
    }

    /// <summary>安装必须由宿主完成，确保签名授权和受保护安装收据不可绕过。</summary>
    private static int Install(string[] args)
    {
        _ = RequirePackagePath(args);
        throw new CliException("Direct CLI installation is disabled because it would bypass publisher approval and the protected installation receipt. Install the package from VelaShell's plugin manager; use `dev-link` only for development builds.");
    }

    /// <summary>把一个插件输出目录登记进 / 移出 plugins.dev.txt(开发期挂载)。</summary>
    private static int DevLink(string[] args, bool remove)
    {
        var options = CliOptions.Parse(args);
        string directory = Path.GetFullPath(options.Positional.FirstOrDefault() ?? ".");
        // 登记的是"插件根目录的父目录":宿主扫的是根目录下的一级子目录,每个子目录一个插件。
        // 传进来的既可能是 <...>/bin/Debug/net11.0(插件本身),也可能已经是父目录。
        string root = File.Exists(Path.Combine(directory, PluginManifestReader.FileName))
            ? Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))!
            : directory;

        string listFile = Path.Combine(DataRoot, "plugins.dev.txt");
        Directory.CreateDirectory(DataRoot);
        List<string> lines = File.Exists(listFile) ? [.. File.ReadAllLines(listFile)] : [];
        bool Matches(string line) => line.Trim().Equals(root, StringComparison.OrdinalIgnoreCase);

        if (remove)
        {
            int removed = lines.RemoveAll(Matches);
            File.WriteAllLines(listFile, lines);
            Console.WriteLine(removed > 0 ? $"Unlinked {root}" : $"{root} was not linked.");
            return 0;
        }
        if (lines.Any(Matches))
        {
            Console.WriteLine($"Already linked: {root}");
            return 0;
        }
        if (lines.Count == 0)
        {
            lines.Add("# VelaShell development plugin roots - one directory per line, '#' starts a comment.");
            lines.Add("# Each listed directory is scanned for plugin sub-directories, exactly like the installed");
            lines.Add("# plugins folder; plugins found here are badged DEV in the plugin manager page.");
        }
        lines.Add(root);
        File.WriteAllLines(listFile, lines);
        Console.WriteLine($"Linked {root}");
        Console.WriteLine($"  registered in {listFile}");
        Console.WriteLine("Restart VelaShell to pick it up.");
        return 0;
    }

    private static int PrintVersion()
    {
        // 打 InformationalVersion 而不是 AssemblyVersion:后者只随主版本动(它是绑定标识,
        // 不是给人看的),打它会让每个补丁版本都自称 1.0.0.0。这里要的是包版本,含预发布后缀。
        Console.WriteLine(typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown");
        return 0;
    }

    // ---- 辅助 -------------------------------------------------------------

    /// <summary>VelaShell 的数据根目录,与宿主的 VelaShellStoragePaths 保持一致。</summary>
    private static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VelaShell");

    private static PluginManifest LoadManifest(string directory)
    {
        string manifestPath = Path.Combine(directory, PluginManifestReader.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new CliException($"No {PluginManifestReader.FileName} in '{directory}'. " +
                                   "Point the command at the plugin's build output directory.");
        }
        return PluginManifestReader.Load(manifestPath);
    }

    private static void RequireEntry(string directory, PluginManifest manifest)
    {
        if (!File.Exists(Path.Combine(directory, manifest.Entry)))
        {
            throw new CliException($"Entry assembly '{manifest.Entry}' is missing from '{directory}'. " +
                                   "Build the plugin project first.");
        }
    }

    private static string RequirePackagePath(string[] args)
    {
        string? path = CliOptions.Parse(args).Positional.FirstOrDefault() ?? throw new CliException("Missing package path. Usage: vela-plugin <command> <package.vpx>");
        string full = Path.GetFullPath(path);
        return File.Exists(full) ? full : throw new CliException($"Package not found: {full}");
    }

    private static ECDsa? LoadPrivateKey(string? path)
    {
        if (path is null)
        {
            return null;
        }
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new CliException($"Key file not found: {full}");
        }
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(full));
        }
        catch (ArgumentException ex)
        {
            key.Dispose();
            throw new CliException($"'{full}' is not a PEM private key: {ex.Message}");
        }
        return key;
    }

    private const int MaxUnpackEntries = 10_000;
    private const long MaxUnpackedBytes = 512L * 1024 * 1024;

    private static void ExtractZipSafely(ZipArchive archive, string destination)
    {
        if (archive.Entries.Count > MaxUnpackEntries)
        {
            throw new CliException($"Package contains too many entries ({archive.Entries.Count}; limit {MaxUnpackEntries}).");
        }
        string root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        long remaining = MaxUnpackedBytes;
        byte[] buffer = new byte[64 * 1024];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new CliException($"Package contains a symbolic link: {entry.FullName}");
            }
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, pathComparison))
            {
                throw new CliException($"Package entry escapes the destination: {entry.FullName}");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream source = entry.Open();
            using FileStream output = File.Create(target);
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                remaining -= read;
                if (remaining < 0)
                {
                    throw new CliException($"Package expands beyond the {MaxUnpackedBytes / (1024 * 1024)} MB limit.");
                }
                output.Write(buffer, 0, read);
            }
        }
    }

    private static void Error(string message) => Console.Error.WriteLine($"error: {message}");

    private static void Warn(string message) => Console.Error.WriteLine($"warning: {message}");

    private static void PrintUsage() => Console.WriteLine(
        """
        vela-plugin - VelaShell plugin developer tool

        USAGE
          vela-plugin <command> [arguments]

        COMMANDS
          pack <dir>            Pack a plugin output directory into a .vpx package
              -o, --output      Output path (default: <id>-<version>.vpx next to <dir>)
              -k, --key         Sign with this PEM private key
                  --no-mask     Store the zip payload unmasked (diagnostics only)
          validate [dir]        Validate plugin.json and the entry assembly
          info <pkg.vpx>        Show container header, signature and manifest
          verify <pkg.vpx>      Verify payload digest and signature
              -k, --key         Base64 public key that the signature must match
          unpack <pkg.vpx> [dir]  Extract a package (diagnostics)
          sign <pkg.vpx>        Add or replace the signature of a package
              -k, --key         PEM private key (required)
              -o, --output      Write to this path instead of signing in place
          keygen                Create a P-256 signing key pair
              -o, --output      Key file path (default: velashell-plugin-key.pem)
                  --force       Overwrite an existing key file
          install <pkg.vpx>     Disabled: install through VelaShell to record trust securely
          dev-link [dir]        Register a plugin output directory for development
          dev-unlink [dir]      Remove such a registration

        EXAMPLES
          dotnet build -c Release
          vela-plugin pack bin/Release/net11.0 -k ~/keys/acme.pem
          vela-plugin dev-link bin/Debug/net11.0
        """);

    /// <summary>可读的用法错误(与格式/清单错误一样只打印消息,不打印堆栈)。</summary>
    private sealed class CliException(string message) : Exception(message);

    /// <summary>极简参数解析:<c>--name value</c> / <c>--flag</c> / 位置参数。</summary>
    private sealed class CliOptions
    {
        private readonly Dictionary<string, string?> _named = [with(StringComparer.Ordinal)];

        public List<string> Positional { get; } = [];

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (!name.StartsWith('-'))
                {
                    options.Positional.Add(name);
                    continue;
                }
                // 先取名再前进:反过来写(在索引器里读 args[i])会把游标已经推到的**值**当成键。
                options._named[name] = i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : null;
            }
            return options;
        }

        public bool Has(string name) => _named.ContainsKey(name);

        public string? Get(string name, string alias) =>
            _named.TryGetValue(name, out string? value) || _named.TryGetValue(alias, out value) ? value : null;
    }
}
