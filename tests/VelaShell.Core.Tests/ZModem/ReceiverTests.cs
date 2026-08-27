using System.Text;
using VelaShell.Core.Tests.FileTransfer;
using VelaShell.Core.ZModem.Protocol;

namespace VelaShell.Core.Tests.ZModem;

[TestClass]
[TestCategory("ZModem")]
public class ReceiverTests
{
    private static async Task RunAsync(
        (string Name, byte[] Data)[] files,
        bool useCrc32,
        int subpacketSize,
        InMemoryFileSink sink)
    {
        (InMemoryByteDuplex receiverSide, InMemoryByteDuplex senderSide) = InMemoryByteDuplex.CreatePair();
        var receiver = new ZModemReceiver(receiverSide, sink);
        var sender = new TestSender(senderSide, useCrc32, subpacketSize);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<Core.FileTransfer.Model.FileTransferSession> recvTask = receiver.ReceiveAsync(cts.Token);
        Task sendTask = sender.SendBatchAsync(files, cts.Token);
        await Task.WhenAll(recvTask, sendTask);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Receive_SingleTextFile_Succeeds(bool useCrc32)
    {
        byte[] content = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog.\n");
        var sink = new InMemoryFileSink();
        await RunAsync([("hello.txt", content)], useCrc32, 1024, sink);

        Assert.IsTrue(sink.Completed.ContainsKey("hello.txt"));
        Assert.AreSequenceEqual(content, sink.Completed["hello.txt"]);
    }

    [TestMethod]
    public async Task Receive_EmptyFile_Succeeds()
    {
        var sink = new InMemoryFileSink();
        await RunAsync([("empty.bin", [])], useCrc32: true, 1024, sink);

        Assert.IsTrue(sink.Completed.ContainsKey("empty.bin"));
        Assert.IsEmpty(sink.Completed["empty.bin"]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Receive_MultiSubpacketBinary_Succeeds(bool useCrc32)
    {
        // Larger than one subpacket + full of escape-triggering bytes.
        byte[] content = new byte[10_000];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)((i * 37 + 11) & 0xFF);
        }
        var sink = new InMemoryFileSink();
        await RunAsync([("blob.bin", content)], useCrc32, 1024, sink);

        Assert.IsTrue(sink.Completed.ContainsKey("blob.bin"));
        Assert.AreSequenceEqual(content, sink.Completed["blob.bin"]);
    }

    [TestMethod]
    public async Task Receive_BatchOfFiles_AllArriveInOrder()
    {
        (string, byte[])[] files =
        [
            ("a.txt", "first"u8.ToArray()),
            ("b.txt", "second file body"u8.ToArray()),
            ("c.bin", [0x00, 0x18, 0x11, 0x13, 0xFF, 0x7F])
        ];
        var sink = new InMemoryFileSink();
        await RunAsync(files, useCrc32: true, 4, sink);

        Assert.HasCount(3, sink.Completed);
        Assert.AreSequenceEqual("first"u8.ToArray(), sink.Completed["a.txt"]);
        Assert.AreSequenceEqual("second file body"u8.ToArray(), sink.Completed["b.txt"]);
        Assert.AreSequenceEqual(new byte[] { 0x00, 0x18, 0x11, 0x13, 0xFF, 0x7F }, sink.Completed["c.bin"]);
        Assert.AreSequenceEqual(["a.txt", "b.txt", "c.bin"], [.. sink.OfferedNames]);
    }

    [TestMethod]
    public async Task Receive_TinySubpackets_StressChunking()
    {
        byte[] content = Encoding.UTF8.GetBytes("chunk boundary stress test payload 0123456789");
        var sink = new InMemoryFileSink();
        // subpacketSize=1 maximizes subpacket count and escape/boundary churn.
        await RunAsync([("s.txt", content)], useCrc32: false, 1, sink);

        Assert.AreSequenceEqual(content, sink.Completed["s.txt"]);
    }

    [TestMethod]
    public async Task Receive_FileMetadata_ParsesNameAndSize()
    {
        byte[] data = Encoding.ASCII.GetBytes("payload");
        var info = new List<byte>();
        info.AddRange(Encoding.ASCII.GetBytes("dir/report.log"));
        info.Add(0);
        info.AddRange(Encoding.ASCII.GetBytes("7 0 0 0 0 7"));
        info.Add(0);

        Core.FileTransfer.Model.TransferFileMetadata meta = ZModemReceiver.ParseFileMetadata([.. info]);
        Assert.AreEqual("dir/report.log", meta.FileName);
        Assert.AreEqual(7L, meta.Size);
        _ = data;
    }
}
