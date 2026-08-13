using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Sftp;

[TestClass]
[TestCategory("Sftp")]
public sealed class SftpServiceConcurrencyCharacterizationTests
{
    [TestMethod]
    public async Task ListDirectoryAsync_WhenCalledConcurrently_AllowsLegacyCallsToOverlap()
    {
        // Given
        var sessionId = Guid.NewGuid();
        ISshConnectionService connection = Substitute.For<ISshConnectionService>();
        ISftpClientWrapper client = Substitute.For<ISftpClientWrapper>();
        client.IsConnected.Returns(true);
        connection.GetSession(sessionId).Returns(new SshSession
        {
            SessionId = sessionId,
            ConnectionInfo = new() { Host = "test.example.com", Port = 22, Username = "test", AuthMethod = AuthMethod.Password },
            Status = SessionStatus.Connected
        });
        var bothCallsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCalls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeCalls = 0;
        int maximumConcurrency = 0;
        client.ListDirectoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            int active = Interlocked.Increment(ref activeCalls);
            maximumConcurrency = Math.Max(maximumConcurrency, active);
            if (active == 2)
            {
                bothCallsStarted.TrySetResult();
            }

            try
            {
                await releaseCalls.Task.WaitAsync(call.Arg<CancellationToken>());
                return [];
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        });
        SftpService service = new(connection, _ => client);

        // When
        Task<List<RemoteFileInfo>> first = service.ListDirectoryAsync(sessionId, "/one");
        Task<List<RemoteFileInfo>> second = service.ListDirectoryAsync(sessionId, "/two");
        await bothCallsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseCalls.TrySetResult();

        // Then
        await Task.WhenAll(first, second);
        Assert.AreEqual(2, maximumConcurrency);
    }
}
