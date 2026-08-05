namespace VelaShell.Services.Syntax;

/// <summary>
/// 按文件名与首行内容判定语法类型,返回语法定义名(见 <see cref="SyntaxHighlightingService" />)。
/// <para>
/// 三级判定,依次尝试:扩展名 → 特殊文件名 → shebang。
/// 第三级对远端编辑尤其重要:服务器上大量可执行脚本是**没有扩展名**的
/// (<c>/usr/local/bin/deploy</c>、<c>/etc/cron.daily/logrotate</c>),
/// 只有首行的 <c>#!</c> 能说明它是什么。
/// </para>
/// </summary>
public static class FileTypeDetector
{
    /// <summary>扩展名(小写,含点)→ 语法定义名。</summary>
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // 运维日常:AvaloniaEdit 未内置,由本项目的 xshd 提供。
        [".sh"] = SyntaxNames.Shell,
        [".bash"] = SyntaxNames.Shell,
        [".zsh"] = SyntaxNames.Shell,
        [".ksh"] = SyntaxNames.Shell,
        [".profile"] = SyntaxNames.Shell,
        [".bashrc"] = SyntaxNames.Shell,
        [".yaml"] = SyntaxNames.Yaml,
        [".yml"] = SyntaxNames.Yaml,
        [".ini"] = SyntaxNames.Ini,
        [".conf"] = SyntaxNames.Ini,
        [".cfg"] = SyntaxNames.Ini,
        [".toml"] = SyntaxNames.Ini,
        [".properties"] = SyntaxNames.Ini,
        [".env"] = SyntaxNames.Ini,
        [".service"] = SyntaxNames.Ini,
        [".log"] = SyntaxNames.Log,

