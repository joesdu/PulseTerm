
namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// POSIX 路径 ↔ 「桶 + 键」的互相翻译。整个 S3 后端都架在这层映射上:
/// 它错一点,文件浏览器就会把对象放进错误的目录,或者把删除打到错误的前缀上。
/// </summary>
[TestClass]
public sealed class S3ObjectPathTests
{
    /// <summary>三种层级各自解析成什么。</summary>
    [TestMethod]
    public void Parse_MapsThePathHierarchy()
    {
        var root = S3ObjectPath.Parse("/");
        Assert.IsTrue(root.IsRoot);
        Assert.IsEmpty(root.Bucket);
        Assert.IsEmpty(root.Key);

        var bucket = S3ObjectPath.Parse("/my-bucket");
        Assert.IsFalse(bucket.IsRoot);
        Assert.IsTrue(bucket.IsBucketRoot);
        Assert.AreEqual("my-bucket", bucket.Bucket);
        Assert.IsEmpty(bucket.Key);

        var key = S3ObjectPath.Parse("/my-bucket/logs/2026/app.log");
        Assert.AreEqual("my-bucket", key.Bucket);
        Assert.AreEqual("logs/2026/app.log", key.Key);
        Assert.AreEqual("app.log", key.Name);
    }

    /// <summary>空、null、纯分隔符一律是根 —— 不能变成一个空桶名。</summary>
    [TestMethod]
    public void Parse_TreatsEmptyInputAsRoot()
    {
        Assert.IsTrue(S3ObjectPath.Parse(null).IsRoot);
        Assert.IsTrue(S3ObjectPath.Parse(string.Empty).IsRoot);
        Assert.IsTrue(S3ObjectPath.Parse("   ").IsRoot);
        Assert.IsTrue(S3ObjectPath.Parse("/").IsRoot);
        Assert.IsTrue(S3ObjectPath.Parse("///").IsRoot);
    }

    /// <summary>反斜杠按分隔符处理(Windows 拖放过来的路径),连续分隔符折叠。</summary>
    [TestMethod]
    public void Parse_NormalizesSeparators()
    {
        Assert.AreEqual(S3ObjectPath.Parse("/bucket/a/b"), S3ObjectPath.Parse("\\bucket\\a\\b"));
        Assert.AreEqual(S3ObjectPath.Parse("/bucket/a/b"), S3ObjectPath.Parse("//bucket//a//b//"));
        Assert.AreEqual(S3ObjectPath.Parse("/bucket/a"), S3ObjectPath.Parse("bucket/a"));
    }

    /// <summary>
    /// 往返一致:解析回来的路径再 ToString 必须与规范形式相同,
    /// 否则「进目录再返回上一级」会走到一个不同的路径上。
    /// </summary>
    [TestMethod]
    public void ToString_RoundTripsThroughParse()
    {
        foreach (string path in new[] { "/", "/bucket", "/bucket/key", "/bucket/a/b/c.txt" })
        {
            Assert.AreEqual(path, S3ObjectPath.Parse(path).ToString(), $"往返不一致:{path}");
        }
        // 末尾斜杠在解析时被吃掉,规范形式不带它。
        Assert.AreEqual("/bucket/dir", S3ObjectPath.Parse("/bucket/dir/").ToString());
    }

    /// <summary>
    /// 前缀形式:非空键要补一个 <c>/</c>。少了它,列举 <c>tree</c> 会连
    /// <c>treasure.txt</c> 一起匹配到 —— 删除时就是误删。
    /// </summary>
    [TestMethod]
    public void Prefix_AppendsTrailingSlashForNonEmptyKeys()
    {
        Assert.AreEqual("logs/", S3ObjectPath.Parse("/bucket/logs").Prefix);
        Assert.AreEqual("logs/", S3ObjectPath.Parse("/bucket/logs/").Prefix);
        Assert.AreEqual("a/b/", S3ObjectPath.Parse("/bucket/a/b").Prefix);
        // 桶根的前缀是空串(列举整个桶),不能是 "/"。
        Assert.IsEmpty(S3ObjectPath.Parse("/bucket").Prefix);
        Assert.IsEmpty(S3ObjectPath.Root.Prefix);
    }

    /// <summary>显示名:根是 /,桶根是桶名,其余是键的最后一段。</summary>
    [TestMethod]
    public void Name_ReturnsTheLastSegment()
    {
        Assert.AreEqual("/", S3ObjectPath.Root.Name);
        Assert.AreEqual("my-bucket", S3ObjectPath.Parse("/my-bucket").Name);
        Assert.AreEqual("file.txt", S3ObjectPath.Parse("/b/dir/file.txt").Name);
        Assert.AreEqual("dir", new S3ObjectPath("b", "a/dir/").Name);
    }

    /// <summary>父路径逐级回退,到根为止(根的父仍是根,不能越界)。</summary>
    [TestMethod]
    public void Parent_WalksUpAndStopsAtRoot()
    {
        var deep = S3ObjectPath.Parse("/bucket/a/b/c.txt");
        Assert.AreEqual("/bucket/a/b", deep.Parent().ToString());
        Assert.AreEqual("/bucket/a", deep.Parent().Parent().ToString());
        Assert.AreEqual("/bucket", deep.Parent().Parent().Parent().ToString());
        Assert.AreEqual("/", deep.Parent().Parent().Parent().Parent().ToString());
        Assert.IsTrue(S3ObjectPath.Root.Parent().IsRoot);
    }

