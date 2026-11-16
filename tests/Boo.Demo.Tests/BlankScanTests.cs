using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Boo.Demo.Tests;

/// <summary>
/// <see cref="BlankScan"/> must agree with scripts/ci/Test-DemoVisual.ps1 on the frames the fixture waits on: a
/// never-painted white window is blank, a window with content is not, and a frame the scan accepts would pass the check.
/// </summary>
public sealed class BlankScanTests
{
    private const int W = 1280, H = 720;

    [Fact]
    public void AWhiteWindowIsBlank()
    {
        using var bitmap = Solid(Color.White);
        Assert.True(BlankScan.IsBlank(bitmap, out var rectangle, out var share));
        Assert.True(rectangle >= 0.99, $"largest flat rectangle {rectangle:P0}");
        Assert.True(share >= 0.99, $"flat colour share {share:P0}");
    }

    [Fact]
    public void AWhiteWindowWithASmallPopUpIsStillBlank()
    {
        using var bitmap = Solid(Color.White);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.FillRectangle(Brushes.SteelBlue, W / 2 - 160, H / 2 - 100, 320, 200);
        }
        Assert.True(BlankScan.IsBlank(bitmap, out var rectangle, out var share));
        Assert.True(share >= BlankScan.MaxColourShare, $"flat colour share {share:P0}");
    }

    [Fact]
    public void AGradientWithTextRowsIsNotBlank()
    {
        using var bitmap = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(20, 80, 160), Color.FromArgb(180, 210, 240), 35f);
            g.FillRectangle(brush, 0, 0, W, H);
            for (var y = 40; y < H; y += 48)
            {
                g.FillRectangle(Brushes.White, 40, y, W - 80, 20);
            }
        }
        Assert.False(BlankScan.IsBlank(bitmap, out var rectangle, out var share));
        Assert.True(rectangle < BlankScan.MaxBlankFraction, $"largest flat rectangle {rectangle:P0}");
        Assert.True(share < BlankScan.MaxColourShare, $"flat colour share {share:P0}");
    }

    [Fact]
    public void ATinyImageIsNotJudged()
    {
        using var bitmap = new Bitmap(8, 8, PixelFormat.Format24bppRgb);
        Assert.False(BlankScan.IsBlank(bitmap, out var rectangle, out var share));
        Assert.Equal(0, rectangle);
        Assert.Equal(0, share);
    }

    private static Bitmap Solid(Color colour)
    {
        var bitmap = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(colour);
        return bitmap;
    }
}
