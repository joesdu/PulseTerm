using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Hosting;
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
            "dev" => Dev(rest),
            "doctor" => Doctor(rest),
            "hosts" => Hosts(rest),
            // 旧名保留:文档与既有工程里到处都是它们,改名不该让谁的脚本一夜失效。
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
        throw new CliException("Direct CLI installation is disabled because it would bypass publisher approval and the protected installation receipt. Install the package from VelaShell's plugin manager; use `vela-plugin dev init` for development builds.");
    }

    // ---- 开发内环(dev 子命令族) -------------------------------------------

    /// <summary><c>vela-plugin dev &lt;子命令&gt;</c> 的分发。</summary>
    private static int Dev(string[] args)
    {
        string sub = args.FirstOrDefault() ?? throw new CliException(
            "Missing sub-command. Try `vela-plugin dev init`, `dev run`, `dev list`, `dev prune`, `dev link` or `dev unlink`.");
        string[] rest = args[1..];
        return sub switch
        {
            "init" => DevInit(rest),
            "run" => DevRun(rest),
            "list" => DevList(),
            "prune" => DevPrune(),
            "link" => DevLink(rest, remove: false),
            "unlink" => DevLink(rest, remove: true),
            _ => throw new CliException($"Unknown sub-command 'dev {sub}'. Run `vela-plugin help` for usage.")
        };
    }

    /// <summary>
    /// 一条命令配好 IDE 启动配置:找到本机安装的 VelaShell(读 <c>host.json</c>),
    /// 把"以调试器启动宿主 + 挂载本工程输出 + 用独立数据根"写进
    /// <c>Properties/launchSettings.json</c>。之后按 F5 即可断点调试。
    /// </summary>
    private static int DevInit(string[] args)
    {
        var options = CliOptions.Parse(args);
        string projectDirectory = Path.GetFullPath(options.Positional.FirstOrDefault() ?? ".");
        PluginManifest manifest = LoadProjectManifest(projectDirectory, options);
        HostRegistryEntry host = RequireHost(options);
        string outputDirectory = ResolveOutputDirectory(projectDirectory, options, manifest);
        string devRoot = Path.GetDirectoryName(outputDirectory.TrimEnd(Path.DirectorySeparatorChar))!;

        // 独立数据根是默认:开发者日常几乎肯定开着一份 VelaShell,共用数据根的话
        // 调试实例会撞上单实例互斥体("已在运行"然后干净退出),看起来就像启动配置写错了。
        // --shared-data 显式退回共用(想在真实配置里试插件时才需要)。
        string? dataRoot = options.Has("--shared-data")
            ? null
            : Path.GetFullPath(options.Get("--data-root") ?? DefaultDevDataRoot);

        var arguments = new List<string> { "--dev-root", devRoot };
        if (!options.Has("--no-wait-debugger"))
        {
            arguments.AddRange(["--wait-debugger", manifest.Id]);
        }
        if (dataRoot is not null)
        {
            arguments.AddRange(["--data-root", dataRoot]);
        }
        if (options.Has("--watch"))
        {
            arguments.Add("--dev-watch");
        }

        string profile = options.Get("--profile") ?? "VelaShell";
        string launchSettings = WriteLaunchSettings(projectDirectory, profile, host.ExePath, arguments);

        Console.WriteLine($"Host       {host.Version} ({host.ExePath})");
        Console.WriteLine($"Plugin     {manifest.Id} v{manifest.Version} [{manifest.HostMode}]");
        Console.WriteLine($"Dev root   {devRoot}");
        Console.WriteLine($"Data root  {dataRoot ?? host.DataRoot + "  (shared with your everyday instance)"}");
        Console.WriteLine($"Profile    {profile} -> {launchSettings}");
        Console.WriteLine();
        WarnOnHostMismatch(manifest, host);
        if (!Directory.Exists(outputDirectory))
        {
            Warn($"'{outputDirectory}' does not exist yet - run `dotnet build` before starting the host.");
        }
        if (options.Has("--link"))
        {
            DevLink([devRoot], remove: false);
        }
        Console.WriteLine("Press F5 in your IDE (profile above), or run `vela-plugin dev run`.");
        return 0;
    }

    /// <summary>不开 IDE 时拉起宿主:同样的参数,直接 <c>Process.Start</c>。</summary>
    private static int DevRun(string[] args)
    {
        var options = CliOptions.Parse(args);
        string projectDirectory = Path.GetFullPath(options.Positional.FirstOrDefault() ?? ".");
        PluginManifest manifest = LoadProjectManifest(projectDirectory, options);
        HostRegistryEntry host = RequireHost(options);
        string outputDirectory = ResolveOutputDirectory(projectDirectory, options, manifest);
        string devRoot = Path.GetDirectoryName(outputDirectory.TrimEnd(Path.DirectorySeparatorChar))!;
        RequireEntry(outputDirectory, manifest);

        var startInfo = new ProcessStartInfo(host.ExePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--dev-root");
        startInfo.ArgumentList.Add(devRoot);
        if (!options.Has("--shared-data"))
        {
            startInfo.ArgumentList.Add("--data-root");
            startInfo.ArgumentList.Add(Path.GetFullPath(options.Get("--data-root") ?? DefaultDevDataRoot));
        }
        if (options.Has("--wait-debugger"))
        {
            startInfo.ArgumentList.Add("--wait-debugger");
            startInfo.ArgumentList.Add(manifest.Id);
        }
        if (options.Has("--watch"))
        {
            startInfo.ArgumentList.Add("--dev-watch");
        }
        using Process process = Process.Start(startInfo)
                                ?? throw new CliException($"Could not start '{host.ExePath}'.");
        Console.WriteLine($"Started {Path.GetFileName(host.ExePath)} (pid {process.Id}) with {manifest.Id} mounted from {devRoot}");
        if (!options.Has("--wait"))
        {
            return 0;
        }
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>列出 plugins.dev.txt 里登记的开发根及其有效性。</summary>
    private static int DevList()
    {
        string listFile = Path.Combine(DataRoot, DevListFileName);
        if (!File.Exists(listFile))
        {
            Console.WriteLine($"No development roots registered ({listFile} does not exist).");
            return 0;
        }
        int count = 0;
        foreach (string line in File.ReadAllLines(listFile))
        {
            string root = line.Trim();
            if (root.Length == 0 || root.StartsWith('#'))
            {
                continue;
            }
            count++;
            string state = !Directory.Exists(root)
                ? "missing"
                : Directory.EnumerateDirectories(root)
                           .Any(d => File.Exists(Path.Combine(d, PluginManifestReader.FileName)))
                    ? "ok"
                    : "no plugin sub-directory";
            Console.WriteLine($"  [{state,-22}] {root}");
        }
        Console.WriteLine(count == 0 ? "No development roots registered." : $"{count} root(s) in {listFile}");
        return 0;
    }

    /// <summary>清掉 plugins.dev.txt 里已不存在的目录(换机器/删工程后的残留)。</summary>
    private static int DevPrune()
    {
        string listFile = Path.Combine(DataRoot, DevListFileName);
        if (!File.Exists(listFile))
        {
            Console.WriteLine("Nothing to prune.");
            return 0;
        }
        List<string> lines = [.. File.ReadAllLines(listFile)];
        int removed = lines.RemoveAll(line =>
        {
            string root = line.Trim();
            return root.Length > 0 && !root.StartsWith('#') && !Directory.Exists(root);
        });
        File.WriteAllLines(listFile, lines);
        Console.WriteLine(removed == 0 ? "Nothing to prune." : $"Removed {removed} stale root(s) from {listFile}");
        return 0;
    }

    /// <summary>列出本机登记过的 VelaShell 安装。</summary>
    private static int Hosts(string[] args)
    {
        bool all = CliOptions.Parse(args).Has("--all");
        IReadOnlyList<HostRegistryEntry> hosts = HostRegistry.List(onlyExisting: !all);
        if (hosts.Count == 0)
        {
            Console.WriteLine($"No VelaShell installation is registered in {HostRegistry.DefaultPath}.");
            Console.WriteLine("Start VelaShell once - it registers itself on every launch.");
            return 0;
        }
        foreach (HostRegistryEntry host in hosts)
        {
            Console.WriteLine($"{host.Version,-16} api {host.ApiLevel}  sdk {host.SdkVersion,-10} avalonia {host.AvaloniaVersion ?? "?"}");
            Console.WriteLine($"  exe        {host.ExePath}{(File.Exists(host.ExePath) ? "" : "   (missing)")}");
            Console.WriteLine($"  data root  {host.DataRoot}");
            Console.WriteLine($"  last seen  {host.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        return 0;
    }

    /// <summary>
    /// 一次性体检:宿主是否登记、版本是否匹配、构建产物是否干净、启动配置是否可用。
    /// 存在阻断性问题时返回 1。
    /// </summary>
    private static int Doctor(string[] args)
    {
        var options = CliOptions.Parse(args);
        string projectDirectory = Path.GetFullPath(options.Positional.FirstOrDefault() ?? ".");
        int problems = 0;

        Console.WriteLine($"vela-plugin {VelaPluginApi.SdkVersion} (apiLevel {VelaPluginApi.Level})");
        // --exe 直指一份安装(便携版、或还没启动过因而没登记的那种);否则查注册表。
        HostRegistryEntry? host = options.Get("--exe") is not null
            ? RequireHost(options)
            : HostRegistry.Resolve(options.Get("--host"));
        if (host is null)
        {
            Warn($"no VelaShell installation registered in {HostRegistry.DefaultPath}; start VelaShell once, "
                 + "or pass --exe to `vela-plugin dev init`.");
            problems++;
        }
        else
        {
            Console.WriteLine($"host        {host.Version}  api {host.ApiLevel}  sdk {host.SdkVersion}  "
                              + $"avalonia {host.AvaloniaVersion ?? "?"}");
            Console.WriteLine($"            {host.ExePath}");
            if (host.PluginHostPath is null)
            {
                Warn("this installation ships no VelaShell.PluginHost - isolated plugins cannot run against it.");
            }
        }

        string manifestPath = Path.Combine(projectDirectory, PluginManifestReader.FileName);
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"project     no {PluginManifestReader.FileName} in {projectDirectory} (skipping project checks)");
            return problems == 0 ? 0 : 1;
        }
        PluginManifest manifest = PluginManifestReader.Load(manifestPath);
        Console.WriteLine($"plugin      {manifest.Id} v{manifest.Version} [{manifest.HostMode}] api {manifest.ApiLevel}");
        if (host is not null)
        {
            problems += WarnOnHostMismatch(manifest, host);
        }

        string? outputDirectory = TryFindOutputDirectory(projectDirectory);
        if (outputDirectory is null)
        {
            Warn("no build output found under bin/ - run `dotnet build` first.");
            problems++;
        }
        else
        {
            Console.WriteLine($"output      {outputDirectory}");
            if (!File.Exists(Path.Combine(outputDirectory, manifest.Entry)))
            {
                Warn($"entry assembly '{manifest.Entry}' is missing from the output directory.");
                problems++;
            }
            if (!File.Exists(Path.Combine(outputDirectory, "plugin.json")))
            {
                Warn("plugin.json is not in the output directory - the host discovers plugins by that file. "
                     + "Reference VelaShell.PluginSdk.Build, or copy it yourself.");
                problems++;
            }
            // 共享程序集混进输出目录不会致命(装载器强制回落到宿主那一份),但它是个信号:
            // 多半是有人直接引用了 Avalonia 或 SDK 而没经过 VelaShell.PluginSdk.Build。
            foreach (string stray in Directory.EnumerateFiles(outputDirectory, "*.dll")
                                              .Select(Path.GetFileName)
                                              .Where(n => n is "VelaShell.PluginSdk.dll"
                                                          || (n?.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ?? false))
                                              .OfType<string>())
            {
                Warn($"'{stray}' is in the output directory; the loader always shares the host's copy, so it only bloats the package.");
            }
            if (!File.Exists(Path.Combine(outputDirectory, Path.ChangeExtension(manifest.Entry, ".deps.json"))))
            {
                Warn("no .deps.json next to the entry assembly - set <EnableDynamicLoading>true</EnableDynamicLoading>, "
                     + "or the plugin's own NuGet dependencies will not resolve at runtime.");
                problems++;
            }
        }

        string launchSettings = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettings))
        {
            Console.WriteLine("launch      not configured - run `vela-plugin dev init`");
        }
        else
        {
            string text = File.ReadAllText(launchSettings);
            Console.WriteLine(text.Contains("%VELASHELL_EXE%", StringComparison.Ordinal)
                ? "launch      placeholder executable path - run `vela-plugin dev init` to fill it in"
                : $"launch      {launchSettings}");
        }
        if (Process.GetProcessesByName("VelaShell").Length > 0)
        {
            Console.WriteLine("note        VelaShell is running. Debug instances use their own --data-root, so this is fine; "
                              + "a shared-data instance would refuse to start (single instance).");
        }
        return problems == 0 ? 0 : 1;
    }

    /// <summary>核对清单与某个宿主的兼容性,返回阻断性问题数。</summary>
    private static int WarnOnHostMismatch(PluginManifest manifest, HostRegistryEntry host)
    {
        int problems = 0;
        if (manifest.ApiLevel > host.ApiLevel)
        {
            Warn($"plugin targets apiLevel {manifest.ApiLevel} but the host supports {host.ApiLevel}; it will not load.");
            problems++;
        }
        if (manifest.MinSdkVersion is { } minSdk && IsOlder(host.SdkVersion, minSdk))
        {
            Warn($"plugin requires SDK >= {minSdk} but the host ships {host.SdkVersion}; it will be marked Incompatible.");
            problems++;
        }
        if (manifest.MinHostVersion is { } minHost && IsOlder(host.Version, minHost))
        {
            Warn($"plugin requires host >= {minHost} but this host is {host.Version}; it will be marked Incompatible.");
            problems++;
        }
        if (manifest.HostMode == PluginHostMode.Isolated && host.PluginHostPath is null)
        {
            Warn("plugin is isolated but the host installation has no VelaShell.PluginHost.");
            problems++;
        }
        return problems;
    }

    /// <summary>把启动配置写进(或并入)工程的 <c>Properties/launchSettings.json</c>。</summary>
    private static string WriteLaunchSettings(string projectDirectory, string profileName, string exePath,
        IReadOnlyList<string> arguments)
    {
        string directory = Path.Combine(projectDirectory, "Properties");
        string path = Path.Combine(directory, "launchSettings.json");
        JsonNode root;
        try
        {
            root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null) ?? new JsonObject();
        }
        catch (JsonException)
        {
            // 手写坏了的 launchSettings 不该让这条命令失败:整份重写,顺带把它修好。
            Warn($"'{path}' was not valid JSON and has been rewritten.");
            root = new JsonObject();
        }
        if (root["profiles"] is not JsonObject profiles)
        {
            profiles = [];
            root["profiles"] = profiles;
        }
        profiles[profileName] = new JsonObject
        {
            ["commandName"] = "Executable",
            ["executablePath"] = exePath,
            // 值里带空格的路径必须自己带引号:这一条最终会被拆成 argv,IDE 不替你加。
            ["commandLineArgs"] = string.Join(' ', arguments.Select(Quote)),
            ["workingDirectory"] = Path.GetDirectoryName(exePath) ?? ""
        };
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;

        static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }

    /// <summary>读工程根目录的 <c>plugin.json</c>(可用 <c>--manifest</c> 指定别处)。</summary>
    private static PluginManifest LoadProjectManifest(string projectDirectory, CliOptions options)
    {
        string path = options.Get("--manifest") is { } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(projectDirectory, PluginManifestReader.FileName);
        if (!File.Exists(path))
        {
            throw new CliException($"No {PluginManifestReader.FileName} in '{projectDirectory}'. " +
                                   "Run this from the plugin project directory, or pass --manifest <path>.");
        }
        return PluginManifestReader.Load(path);
    }

    /// <summary>选定要挂到哪一份宿主上(<c>--exe</c> 直指,<c>--host</c> 按版本/路径挑)。</summary>
    private static HostRegistryEntry RequireHost(CliOptions options)
    {
        if (options.Get("--exe") is { } exe)
        {
            string full = Path.GetFullPath(exe);
            if (!File.Exists(full))
            {
                throw new CliException($"'{full}' does not exist.");
            }
            // 直指可执行文件时旁边探一下 PluginHost:否则隔离插件会被误报成"这份安装跑不了"。
            string? pluginHost = Path.GetDirectoryName(full) is { } directory
                ? Path.Combine(directory, OperatingSystem.IsWindows()
                    ? "VelaShell.PluginHost.exe"
                    : "VelaShell.PluginHost")
                : null;
            return new()
            {
                ExePath = full,
                PluginHostPath = pluginHost is not null && File.Exists(pluginHost) ? pluginHost : null,
                Version = "(unknown)",
                ApiLevel = VelaPluginApi.Level,
                SdkVersion = VelaPluginApi.SdkVersion,
                DataRoot = DataRoot
            };
        }
        return HostRegistry.Resolve(options.Get("--host"))
               ?? throw new CliException(
                   $"No VelaShell installation is registered in {HostRegistry.DefaultPath}. "
                   + "Start VelaShell once (it registers itself on every launch), or pass --exe <path to VelaShell>.");
    }

    /// <summary>
    /// 解析插件的构建产物目录:<c>--output</c> 优先,否则在 <c>bin/</c> 下找带 <c>plugin.json</c>
    /// 的最新目录,再退回约定路径 <c>bin/Debug/net11.0</c>。
    /// </summary>
    private static string ResolveOutputDirectory(string projectDirectory, CliOptions options, PluginManifest manifest)
    {
        if (options.Get("--output", "-o") is { } explicitPath)
        {
            return Path.GetFullPath(explicitPath);
        }
        _ = manifest;
        return TryFindOutputDirectory(projectDirectory)
               ?? Path.Combine(projectDirectory, "bin", "Debug", "net11.0");
    }

    /// <summary><c>bin/</c> 下最近构建出来的那个含 <c>plugin.json</c> 的目录。</summary>
    private static string? TryFindOutputDirectory(string projectDirectory)
    {
        string bin = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }
        try
        {
            return Directory.EnumerateFiles(bin, PluginManifestReader.FileName, SearchOption.AllDirectories)
                            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}vpx{Path.DirectorySeparatorChar}",
                                StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .Select(Path.GetDirectoryName)
                            .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>调试实例的默认数据根(与日常实例分开,见 dev init 的注释)。</summary>
    private static string DefaultDevDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell-dev");

    /// <summary>开发根登记文件名。</summary>
    private const string DevListFileName = "plugins.dev.txt";

    /// <summary>
    /// <paramref name="actual" /> 是否比 <paramref name="required" /> 老。与宿主同一口径:
    /// 忽略预发布后缀,任一侧解析不出版本号就不判老(拦一个版本号写得怪的插件,损失大于收益)。
    /// </summary>
    private static bool IsOlder(string actual, string required)
    {
        static Version? ParseNumeric(string v)
        {
            string numeric = v.Split('-', 2)[0];
            if (!numeric.Contains('.', StringComparison.Ordinal))
            {
                numeric += ".0";
            }
            return Version.TryParse(numeric, out Version? parsed) ? parsed : null;
        }
        return ParseNumeric(actual) is { } left && ParseNumeric(required) is { } right && left < right;
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

        string listFile = Path.Combine(DataRoot, DevListFileName);
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
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell");

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

        DEVELOPMENT
          dev init [projectDir] Write an IDE launch profile that starts the installed VelaShell
                                with this plugin mounted (reads ~/.velashell/host.json)
                  --host        Pick a registered installation by version or exe path
                  --exe         Use this VelaShell executable instead of a registered one
              -o, --output      Plugin build output directory (default: newest under bin/)
                  --data-root   Data root for the debug instance (default: ~/.velashell-dev)
                  --shared-data Use the everyday data root instead of a separate one
                  --no-wait-debugger  Do not pass --wait-debugger (isolated plugins only)
                  --watch       Also pass --dev-watch (auto-reload on rebuild)
                  --profile     Launch profile name (default: VelaShell)
                  --link        Also register the directory in plugins.dev.txt
          dev run [projectDir]  Start the host with this plugin mounted (no IDE needed)
                  --wait        Wait for the host to exit and return its exit code
                  --wait-debugger / --watch / --data-root / --shared-data / --exe / --host
          dev list              Show registered development roots and their state
          dev prune             Drop development roots that no longer exist
          dev link [dir]        Register a plugin output directory for development
          dev unlink [dir]      Remove such a registration
          hosts                 List VelaShell installations registered on this machine
                  --all         Include installations whose executable is gone
          doctor [projectDir]   Check host, manifest, build output and launch profile

        EXAMPLES
          dotnet build -c Release
          vela-plugin pack bin/Release/net11.0 -k ~/keys/acme.pem
          vela-plugin dev init          # then press F5 in your IDE
          vela-plugin doctor
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

        public string? Get(string name, string? alias = null) =>
            _named.TryGetValue(name, out string? value)
            || (alias is not null && _named.TryGetValue(alias, out value))
                ? value
                : null;
    }
}
