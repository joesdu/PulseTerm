using Avalonia.Headless;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using VelaShell.Services.Syntax;

namespace VelaShell.Tests.Services;

/// <summary>
/// 语法高亮:文件类型判定 + 自带 xshd 的可加载性。
/// <para>
/// 后者尤其必要:<see cref="SyntaxHighlightingService" /> 刻意吞掉单份定义的加载异常
/// (坏掉一份不该让编辑器打不开),代价是**正则写错会静默退化成纯文本**。
/// 这组测试就是那个静默失败的守门人。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Syntax")]
public class SyntaxHighlightingTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SyntaxHighlightingTests).Assembly);

    // ---- 扩展名判定 ----

    [TestMethod]
    [DataRow("deploy.sh", SyntaxNames.Shell)]
    [DataRow("docker-compose.yml", SyntaxNames.Yaml)]
    [DataRow("values.yaml", SyntaxNames.Yaml)]
    [DataRow("app.ini", SyntaxNames.Ini)]
    [DataRow("nginx.conf", SyntaxNames.Ini)]
    [DataRow("Cargo.toml", SyntaxNames.Ini)]
    [DataRow("syslog.log", SyntaxNames.Log)]
    [DataRow("package.json", "Json")]
    [DataRow("main.py", "Python")]
    [DataRow("pom.xml", "XML")]
    public void Detect_ByExtension(string fileName, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(fileName));

    // ---- 特殊文件名(无扩展名)----

    [TestMethod]
    [DataRow("Dockerfile", SyntaxNames.Dockerfile)]
    [DataRow("dockerfile", SyntaxNames.Dockerfile)]
    [DataRow("Makefile", SyntaxNames.Shell)]
    [DataRow(".bashrc", SyntaxNames.Shell)]
    [DataRow("sshd_config", SyntaxNames.Ini)]
    [DataRow("fstab", SyntaxNames.Ini)]
    [DataRow("known_hosts", SyntaxNames.Ini)]
    public void Detect_BySpecialFileName(string fileName, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(fileName));

    /// <summary>远端路径要能剥掉目录再判定 —— 传进来的往往是完整远端路径。</summary>
    [TestMethod]
    [DataRow("/etc/nginx/nginx.conf", SyntaxNames.Ini)]
    [DataRow("/home/deploy/scripts/backup.sh", SyntaxNames.Shell)]
    [DataRow(@"C:\temp\vela\app.yaml", SyntaxNames.Yaml)]
    public void Detect_StripsDirectories(string path, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(path));

    // ---- shebang(服务器上大量脚本没有扩展名,只有首行能说明类型)----

    [TestMethod]
    [DataRow("#!/bin/bash", SyntaxNames.Shell)]
    [DataRow("#!/bin/sh", SyntaxNames.Shell)]
    [DataRow("#!/usr/bin/env bash", SyntaxNames.Shell)]
    [DataRow("#!/usr/bin/env python3", "Python")]
    [DataRow("#!/usr/bin/python3.11", "Python")]
    [DataRow("#!/usr/bin/env -S node --experimental", "JavaScript")]
    [DataRow("#!/usr/bin/pwsh", "PowerShell")]
    public void Detect_ByShebang(string firstLine, string expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect("deploy", firstLine));

    [TestMethod]
    public void Detect_ExtensionWinsOverShebang()
    {
        // 扩展名是更强的信号;shebang 只是无扩展名时的兜底。
        Assert.AreEqual("Python", FileTypeDetector.Detect("tool.py", "#!/bin/bash"));
    }

    [TestMethod]
    [DataRow("notes", null)]
    [DataRow("data.bin", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void Detect_UnknownReturnsNull(string? fileName, string? expected) =>
        Assert.AreEqual(expected, FileTypeDetector.Detect(fileName));

    [TestMethod]
    public void Detect_MalformedShebangDoesNotThrow()
    {
        Assert.IsNull(FileTypeDetector.Detect("script", "#!"));
        Assert.IsNull(FileTypeDetector.Detect("script", "#!/usr/bin/env"));
        Assert.IsNull(FileTypeDetector.Detect("script", "#!   "));
        Assert.IsNull(FileTypeDetector.Detect("script", "not a shebang"));
    }

    // ---- 自带 xshd 必须真的能加载 ----

    /// <summary>
    /// 每一种自带类型都要能解析出定义。若某份 xshd 的正则或 XML 写错,
    /// 服务会把异常吞掉、这里就会拿到 null —— 从而把"静默退化成纯文本"变成一次红色测试。
    /// </summary>
    [TestMethod]
    [DataRow(SyntaxNames.Shell)]
    [DataRow(SyntaxNames.Yaml)]
    [DataRow(SyntaxNames.Ini)]
    [DataRow(SyntaxNames.Dockerfile)]
    [DataRow(SyntaxNames.Log)]
    public void BundledDefinitions_Load(string expectedName)
    {
        IHighlightingDefinition? definition = ResolveFor(expectedName);

        Assert.IsNotNull(definition, $"{expectedName} 的 xshd 没能加载 —— 多半是正则或 XML 写错了");
        Assert.AreEqual(expectedName, definition.Name);
        Assert.IsNotEmpty(definition.NamedHighlightingColors, "定义应当声明命名颜色,否则无法跟随主题换肤");
    }

    /// <summary>
    /// 自带定义必须真的能对文本着色,而不只是"能加载但规则一条都不命中"。
    /// <para>
    /// 走 headless 会话:<see cref="DocumentHighlighter" /> 构造时会 <c>Dispatcher.VerifyAccess()</c>,
    /// 必须在 Avalonia UI 线程上创建。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ShellDefinition_ActuallyHighlights() =>
        _session.Dispatch(() =>
        {
            IHighlightingDefinition? definition = ResolveFor(SyntaxNames.Shell);
            Assert.IsNotNull(definition);

            var document = new AvaloniaEdit.Document.TextDocument(
                "#!/bin/bash\nNAME=\"world\"\nif [ -n \"$NAME\" ]; then\n  echo \"hi $NAME\"\nfi\n");
            using var highlighter = new DocumentHighlighter(document, definition);

            int coloured = 0;
            for (int line = 1; line <= document.LineCount; line++)
            {
                coloured += highlighter.HighlightLine(line).Sections.Count;
            }
            Assert.IsGreaterThan(0, coloured, "Shell 定义一个高亮区段都没产生,规则没生效");
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>换主题要能换出不同的前景色,否则暗色下会沿用浅色配色。</summary>
    [TestMethod]
    public void Recolor_FollowsThemeVariant()
    {
        IHighlightingDefinition? dark = ResolveFor(SyntaxNames.Shell, ThemeVariant.Dark);
        Assert.IsNotNull(dark);
        HighlightingColor comment = dark.NamedHighlightingColors.First(c => c.Name == "Comment");
        Avalonia.Media.Color? darkColor = comment.Foreground?.GetColor(null);

        IHighlightingDefinition? light = ResolveFor(SyntaxNames.Shell, ThemeVariant.Light);
        Assert.IsNotNull(light);
        Avalonia.Media.Color? lightColor = light.NamedHighlightingColors
                                                .First(c => c.Name == "Comment").Foreground?.GetColor(null);

        Assert.IsNotNull(darkColor);
        Assert.IsNotNull(lightColor);
        Assert.AreNotEqual(darkColor, lightColor, "深浅主题的注释色应当不同");
    }

    /// <summary>用一个能命中该定义的文件名把它解析出来。</summary>
    private static IHighlightingDefinition? ResolveFor(string name, ThemeVariant? theme = null)
    {
        string probeFile = name switch
        {
            SyntaxNames.Shell => "probe.sh",
            SyntaxNames.Yaml => "probe.yaml",
            SyntaxNames.Ini => "probe.ini",
            SyntaxNames.Dockerfile => "Dockerfile",
            SyntaxNames.Log => "probe.log",
            _ => "probe.txt",
        };
        return SyntaxHighlightingService.Resolve(probeFile, null, theme ?? ThemeVariant.Dark);
    }
}
