namespace PulseTerm.Core.Models;

/// <summary>
/// Specifies the type of SSH port forwarding tunnel.
/// </summary>
public enum TunnelType
{
    /// <summary>
    /// Local forward: localhost:localPort → remoteHost:remotePort (via SSH server)
    /// </summary>
    LocalForward,

    /// <summary>
    /// Remote forward: sshServer:remotePort → localhost:localPort
    /// </summary>
    RemoteForward
}
