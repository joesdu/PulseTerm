using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

[TestClass]
public sealed class ExternalEditSessionManagerTests
{
    [TestCleanup]
    public void CleanupExternalEditSessions() => ExternalEditSessionManager.CleanupAll();

    [TestMethod]
    [TestCategory("ExternalEdit")]
    [DataRow("..")]
    [DataRow(".")]
    [DataRow("/escape.txt")]
    [DataRow("../escape.txt")]
    [DataRow("nested/name.txt")]
    [DataRow("nested\\name.txt")]
    public async Task OpenAsync_RejectsUnsafeRemoteLeafNameBeforeTempOrEditor(string fileName)
    {
        ExternalEditSessionManager.CleanupAll();
        string tempRoot = Path.Combine(Path.GetTempPath(), "VelaShell", "remote-edit");
        ISftpService sftpService = Substitute.For<ISftpService>();

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ExternalEditSessionManager.OpenAsync(
                sftpService,
                Guid.NewGuid(),
                "/home/user/" + fileName,
                fileName,
                "not-a-real-editor",
                null
            )
        );

        Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), exception.Message);
        await sftpService
            .DidNotReceive()
            .DownloadFileAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<IProgress<TransferProgress>?>(),
                cancellationToken: Arg.Any<CancellationToken>()
            );
        Assert.IsFalse(Directory.Exists(tempRoot));
    }
}
