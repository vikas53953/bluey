namespace Boo.Core;

public sealed class PresenceController
{
    private bool _pointerInside;
    private bool _captureActive;
    private bool _microphoneActive;

    public bool IsRevealed => _pointerInside || _captureActive || _microphoneActive;

    public void SetPointerInside(bool inside)
    {
        _pointerInside = inside;
    }

    public void SetCaptureActive(bool active)
    {
        _captureActive = active;
    }

    public void SetMicrophoneActive(bool active)
    {
        _microphoneActive = active;
    }
}