        // 以下由 AvaloniaEdit 内置定义提供(名称须与其 Name 一致)。
        [".json"] = "Json",
        [".jsonc"] = "Json",
        [".xml"] = "XML",
        [".xsd"] = "XML",
        [".xsl"] = "XML",
        [".config"] = "XML",
        [".csproj"] = "XML",
        [".props"] = "XML",
        [".targets"] = "XML",
        [".plist"] = "XML",
        [".svg"] = "XML",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".js"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".ts"] = "JavaScript",
        [".py"] = "Python",
        [".pyw"] = "Python",
        [".ps1"] = "PowerShell",
        [".psm1"] = "PowerShell",
        [".sql"] = "TSQL",
        [".md"] = "MarkDown",
        [".markdown"] = "MarkDown",
        [".cs"] = "C#",
        [".c"] = "C++",
        [".h"] = "C++",
        [".cpp"] = "C++",
        [".hpp"] = "C++",
        [".cc"] = "C++",
        [".java"] = "Java",
        [".php"] = "PHP",
        [".patch"] = "Patch",
        [".diff"] = "Patch",
        [".tex"] = "TeX",
        [".vb"] = "VB",
    };

    /// <summary>无扩展名但含义明确的文件名(小写)→ 语法定义名。</summary>
    private static readonly Dictionary<string, string> ByFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dockerfile"] = SyntaxNames.Dockerfile,
        ["containerfile"] = SyntaxNames.Dockerfile,
        ["makefile"] = SyntaxNames.Shell,
        ["gnumakefile"] = SyntaxNames.Shell,
        [".bashrc"] = SyntaxNames.Shell,
        [".bash_profile"] = SyntaxNames.Shell,
        [".bash_aliases"] = SyntaxNames.Shell,
        [".zshrc"] = SyntaxNames.Shell,
        [".profile"] = SyntaxNames.Shell,
        ["crontab"] = SyntaxNames.Shell,

        // /etc 下的常见配置,绝大多数是 key=value 或 key value 风格。
        ["sshd_config"] = SyntaxNames.Ini,
        ["ssh_config"] = SyntaxNames.Ini,
        ["nginx.conf"] = SyntaxNames.Ini,
        ["my.cnf"] = SyntaxNames.Ini,
        ["redis.conf"] = SyntaxNames.Ini,
        ["fstab"] = SyntaxNames.Ini,
        ["hosts"] = SyntaxNames.Ini,
        ["resolv.conf"] = SyntaxNames.Ini,
        ["known_hosts"] = SyntaxNames.Ini,
        ["authorized_keys"] = SyntaxNames.Ini,
        [".gitconfig"] = SyntaxNames.Ini,
        [".npmrc"] = SyntaxNames.Ini,
        [".editorconfig"] = SyntaxNames.Ini,

        ["docker-compose.yml"] = SyntaxNames.Yaml,
        ["docker-compose.yaml"] = SyntaxNames.Yaml,
    };

    /// <summary>shebang 解释器名(小写)→ 语法定义名。</summary>
    private static readonly Dictionary<string, string> ByInterpreter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sh"] = SyntaxNames.Shell,
        ["bash"] = SyntaxNames.Shell,
        ["zsh"] = SyntaxNames.Shell,
        ["ksh"] = SyntaxNames.Shell,
        ["dash"] = SyntaxNames.Shell,
        ["ash"] = SyntaxNames.Shell,
        ["python"] = "Python",
        ["python2"] = "Python",
        ["python3"] = "Python",
        ["pwsh"] = "PowerShell",
        ["powershell"] = "PowerShell",
        ["node"] = "JavaScript",
        ["php"] = "PHP",
    };

    /// <summary>
    /// 判定语法定义名;无法判定时返回 <c>null</c>(调用方应表现为纯文本)。
    /// </summary>
    /// <param name="fileName">文件名(可带路径,只取最后一段)。</param>
    /// <param name="firstLine">文件首行,用于 shebang 判定;为空则跳过该级。</param>
    public static string? Detect(string? fileName, string? firstLine = null)
    {
        string name = GetName(fileName);

        // 完整文件名优先于扩展名:docker-compose.yml 既命中 ".yml" 也命中全名,
        // 而 nginx.conf 这类"扩展名恰好也认识"的情况两者结论一致,谁先都行;
        // 真正需要全名优先的是将来可能出现的更精确规则。
        if (name.Length > 0 && ByFileName.TryGetValue(name, out string? byName))
        {
            return byName;
        }
        string extension = Path.GetExtension(name);
        if (extension.Length > 0 && ByExtension.TryGetValue(extension, out string? byExtension))
        {
            return byExtension;
        }
        return DetectByShebang(firstLine);
    }

    /// <summary>
    /// 解析 shebang。同时处理直接形式 <c>#!/bin/bash</c> 与 env 形式
    /// <c>#!/usr/bin/env python3</c>,后者真正的解释器在第二个词上。
    /// </summary>
    private static string? DetectByShebang(string? firstLine)
    {
        if (string.IsNullOrWhiteSpace(firstLine) || !firstLine.StartsWith("#!", StringComparison.Ordinal))
        {
            return null;
        }
        string[] parts = firstLine[2..]
                         .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }
        string interpreter = LastSegment(parts[0]);

        // env 形式:真正的解释器是它后面那个词(还要跳过 -S / -i 之类的选项)。
        if (interpreter.Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            string? next = parts.Skip(1).FirstOrDefault(p => !p.StartsWith('-') && !p.Contains('='));
            if (next is null)
            {
                return null;
            }
            interpreter = LastSegment(next);
        }
        return ByInterpreter.GetValueOrDefault(StripVersionSuffix(interpreter));
    }

    /// <summary>取路径最后一段(shebang 里写的是绝对路径)。</summary>
    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>python3.11 → python3;表里已收 python/python2/python3。</summary>
    private static string StripVersionSuffix(string interpreter)
    {
        int dot = interpreter.IndexOf('.');
        return dot > 0 ? interpreter[..dot] : interpreter;
    }

    private static string GetName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        // 远端是 unix 路径,本地可能是 windows 路径 —— 两种分隔符都要认。
        int slash = fileName.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? fileName[(slash + 1)..] : fileName;
    }
}

/// <summary>本项目自带的语法定义名(AvaloniaEdit 未内置的那些)。</summary>
public static class SyntaxNames
{
    /// <summary>Shell / Bash 脚本。</summary>
    public const string Shell = "Shell";

    /// <summary>YAML。</summary>
    public const string Yaml = "YAML";

    /// <summary>INI / conf / properties 风格的配置。</summary>
    public const string Ini = "Ini";

    /// <summary>Dockerfile。</summary>
    public const string Dockerfile = "Dockerfile";

    /// <summary>日志文件(按级别着色)。</summary>
    public const string Log = "Log";
}
