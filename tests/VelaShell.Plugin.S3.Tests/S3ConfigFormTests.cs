using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Plugin.S3.Ui;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// 桶配置的「JSON ↔ 结构化表单」映射。
/// <para>
/// 这里最要紧的一条是 <see cref="Apply_PreservesFieldsTheFormDoesNotKnowAbout" />:
/// 表单只覆盖它认识的路径,其余内容必须原样保留。做不到这一点,用户在表单里改一个开关
/// 就会**静默清空**服务端上那些我们没渲染的字段 —— 那是会真的改坏生产配置的。
/// </para>
/// </summary>
[TestClass]
public sealed class S3ConfigFormTests
{
    /// <summary>表单字段的标签要经文案表取词;单测用英文表即可。</summary>
    private static readonly Loc Loc = new("en");

    /// <summary>每一项桶配置都必须有描述,否则界面上会缺一块。</summary>
    [TestMethod]
    public void EveryConfigKind_HasADescriptor()
    {
        foreach (S3ConfigKind kind in Enum.GetValues<S3ConfigKind>())
        {
            var descriptor = S3ConfigDescriptor.For(kind);
            Assert.AreEqual(kind, descriptor.Kind);
            Assert.IsNotEmpty(descriptor.ResourceKey, $"{kind} 缺少标题资源键。");
        }
        // 描述表不能有重复项(重复会让左侧导航出现两行同名条目)。
        Assert.HasCount(
            Enum.GetValues<S3ConfigKind>().Length,
            S3ConfigDescriptor.All.Select(d => d.Kind).Distinct());
    }

    /// <summary>表单类配置都能构造出至少一个字段;文档类配置不构造字段。</summary>
    [TestMethod]
    public void Build_ProducesFieldsExactlyForFormKinds()
    {
        foreach (S3ConfigDescriptor descriptor in S3ConfigDescriptor.All)
        {
            List<S3FormFieldViewModel> fields = S3ConfigForm.Build(descriptor.Kind, string.Empty, Loc);
            if (descriptor.Editor == S3ConfigEditor.Form)
            {
                Assert.IsNotEmpty(fields, $"{descriptor.Kind} 声明为表单却没有字段。");
            }
            else
            {
                Assert.IsEmpty(fields, $"{descriptor.Kind} 是文档类配置,不该有表单字段。");
            }
        }
    }

    /// <summary>读取:把服务端返回的 JSON 填进字段。</summary>
    [TestMethod]
    public void Build_FillsValuesFromJson()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(
            S3ConfigKind.Versioning,
            """{"Status":"Enabled","EnableMfaDelete":true}""", Loc);

