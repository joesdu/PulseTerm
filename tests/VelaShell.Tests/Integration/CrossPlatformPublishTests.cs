using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VelaShell.Tests.Integration;

[TestClass]
public class CrossPlatformPublishTests : IDisposable
{
    private readonly string _publishOutputDir;

    public TestContext TestContext { get; set; } = null!;

    public CrossPlatformPublishTests()
    {
        _publishOutputDir = Path.Combine(Path.GetTempPath(), $"velashell_publish_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_publishOutputDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_publishOutputDir))
                Directory.Delete(_publishOutputDir, true);
        }
        catch
        {
        }
        GC.SuppressFinalize(this);
    }

    private static string FindSolutionRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not find solution root. Expected VelaShell.slnx in an ancestor directory.");
    }

    private (int exitCode, string output, string error) RunDotnetPublish(string rid)
    {
        string solutionRoot = FindSolutionRoot();
        string projectPath = Path.Combine(solutionRoot, "src", "VelaShell");
        string outputDir = Path.Combine(_publishOutputDir, rid);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // 与 release.yml / publish-all.ps1 同一条命令(刻意不带 PublishSingleFile:
            // 隔离插件的 PluginHost 需要磁盘上的真实可执行体)。
            Arguments = $"publish \"{projectPath}\" -r {rid} --self-contained -c Release -o \"{outputDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = solutionRoot
        };

        using Process process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);

        return (process.ExitCode, stdout, stderr);
    }

    private static bool IsNativeRid(string rid)
    {
        return rid switch
        {
            "osx-arm64" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.Arm64,
            "osx-x64" => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.OSArchitecture == Architecture.X64,
            "win-x64" => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X64,
            "linux-x64" => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64,
            _ => false
        };
    }

    /// <summary>
    /// 没开开关、或当前平台不是这个 RID 的原生平台,就把本用例标为<b>未执行</b>。
    /// </summary>
    /// <remarks>
    /// 以前是"写一行 <c>[SKIP]</c> 日志然后 return":框架看到的是一次正常返回,报告上写着
    /// "通过"。于是一份全绿的报告里混着一批<b>一次 publish 都没跑过</b>的用例 ——
    /// 而发布这条路恰恰是最需要如实知道"到底验没验过"的地方。
    /// </remarks>
    private static void RequireNativePublish(string rid)
    {
        // 这些用例真的会跑 `dotnet publish`,一次好几分钟,所以默认不跑。
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELASHELL_PUBLISH_TESTS")))
        {
            Assert.Inconclusive($"Publish tests are opt-in. Set VELASHELL_PUBLISH_TESTS=1 to enable. (RID: {rid})");
        }
        if (!IsNativeRid(rid))
        {
            Assert.Inconclusive(
                $"Skipping publish test for {rid}: current platform is {RuntimeInformation.RuntimeIdentifier}. "
                + "Cross-compilation for non-native RIDs may not be supported without additional workloads.");
        }
    }

    [TestMethod]
    [TestCategory("CrossPlatform")]
    public void Publish_OsxArm64_Succeeds()
    {
        const string rid = "osx-arm64";
        RequireNativePublish(rid);

        (int exitCode, string? stdout, string? stderr) = RunDotnetPublish(rid);

        Assert.AreEqual(0, exitCode,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        string outputDir = Path.Combine(_publishOutputDir, rid);
        Assert.IsTrue(Directory.Exists(outputDir));
        Assert.IsNotEmpty(Directory.GetFiles(outputDir),
            $"publish output for {rid} should contain files");
    }

    [TestMethod]
    [TestCategory("CrossPlatform")]
    public void Publish_WinX64_Succeeds()
    {
        const string rid = "win-x64";
        RequireNativePublish(rid);

        (int exitCode, string? stdout, string? stderr) = RunDotnetPublish(rid);

        Assert.AreEqual(0, exitCode,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        string outputDir = Path.Combine(_publishOutputDir, rid);
        Assert.IsTrue(Directory.Exists(outputDir));
        Assert.IsNotEmpty(Directory.GetFiles(outputDir),
            $"publish output for {rid} should contain files");
    }

    [TestMethod]
    [TestCategory("CrossPlatform")]
    public void Publish_LinuxX64_Succeeds()
    {
        const string rid = "linux-x64";
        RequireNativePublish(rid);

        (int exitCode, string? stdout, string? stderr) = RunDotnetPublish(rid);

        Assert.AreEqual(0, exitCode,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        string outputDir = Path.Combine(_publishOutputDir, rid);
        Assert.IsTrue(Directory.Exists(outputDir));
        Assert.IsNotEmpty(Directory.GetFiles(outputDir),
            $"publish output for {rid} should contain files");
    }
}
