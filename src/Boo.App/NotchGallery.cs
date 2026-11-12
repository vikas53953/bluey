using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boo.Core;

namespace Boo.App;

/// <summary>
/// "Boo.exe --notch-gallery &lt;dir&gt;": renders the notch in every face state to PNGs (2x) plus a contact sheet,
/// then exits. Used by CI for before/after screenshots; no window is shown, nothing is captured or stored.
/// </summary>
internal static class NotchGallery
{
    public const string Argument = "--notch-gallery";
    private const double Scale = 2;

    /// <summary>One gallery still: state at time, optionally on a dark desktop or frozen inside the wake-up / celebrate.</summary>
    private readonly record struct Shot(NotchFaceState State, int AtMs, string Name, bool Dark = false, int? WakeMs = null, int? CelebrateMs = null);

    private static readonly Shot[] Frames =
    {
        new(NotchFaceState.Hover, 2000, "01-hover"),
        new(NotchFaceState.Idle, 2000, "02-idle"),
        new(NotchFaceState.Listening, 2000, "03-listening"),
        new(NotchFaceState.Thinking, 2360, "04-thinking"),
        new(NotchFaceState.Responding, 2000, "05-responding"),
        new(NotchFaceState.Capture, 2000, "06-capture"),
        new(NotchFaceState.Nudge, 2000, "07-nudge"),
        new(NotchFaceState.Paused, 2000, "08-paused"),
        new(NotchFaceState.Happy, 2280, "09-happy"),
        // Living ink: the same blot on a dark desktop (ink body + quiet rim, cream eyes; never a white pocket).
        new(NotchFaceState.Idle, 2000, "10-dark-idle", Dark: true),
        new(NotchFaceState.Thinking, 2360, "11-dark-thinking", Dark: true),
        new(NotchFaceState.Responding, 2000, "12-dark-responding", Dark: true),
        // Wake-up (~520 ms): stain, bead, eyes shut, peek, blink, looks over.
        new(NotchFaceState.Idle, 2000, "20-wake-1-stain", WakeMs: 45),
        new(NotchFaceState.Idle, 2000, "21-wake-2-bead", WakeMs: 165),
        new(NotchFaceState.Idle, 2000, "22-wake-3-eyes-shut", WakeMs: 240),
        new(NotchFaceState.Idle, 2000, "23-wake-4-peek", WakeMs: 325),
        new(NotchFaceState.Idle, 2000, "24-wake-5-blink", WakeMs: 415),
        new(NotchFaceState.Idle, 2000, "25-wake-6-looks-over", WakeMs: 515),
        // Celebrate (focus session complete, ~1 s): squash with splatters, then the splatters fade.
        new(NotchFaceState.Happy, 2280, "30-celebrate-squash", CelebrateMs: 190),
        new(NotchFaceState.Happy, 2280, "31-celebrate-splatters", CelebrateMs: 520),
    };

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var notch = new NotchWindow(() => { }, () => { }, () => null, new NotchAppearance(), () => { });
        var shots = new List<(string Name, BitmapSource Image)>();
        foreach (var shot in Frames)
        {
            var image = notch.RenderGalleryFrame(shot.State, TimeSpan.FromMilliseconds(shot.AtMs), Scale, shot.Dark, shot.WakeMs, shot.CelebrateMs);
            Save(image, Path.Combine(directory, shot.Name + ".png"));
            shots.Add((shot.Name, image));
        }
        Save(ContactSheet(shots), Path.Combine(directory, "00-contact-sheet.png"));
        notch.Close();
        return 0;
    }

    private static BitmapSource ContactSheet(IReadOnlyList<(string Name, BitmapSource Image)> shots)
    {
        const double pad = 16 * Scale;
        const double label = 18 * Scale;
        var width = shots.Max(s => s.Image.PixelWidth) + 2 * pad;
        var height = shots.Sum(s => s.Image.PixelHeight + label + pad) + pad;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xE9, 0xE6)), null, new Rect(0, 0, width, height));
            var y = pad;
            var typeface = new Typeface("Segoe UI");
            foreach (var (name, image) in shots)
            {
                var text = new FormattedText(name, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    typeface, 11 * Scale, System.Windows.Media.Brushes.DimGray, 1);
                dc.DrawText(text, new System.Windows.Point(pad, y));
                y += label;
                dc.DrawImage(image, new Rect((width - image.PixelWidth) / 2, y, image.PixelWidth, image.PixelHeight));
                y += image.PixelHeight + pad;
            }
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
