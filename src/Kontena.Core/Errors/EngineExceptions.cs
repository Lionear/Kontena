namespace Kontena.Core.Errors;

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

/// <summary>The active engine does not support the requested capability.</summary>
public sealed class CapabilityNotSupportedException : EngineException
{
    public CapabilityNotSupportedException(string capability)
        : base($"The active engine does not support: {capability}.") { }
}
