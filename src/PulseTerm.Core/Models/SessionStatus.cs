namespace PulseTerm.Core.Models;

/// <summary>
/// SSH session connection status
/// </summary>
public enum SessionStatus
{
    /// <summary>
    /// Session is actively connected
    /// </summary>
    Connected,

    /// <summary>
    /// Session is in the process of connecting
    /// </summary>
    Connecting,

    /// <summary>
    /// Session is disconnected
    /// </summary>
    Disconnected,

    /// <summary>
    /// Session encountered an error
    /// </summary>
    Error
}
