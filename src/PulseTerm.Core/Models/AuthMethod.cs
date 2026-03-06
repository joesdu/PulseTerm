namespace PulseTerm.Core.Models;

/// <summary>
/// SSH authentication method types
/// </summary>
public enum AuthMethod
{
    /// <summary>
    /// Password-based authentication
    /// </summary>
    Password,

    /// <summary>
    /// Private key authentication (RSA, ED25519, ECDSA)
    /// </summary>
    PrivateKey
}
