namespace Boo.Windows;

/// <summary>
/// A user-visible capture limitation. Callers must surface the message and
/// stop the capture session; this is intentionally not a silent fallback.
/// </summary>
public sealed class CaptureLimitationException : InvalidOperationException
{
    public CaptureLimitationException(string message)
        : base(message)
    {
    }

    public CaptureLimitationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
