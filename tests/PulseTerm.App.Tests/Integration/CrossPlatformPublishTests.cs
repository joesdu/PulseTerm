using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit.Abstractions;

namespace PulseTerm.App.Tests.Integration;

[Collection("IntegrationTests")]
public class CrossPlatformPublishTests : IDisposable
{
    private readonly string _publishOutputDir;
    private readonly ITestOutputHelper _output;

    public CrossPlatformPublishTests(ITestOutputHelper output)
    {
        _output = output;
        _publishOutputDir = Path.Combine(Path.GetTempPath(), $"pulseterm_publish_test_{Guid.NewGuid():N}");
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
    }

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "src", "PulseTerm.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not find solution root. Expected src/PulseTerm.slnx in an ancestor directory.");
    }

    private (int exitCode, string output, string error) RunDotnetPublish(string rid)
    {
        var solutionRoot = FindSolutionRoot();
        var projectPath = Path.Combine(solutionRoot, "src", "PulseTerm.App");
        var outputDir = Path.Combine(_publishOutputDir, rid);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{projectPath}\" -r {rid} --self-contained -c Release -o \"{outputDir}\" /p:PublishSingleFile=true",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = solutionRoot
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
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

    private bool SkipIfNotNativeRid(string rid)
    {
        // These tests run actual `dotnet publish` which takes several minutes.
        // Only run when PULSETERM_PUBLISH_TESTS=1 environment variable is set.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PULSETERM_PUBLISH_TESTS")))
        {
            _output.WriteLine($"[SKIP] Publish tests are opt-in. Set PULSETERM_PUBLISH_TESTS=1 to enable. (RID: {rid})");
            return true;
        }

        if (!IsNativeRid(rid))
        {
            _output.WriteLine($"[SKIP] Skipping publish test for {rid}: current platform is {RuntimeInformation.RuntimeIdentifier}. Cross-compilation for non-native RIDs may not be supported without additional workloads.");
            return true;
        }
        return false;
    }

    [Fact]
    [Trait("Category", "CrossPlatform")]
    public void Publish_OsxArm64_Succeeds()
    {
        const string rid = "osx-arm64";
        if (SkipIfNotNativeRid(rid)) return;

        var (exitCode, stdout, stderr) = RunDotnetPublish(rid);

        exitCode.Should().Be(0,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var outputDir = Path.Combine(_publishOutputDir, rid);
        Directory.Exists(outputDir).Should().BeTrue();
        Directory.GetFiles(outputDir).Should().NotBeEmpty(
            $"publish output for {rid} should contain files");
    }

    [Fact]
    [Trait("Category", "CrossPlatform")]
    public void Publish_WinX64_Succeeds()
    {
        const string rid = "win-x64";
        if (SkipIfNotNativeRid(rid)) return;

        var (exitCode, stdout, stderr) = RunDotnetPublish(rid);

        exitCode.Should().Be(0,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var outputDir = Path.Combine(_publishOutputDir, rid);
        Directory.Exists(outputDir).Should().BeTrue();
        Directory.GetFiles(outputDir).Should().NotBeEmpty(
            $"publish output for {rid} should contain files");
    }

    [Fact]
    [Trait("Category", "CrossPlatform")]
    public void Publish_LinuxX64_Succeeds()
    {
        const string rid = "linux-x64";
        if (SkipIfNotNativeRid(rid)) return;

        var (exitCode, stdout, stderr) = RunDotnetPublish(rid);

        exitCode.Should().Be(0,
            $"dotnet publish for {rid} should succeed.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var outputDir = Path.Combine(_publishOutputDir, rid);
        Directory.Exists(outputDir).Should().BeTrue();
        Directory.GetFiles(outputDir).Should().NotBeEmpty(
            $"publish output for {rid} should contain files");
    }
}
