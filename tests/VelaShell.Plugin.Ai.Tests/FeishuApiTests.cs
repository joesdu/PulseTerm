using System.Text.Json;
using VelaShell.Plugin.Ai.Bridge.Channels.Feishu;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>飞书 REST 调用里那些"字段名本身就是协议"的地方。</summary>
[TestClass]
public sealed class FeishuApiTests
{
    /// <summary>
    /// 长连接接入点那条请求的字段名必须逐字是 <c>AppID</c> / <c>AppSecret</c>。
    /// </summary>
    /// <remarks>
    /// <b>这是一条回归用例,不是防御性断言。</b>真实翻车过一次:
    /// <c>PostAsJsonAsync</c> 默认用 <see cref="JsonSerializerDefaults.Web" />(camelCase),
    /// 匿名对象写 <c>AppID</c> 发出去变成 <c>appID</c>,平台回
    /// <c>{"code":9499,"msg":"Bad Request"}</c>。
    /// <para>
    /// 阴险之处在于同一个类里换令牌那条用的是 <c>app_id</c>(本来就小写开头),camelCase
    /// 动不了它 —— 于是界面上显示的是"凭证 OK,但接入点被拒",没有人会想到是序列化。
    /// 所以这里刻意<b>按 Web 默认值序列化</b>:用 <c>JsonSerializerOptions.Default</c> 测
    /// 是测不出这个 bug 的,那正是当初漏掉它的原因。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void EndpointRequest_KeepsItsPascalCaseFieldNames()
    {
        string json = JsonSerializer.Serialize(new FeishuApi.EndpointRequest("cli_x", "s3cret"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"AppID\"");
        StringAssert.Contains(json, "\"AppSecret\"");
        Assert.IsFalse(json.Contains("\"appID\"", StringComparison.Ordinal),
            $"the camelCased name is what the platform rejects with code 9499; got {json}");
    }
}
