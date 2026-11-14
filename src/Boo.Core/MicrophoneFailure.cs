namespace Boo.Core;

public enum MicrophoneFailureKind
{
    Unknown,
    NoDevice,
    DeviceMissing,
    AccessDenied,
    BlockedOrInUse,
    UnsupportedFormat,
    Disconnected
}

/// <summary>
/// Pure mapping from WinMM (waveIn) results and access-denied signals to a microphone failure the user can fix.
/// Windows' microphone privacy switch commonly surfaces through waveIn as MMSYSERR_ALLOCATED, which is also
/// what a device held exclusively by another app returns, so that code gets a message covering both.
/// </summary>
public static class MicrophoneFailures
{
    public const int MmUnspecifiedError = 1;
    public const int MmBadDeviceId = 2;
    public const int MmAllocated = 4;
    public const int MmNoDriver = 6;
    public const int MmNotSupported = 8;
    public const int MmNodeNotFound = 20;
    public const int WaveBadFormat = 32;

    private const int ErrorAccessDenied = 5;
    private const int EAccessDenied = unchecked((int)0x80070005);

    private const string PrivacyPath = "Settings > Privacy & security > Microphone > Let desktop apps access your microphone";

    public static MicrophoneFailureKind ClassifyStart(int deviceCount, int requestedDevice, int? mmResult, bool accessDenied)
    {
        if (accessDenied)
            return MicrophoneFailureKind.AccessDenied;
        if (deviceCount <= 0)
            return MicrophoneFailureKind.NoDevice;
        if (requestedDevice >= deviceCount)
            return MicrophoneFailureKind.DeviceMissing;
        return mmResult switch
        {
            MmAllocated => MicrophoneFailureKind.BlockedOrInUse,
            MmBadDeviceId or MmNoDriver or MmNodeNotFound => MicrophoneFailureKind.DeviceMissing,
            WaveBadFormat or MmNotSupported => MicrophoneFailureKind.UnsupportedFormat,
            _ => MicrophoneFailureKind.Unknown
        };
    }

    /// <summary>Recording stopped with an error after it had started (device unplugged or access revoked).</summary>
    public static MicrophoneFailureKind ClassifyStopped(int? mmResult, bool accessDenied) =>
        accessDenied ? MicrophoneFailureKind.AccessDenied : MicrophoneFailureKind.Disconnected;

    /// <summary>True for Win32 ERROR_ACCESS_DENIED or HRESULT E_ACCESSDENIED.</summary>
    public static bool IsAccessDeniedCode(int code) => code is ErrorAccessDenied or EAccessDenied;

    public static string Message(MicrophoneFailureKind kind) => kind switch
    {
        MicrophoneFailureKind.NoDevice => "No microphone found. Connect one and try again; typing still works.",
        MicrophoneFailureKind.DeviceMissing => "The selected microphone isn't available. Plug it in or choose another in Settings.",
        MicrophoneFailureKind.AccessDenied => $"Microphone access is off. Turn on {PrivacyPath}.",
        MicrophoneFailureKind.BlockedOrInUse => $"Microphone is blocked or in use. Close other apps using it, or turn on {PrivacyPath}.",
        MicrophoneFailureKind.UnsupportedFormat => "This microphone can't record the audio Boo needs. Choose another in Settings.",
        MicrophoneFailureKind.Disconnected => "The microphone was disconnected. Reconnect it and try again.",
        _ => "Microphone capture could not start."
    };
}
