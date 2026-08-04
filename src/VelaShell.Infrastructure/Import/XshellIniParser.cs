namespace VelaShell.Infrastructure.Import;

/// <summary>从 Xshell <c>.xsh</c>(UTF-16 INI)中解析出的、导入所需的原始字段。</summary>
internal sealed record XshellSessionFile(
    string? Version,
    string? Host,
    int Port,
    string? Protocol,
    string? UserName,
    string? EncryptedPassword,
    string? UserKey);

/// <summary>Xshell 会话文件的分节 INI 解析器(只取导入需要的少量字段)。</summary>
internal static class XshellIniParser
{
    /// <summary>解析已按行读入的会话文件内容。</summary>
    public static XshellSessionFile Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string section = string.Empty;
        string? version = null, host = null, protocol = null, userName = null, password = null, userKey = null;
        int port = 0;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();

            switch (section)
            {
                case "SessionInfo" when key.Equals("Version", StringComparison.OrdinalIgnoreCase):
                    version = value;
                    break;
                case "CONNECTION" when key.Equals("Host", StringComparison.OrdinalIgnoreCase):
                    host = value;
                    break;
                case "CONNECTION" when key.Equals("Port", StringComparison.OrdinalIgnoreCase):
                    _ = int.TryParse(value, out port);
                    break;
                case "CONNECTION" when key.Equals("Protocol", StringComparison.OrdinalIgnoreCase):
                    protocol = value;
                    break;
                case "CONNECTION:AUTHENTICATION" when key.Equals("UserName", StringComparison.OrdinalIgnoreCase):
                    userName = value;
                    break;
                case "CONNECTION:AUTHENTICATION" when key.Equals("Password", StringComparison.OrdinalIgnoreCase):
                    password = value;
                    break;
                case "CONNECTION:AUTHENTICATION" when key.Equals("UserKey", StringComparison.OrdinalIgnoreCase):
                    userKey = value;
                    break;
            }
        }
        return new XshellSessionFile(version, host, port, protocol, userName, password, userKey);
    }
}
