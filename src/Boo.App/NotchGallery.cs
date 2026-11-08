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

    private static readonly (NotchFaceState State, int AtMs, string Name)[] Frames =
    {
        (NotchFaceState.Hover, 2000, "01-hover"),
        (NotchFaceState.Idle, 2000, "02-idle"),
        (NotchFaceState.Listening, 2000, "03-listening"),
        (NotchFaceState.Thinking, 2360, "04-thinking"),
        (NotchFaceState.Responding, 2000, "05-responding"),
        (NotchFaceState.Capture, 2000, "06-capture"),
        (NotchFaceState.Nudge, 2000, "07-nudge"),
        (NotchFaceState.Paused, 2000, "08-paused"),
        (NotchFaceState.Happy, 2280, "09-happy"),
    };

    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var notch = new NotchWindow(() => { }, () => { }, () => null, new NotchAppearance(), () => { });
        var shots = new List<(string Name, BitmapSource Image)>();
        foreach (var (state, at, name) in Frames)
        {
            var image = notch.RenderGalleryFrame(state, TimeSpan.FromMilliseconds(at), Scale);
            Save(image, Path.Combine(directory, name + ".png"));
            shots.Add((name, image));
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
