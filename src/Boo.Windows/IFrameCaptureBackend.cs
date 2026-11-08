using Boo.Core;

namespace Boo.Windows;

public interface IFrameCaptureBackend
{
    Task<CapturedFrame> CaptureAsync(DisplayInfo display, WindowIdentity target,
        IReadOnlyList<long> ownedWindows, long generation, CancellationToken cancellationToken);
}
