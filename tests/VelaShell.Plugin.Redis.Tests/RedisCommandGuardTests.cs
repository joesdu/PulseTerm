namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 命令闸门。守住的是护栏的**判定依据**:分级来自服务器的 <c>COMMAND INFO</c> flags,
/// 不是插件里手写的黑名单 —— 手写名单必然漏掉模块命令、新版本命令与分叉自带的命令。
/// </summary>
[TestClass]
public sealed class RedisCommandGuardTests
{
    private static RedisCommandGuard WithServerFlags(params (string Command, string[] Flags)[] entries)
    {
        var guard = new RedisCommandGuard();
        guard.LoadServerMetadata(entries.ToDictionary(
            entry => entry.Command,
            entry => (IReadOnlyList<string>)entry.Flags,
            StringComparer.OrdinalIgnoreCase));
        return guard;
    }

    [TestMethod]
    public void ServerFlags_DecideWriteVersusRead()
    {
        RedisCommandGuard guard = WithServerFlags(
            ("get", ["readonly", "fast"]),
            ("set", ["write", "denyoom"]));

        Assert.AreEqual(RedisCommandRisk.Read, guard.Classify("GET"));
        Assert.AreEqual(RedisCommandRisk.Write, guard.Classify("SET"));
        Assert.IsTrue(guard.MetadataFromServer);
    }

    [TestMethod]
    public void AdminFlag_MeansDangerous()
    {
        RedisCommandGuard guard = WithServerFlags(("bgsave", ["admin", "noscript"]));

        Assert.AreEqual(RedisCommandRisk.Dangerous, guard.Classify("BGSAVE"));
    }

    [TestMethod]
    public void ModuleCommandFromServer_IsClassifiedByItsFlags()
    {
        // 这是"依据服务器"的全部意义:JSON.SET 不在任何人手写的名单里,
        // 但服务器说它是 write,闸门就该拦住它。
        RedisCommandGuard guard = WithServerFlags(("json.set", ["write", "denyoom"]));
        guard.ReadOnly = true;

        RedisCommandVerdict verdict = guard.Evaluate("JSON.SET");

        Assert.AreEqual(RedisCommandRisk.Write, verdict.Risk);
        Assert.IsFalse(verdict.Allowed);
    }

    [TestMethod]
    public void FlushCommands_AreDestructiveEvenThoughFlagsSayJustWrite()
    {
        // FLUSHDB 在 flags 上与普通写命令没有区别,但误伤成本完全不是一回事 ——
        // 这是刻意的按名字例外。
        RedisCommandGuard guard = WithServerFlags(("flushdb", ["write"]), ("flushall", ["write"]));

        Assert.AreEqual(RedisCommandRisk.Destructive, guard.Classify("FLUSHDB"));
        Assert.AreEqual(RedisCommandRisk.Destructive, guard.Classify("FLUSHALL"));
        Assert.AreEqual(RedisCommandRisk.Destructive, guard.Classify("SHUTDOWN"));
    }

    [TestMethod]
    public void UnknownCommand_IsTreatedAsAWrite()
    {
        // **未知不等于安全**:打错的命令与新出的模块命令都走这条路。
        RedisCommandGuard guard = WithServerFlags(("get", ["readonly"]));

        Assert.AreEqual(RedisCommandRisk.Write, guard.Classify("SOMETHING.NEW"));
    }

    [TestMethod]
    public void WithoutServerMetadata_FallbackTableStillCatchesCommonWrites()
    {
        var guard = new RedisCommandGuard();

        Assert.IsFalse(guard.MetadataFromServer);
        Assert.AreEqual(RedisCommandRisk.Write, guard.Classify("HSET"));
        Assert.AreEqual(RedisCommandRisk.Write, guard.Classify("ZADD"));
        Assert.AreEqual(RedisCommandRisk.Dangerous, guard.Classify("MONITOR"));
        Assert.AreEqual(RedisCommandRisk.Destructive, guard.Classify("FLUSHALL"));
    }

    [TestMethod]
    public void ReadOnly_BlocksWritesButNotReads()
    {
        RedisCommandGuard guard = WithServerFlags(("get", ["readonly"]), ("set", ["write"]));
        guard.ReadOnly = true;

        Assert.IsTrue(guard.Evaluate("GET").Allowed);
        RedisCommandVerdict blocked = guard.Evaluate("SET");
        Assert.IsFalse(blocked.Allowed);
        Assert.AreEqual("readonly", blocked.Reason);
    }

    [TestMethod]
    public void DangerousCommands_NeedConfirmationButNotTyping()
    {
        RedisCommandGuard guard = WithServerFlags(("config", ["admin"]));

        RedisCommandVerdict verdict = guard.Evaluate("CONFIG");

        Assert.IsTrue(verdict.Allowed);
        Assert.IsTrue(verdict.NeedsConfirmation);
        Assert.IsFalse(verdict.NeedsTypedConfirmation);
    }

    [TestMethod]
    public void DestructiveCommands_RequireTypedConfirmation()
    {
        var guard = new RedisCommandGuard();

        RedisCommandVerdict verdict = guard.Evaluate("FLUSHDB");

        Assert.IsTrue(verdict.Allowed);
        Assert.IsTrue(verdict.NeedsTypedConfirmation);
    }

    [TestMethod]
    public void ProductionLock_ForbidsDestructiveEntirely()
    {
        // 生产标记下不是"多问一句",是**整条禁用** —— 要解锁得去连接设置里改。
        var guard = new RedisCommandGuard { LockDestructive = true };

        RedisCommandVerdict verdict = guard.Evaluate("FLUSHALL");

        Assert.IsFalse(verdict.Allowed);
        Assert.AreEqual("production-locked", verdict.Reason);
    }

    [TestMethod]
    public void ReadOnly_TakesPrecedenceOverProductionLock()
    {
        // 两个开关都开时,给出的原因应当是"只读"—— 那是用户一键就能解除的那一个。
        var guard = new RedisCommandGuard { ReadOnly = true, LockDestructive = true };

        Assert.AreEqual("readonly", guard.Evaluate("FLUSHALL").Reason);
    }

    [TestMethod]
    public void Normalize_TakesTheFirstWordAndUppercasesIt()
    {
        Assert.AreEqual("HGETALL", RedisCommandGuard.Normalize("hgetall user:1"));
        Assert.AreEqual("SET", RedisCommandGuard.Normalize("  set a b  "));
        Assert.AreEqual("GET", RedisCommandGuard.Normalize("\"get\""));
        Assert.AreEqual(string.Empty, RedisCommandGuard.Normalize("   "));
    }

    [TestMethod]
    public void SubcommandsDoNotChangeTheVerdict()
    {
        // CONFIG GET 与 CONFIG SET 都按 CONFIG 定档:读配置也值得知道自己在动管理面。
        RedisCommandGuard guard = WithServerFlags(("config", ["admin"]));

        Assert.AreEqual(RedisCommandRisk.Dangerous, guard.Classify("config get maxmemory"));
    }
}