        Assert.AreEqual("Enabled", fields.Single(f => f.Path == "Status").Text);
        Assert.IsTrue(fields.Single(f => f.Path == "EnableMfaDelete").Toggle);
    }

    /// <summary>写回:字段的值进入 JSON。</summary>
    [TestMethod]
    public void Apply_WritesValuesBackToJson()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(S3ConfigKind.Versioning, string.Empty, Loc);
        fields.Single(f => f.Path == "Status").Text = "Suspended";
        fields.Single(f => f.Path == "EnableMfaDelete").Toggle = true;

        JsonNode root = JsonNode.Parse(S3ConfigForm.Apply(S3ConfigKind.Versioning, string.Empty, fields))!;

        Assert.AreEqual("Suspended", root["Status"]!.GetValue<string>());
        Assert.IsTrue(root["EnableMfaDelete"]!.GetValue<bool>());
    }

    /// <summary>
    /// **表单不得吃掉它不认识的字段。** 服务端可能返回我们没渲染的键(新版本加的、
    /// 或某个 S3 兼容实现特有的);保存时必须原样带回去。
    /// </summary>
    [TestMethod]
    public void Apply_PreservesFieldsTheFormDoesNotKnowAbout()
    {
        const string original = """
                                {
                                  "Status": "Enabled",
                                  "VendorSpecificFlag": "keep-me",
                                  "Nested": { "Deep": [1, 2, 3] }
                                }
                                """;
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(S3ConfigKind.Versioning, original, Loc);
        fields.Single(f => f.Path == "Status").Text = "Suspended";

        JsonNode root = JsonNode.Parse(S3ConfigForm.Apply(S3ConfigKind.Versioning, original, fields))!;

        Assert.AreEqual("Suspended", root["Status"]!.GetValue<string>());
        Assert.AreEqual("keep-me", root["VendorSpecificFlag"]!.GetValue<string>(), "表单外的字段被清空了。");
        Assert.AreEqual(3, root["Nested"]!["Deep"]!.AsArray().Count, "嵌套结构被清空了。");
    }

    /// <summary>嵌套路径与数组下标要能被自动补齐(第一次配置时文档是空的)。</summary>
    [TestMethod]
    public void Apply_CreatesNestedContainersOnDemand()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(S3ConfigKind.Encryption, string.Empty, Loc);
        fields.Single(f => f.Path.EndsWith("ServerSideEncryptionAlgorithm", StringComparison.Ordinal)).Text = "AES256";
        fields.Single(f => f.Path.EndsWith("BucketKeyEnabled", StringComparison.Ordinal)).Toggle = true;

        JsonNode root = JsonNode.Parse(S3ConfigForm.Apply(S3ConfigKind.Encryption, string.Empty, fields))!;
        JsonNode rule = root["ServerSideEncryptionRules"]!.AsArray()[0]!;

        Assert.AreEqual("AES256", rule["ServerSideEncryptionByDefault"]!["ServerSideEncryptionAlgorithm"]!.GetValue<string>());
        Assert.IsTrue(rule["BucketKeyEnabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// 留空 = 不设置,写成 <c>null</c> 而不是空串:空串在不少配置里是**非法值**,
    /// 会被服务端直接拒掉。
    /// </summary>
    [TestMethod]
    public void Apply_EmptyTextBecomesNullRatherThanEmptyString()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(
            S3ConfigKind.Encryption,
            """{"ServerSideEncryptionRules":[{"ServerSideEncryptionByDefault":{"ServerSideEncryptionKeyManagementServiceKeyId":"old-key"}}]}""", Loc);
        fields.Single(f => f.Path.EndsWith("KeyManagementServiceKeyId", StringComparison.Ordinal)).Text = "  ";

        JsonNode root = JsonNode.Parse(S3ConfigForm.Apply(S3ConfigKind.Encryption, string.Empty, fields))!;
        JsonNode? keyId = root["ServerSideEncryptionRules"]!.AsArray()[0]!["ServerSideEncryptionByDefault"]!["ServerSideEncryptionKeyManagementServiceKeyId"];

        Assert.IsNull(keyId);
    }

    /// <summary>数字字段要写成 JSON 数字而不是字符串(服务端 schema 对此敏感)。</summary>
    [TestMethod]
    public void Apply_NumberFieldsAreWrittenAsJsonNumbers()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(S3ConfigKind.ObjectLock, string.Empty, Loc);
        fields.Single(f => f.Path.EndsWith("Days", StringComparison.Ordinal)).Text = "30";

        JsonNode root = JsonNode.Parse(S3ConfigForm.Apply(S3ConfigKind.ObjectLock, string.Empty, fields))!;
        JsonNode days = root["Rule"]!["DefaultRetention"]!["Days"]!;

        Assert.AreEqual(JsonValueKind.Number, days.GetValueKind());
        Assert.AreEqual(30, days.GetValue<int>());
    }

    /// <summary>标签列表往返:读进来是几行,写回去还是几行,空键的行被丢掉。</summary>
    [TestMethod]
    public void TagList_RoundTripsAndDropsBlankRows()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(
            S3ConfigKind.Tagging,
            """{"TagSet":[{"Key":"env","Value":"prod"},{"Key":"team","Value":"infra"}]}""", Loc);
        S3FormFieldViewModel tags = fields.Single();

        Assert.HasCount(2, tags.Tags);
        Assert.AreEqual("env", tags.Tags[0].Key);
        Assert.AreEqual("prod", tags.Tags[0].Value);

        // 一行只有值没有键 —— 那不是标签,保存时应被丢掉。
        tags.Tags.Add(new() { Key = "  ", Value = "orphan" });
        tags.Tags.Add(new() { Key = "owner", Value = "ops" });

        JsonNode root = JsonNode.Parse(S3ConfigForm.Apply(S3ConfigKind.Tagging, string.Empty, fields))!;
        JsonArray written = root["TagSet"]!.AsArray();

        Assert.HasCount(3, written);
        Assert.AreSequenceEqual(
            ["env", "team", "owner"], [.. written.Select(t => t!["Key"]!.GetValue<string>())], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    /// <summary>损坏的 JSON 不能把表单打崩,退化成空表单即可。</summary>
    [TestMethod]
    public void Build_ToleratesMalformedJson()
    {
        List<S3FormFieldViewModel> fields = S3ConfigForm.Build(S3ConfigKind.Versioning, "{not json", Loc);

        Assert.IsNotEmpty(fields);
        Assert.IsEmpty(fields.Single(f => f.Path == "Status").Text);
    }

    /// <summary>只有 Form 类配置才声明有表单;这是界面分支的判据,要与描述表一致。</summary>
    [TestMethod]
    public void HasForm_MatchesTheDescriptorTable()
    {
        foreach (S3ConfigDescriptor descriptor in S3ConfigDescriptor.All)
        {
            Assert.AreEqual(descriptor.Editor == S3ConfigEditor.Form, S3ConfigForm.HasForm(descriptor.Kind), descriptor.Kind.ToString());
        }
    }
}
