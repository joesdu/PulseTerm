using ReactiveUI;

namespace PulseTerm.Core.Models;

/// <summary>
/// Represents an active SSH session
/// </summary>
public class SshSession : ReactiveObject
{
    private SessionStatus _status;
    private string? _errorMessage;

    /// <summary>
    /// Gets the unique session identifier
    /// </summary>
    public Guid SessionId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets the connection information
    /// </summary>
    public required ConnectionInfo ConnectionInfo { get; init; }

    /// <summary>
    /// Gets or sets the session status
    /// </summary>
    public SessionStatus Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>
    /// Gets or sets the error message (if Status is Error)
    /// </summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>
    /// Gets the timestamp when the session was created
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the timestamp when the session was last connected
    /// </summary>
    public DateTime? ConnectedAt { get; set; }
}
