using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Plugin.Ai.Auth;

/// <summary>
/// 一次 PKCE(RFC 7636)握手用的一组随机串:verifier 留在本机,challenge 随授权请求发出去,
/// 换 token 时再把 verifier 亮出来证明"当初发起授权的就是我"。
/// </summary>
/// <remarks>
/// 桌面程序装在用户机器上,任何"客户端密钥"都是公开的,所以授权码被中途截走时,
/// 拦住攻击者的只有这一层:challenge 是 verifier 的 SHA-256,反推不出来。
/// <paramref name="State" /> 是另一回事 —— 它防的是"别人塞一个自己的 code 进我的回调",
/// 与 PKCE 正交,两个都要。
/// </remarks>
/// <param name="Verifier">code_verifier:43–128 字符的高熵随机串,<b>不出本机</b>。</param>
/// <param name="Challenge">code_challenge:verifier 的 SHA-256,base64url 无填充。</param>
/// <param name="State">防 CSRF 的一次性随机串,回调里必须原样带回来。</param>
public readonly record struct PkceCodes(string Verifier, string Challenge, string State)
{
    /// <summary>challenge 的生成方式;固定 S256(<c>plain</c> 等于没做)。</summary>
    public const string Method = "S256";

    /// <summary>现造一组。</summary>
    public static PkceCodes Create()
    {
        string verifier = RandomUrlSafe(64);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkceCodes(verifier, challenge, RandomUrlSafe(16));
    }

    /// <summary><paramref name="bytes" /> 字节的随机数据,编成 URL 安全的 base64(无填充)。</summary>
    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>base64url:去掉 <c>=</c> 填充,<c>+/</c> 换成 <c>-_</c>(RFC 4648 §5)。</summary>
    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
