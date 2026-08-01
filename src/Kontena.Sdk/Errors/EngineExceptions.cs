namespace Kontena.Sdk.Errors;

/// <summary>Base type for all engine-related failures Kontena raises.</summary>
public class EngineException : Exception
{
    public EngineException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The engine could not be reached (socket down, engine stopped, still starting).
/// Feeds the engine-down UI state.
/// </summary>
public sealed class EngineUnreachableException : EngineException
{
    public EngineUnreachableException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>Access to the engine was denied (permissions / authorization).</summary>
public sealed class EnginePermissionException : EngineException
{
    public EnginePermissionException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>A referenced resource (container, image, volume, network) does not exist.</summary>
public sealed class ResourceNotFoundException : EngineException
{
    public ResourceNotFoundException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Why ssh refused a host before authentication ever started (KON-260).
/// </summary>
public enum SshHostKeyProblem
{
    /// <summary>Not a host key failure — whatever went wrong, it was something else.</summary>
    None = 0,

    /// <summary>
    /// The host is not in <c>known_hosts</c>. With <c>BatchMode=yes</c> ssh cannot ask, so it refuses;
    /// the fingerprint has to be confirmed before there is anything to connect to.
    /// </summary>
    Unknown,

    /// <summary>
    /// The host answered with a different key than the one already trusted. Never something to fix on
    /// the user's behalf: it is either a rebuilt machine or someone standing in the middle, and only
    /// the person who knows which can say.
    /// </summary>
    Changed,
}

/// <summary>
/// ssh would not talk to the host because of its key. Carries which of the two cases it is, so the UI
/// can offer a fingerprint to confirm for one and refuse to offer anything for the other.
/// </summary>
public sealed class SshHostKeyException : EngineException
{
    public SshHostKeyException(SshHostKeyProblem problem, string message, string complaint)
        : base(message)
    {
        Problem = problem;
        Complaint = complaint;
    }

    public SshHostKeyProblem Problem { get; }

    /// <summary>ssh's own words, kept whole — it names the file and line a changed key sits on.</summary>
    public string Complaint { get; }
}

/// <summary>The active engine does not support the requested capability.</summary>
public sealed class CapabilityNotSupportedException : EngineException
{
    public CapabilityNotSupportedException(string capability)
        : base($"The active engine does not support: {capability}.") { }
}