    /// <summary>追加相对路径:根上追加的第一段成为桶名。</summary>
    [TestMethod]
    public void Append_JoinsRelativeSegments()
    {
        Assert.AreEqual("/bucket/a/b", S3ObjectPath.Parse("/bucket/a").Append("b").ToString());
        Assert.AreEqual("/bucket/a/b/c", S3ObjectPath.Parse("/bucket/a").Append("b/c").ToString());
        Assert.AreEqual("/bucket/file", S3ObjectPath.Parse("/bucket").Append("file").ToString());
        Assert.AreEqual("/bucket/key", S3ObjectPath.Root.Append("bucket/key").ToString());
        // 追加空串是空操作。
        Assert.AreEqual("/bucket/a", S3ObjectPath.Parse("/bucket/a").Append(string.Empty).ToString());
    }

    /// <summary>桶名合法性:长度、字符集、首尾、以及"不得形如 IPv4"。</summary>
    [TestMethod]
    public void IsValidBucketName_AppliesTheCommonRules()
    {
        Assert.IsTrue(S3ObjectPath.IsValidBucketName("my-bucket"));
        Assert.IsTrue(S3ObjectPath.IsValidBucketName("my.bucket.name"));
        Assert.IsTrue(S3ObjectPath.IsValidBucketName("abc"));
        Assert.IsTrue(S3ObjectPath.IsValidBucketName("a1b2c3"));

        Assert.IsFalse(S3ObjectPath.IsValidBucketName(null));
        Assert.IsFalse(S3ObjectPath.IsValidBucketName("ab"), "少于 3 个字符");
        Assert.IsFalse(S3ObjectPath.IsValidBucketName(new string('a', 64)), "超过 63 个字符");
        Assert.IsFalse(S3ObjectPath.IsValidBucketName("MyBucket"), "不允许大写");
        Assert.IsFalse(S3ObjectPath.IsValidBucketName("-bucket"), "不能以连字符开头");
        Assert.IsFalse(S3ObjectPath.IsValidBucketName("bucket-"), "不能以连字符结尾");
        Assert.IsFalse(S3ObjectPath.IsValidBucketName("my_bucket"), "不允许下划线");
        Assert.IsFalse(S3ObjectPath.IsValidBucketName("192.168.1.1"), "不得形如 IPv4 地址");
    }

    /// <summary>
    /// 含点的桶名放不进主机名:<c>*.s3.amazonaws.com</c> 这类通配证书只覆盖一级标签,
    /// 走虚拟主机式会让 TLS 校验失败。
    /// </summary>
    [TestMethod]
    public void IsVirtualHostSafe_RejectsDottedBucketNames()
    {
        Assert.IsTrue(S3ObjectPath.IsVirtualHostSafe("my-bucket"));
        Assert.IsFalse(S3ObjectPath.IsVirtualHostSafe("my.bucket"));
        Assert.IsFalse(S3ObjectPath.IsVirtualHostSafe("MyBucket"));
        Assert.IsFalse(S3ObjectPath.IsVirtualHostSafe(null));
    }

    /// <summary>端点框里粘一整条 URL 是最常见的输入,必须能剥出主机与端口。</summary>
    [TestMethod]
    public void FromRequest_AcceptsAFullUrlAsTheEndpoint()
    {
        var info = S3ConnectionInfo.FromRequest(new()
        {
            Host = "http://10.0.0.2:9000/browser",
            Port = 0,
            Username = "key",
            Password = "secret",
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["useTls"] = "false" },
        });

        Assert.AreEqual("10.0.0.2", info.Endpoint);
        Assert.AreEqual(9000, info.Port);
        Assert.AreEqual("key", info.AccessKeyId);
        Assert.AreEqual("secret", info.SecretAccessKey);
        Assert.AreEqual("http://10.0.0.2:9000", info.BaseUri.ToString().TrimEnd('/'));
    }

    /// <summary>设置全缺时(「最近连接」重建的临时配置)要能兜底成默认值。</summary>
    [TestMethod]
    public void FromRequest_FallsBackToDefaultsWhenSettingsAreMissing()
    {
        var info = S3ConnectionInfo.FromRequest(new()
        {
            Host = "s3.amazonaws.com",
            Port = 443,
        });

        Assert.AreEqual("s3.amazonaws.com", info.Endpoint);
        Assert.AreEqual(S3Settings.DefaultRegion, info.Settings.EffectiveRegion);
        Assert.IsTrue(info.Settings.UseTls);
        Assert.IsTrue(info.IsAnonymous, "没有 Access Key 即匿名访问(公开只读桶)。");
    }

    /// <summary>分片大小与并发数要被夹到协议允许的区间内。</summary>
    [TestMethod]
    public void S3Settings_ClampsPartSizeAndConcurrency()
    {
        Assert.AreEqual(S3Settings.MinPartSizeBytes, new S3Settings { PartSizeBytes = 1 }.EffectivePartSize);
        Assert.AreEqual(S3Settings.MaxPartSizeBytes, new S3Settings { PartSizeBytes = long.MaxValue }.EffectivePartSize);
        Assert.AreEqual(S3Settings.DefaultPartSizeBytes, new S3Settings { PartSizeBytes = 0 }.EffectivePartSize);
        Assert.AreEqual(1, new S3Settings { MaxConcurrentParts = 0 }.EffectiveConcurrency);
        Assert.AreEqual(16, new S3Settings { MaxConcurrentParts = 999 }.EffectiveConcurrency);
    }
}
