using System.Drawing;
using System.Drawing.Imaging;

namespace Boo.Demo.Tests;

/// <summary>
/// The blank-window rule of scripts/ci/Test-DemoVisual.ps1, ported so a test can ask the same question before it
/// captures: is this frame one flat rectangle covering <see cref="MaxBlankFraction"/> of the image or more, or one
/// flat colour covering <see cref="MaxColourShare"/> or more? The image is cut into tiles (about 80 across); a tile is
/// flat when every channel varies by 6 or less; flat tiles are grouped by colour after rounding each channel to 5 bits
/// with one step of tolerance. Keep the numbers equal to the script's defaults, or a frame this scan accepts can still
/// fail the check.
/// </summary>
public static class BlankScan
{
    public const double MaxBlankFraction = 0.40;
    public const double MaxColourShare = 0.70;

    public static bool IsBlank(Bitmap bitmap, out double rectangleFraction, out double colourShare)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            rectangleFraction = LargestFlatRectangle(bytes, data.Width, data.Height, data.Stride, out colourShare);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return rectangleFraction >= MaxBlankFraction || colourShare >= MaxColourShare;
    }

    /// <summary>
    /// Fraction of the image covered by the largest rectangle of flat, same-coloured tiles, and in
    /// <paramref name="colourShare"/> the largest fraction covered by flat tiles of one colour in any shape.
    /// </summary>
    public static double LargestFlatRectangle(byte[] px, int width, int height, int stride, out double colourShare)
    {
        colourShare = 0;
        var tile = Math.Max(4, (int)Math.Round(width / 80.0));
        int cols = width / tile, rows = height / tile;
        if (cols < 4 || rows < 4)
        {
            return 0;
        }

        var flat = new bool[rows, cols];
        var key = new int[rows, cols, 3];
        var counts = new Dictionary<int, int>();
        for (var ty = 0; ty < rows; ty++)
        {
            for (var tx = 0; tx < cols; tx++)
            {
                int[] min = { 255, 255, 255 }, max = { 0, 0, 0 };
                long[] sum = { 0, 0, 0 };
                for (var y = ty * tile; y < (ty + 1) * tile; y++)
                {
                    var row = y * stride;
                    for (var x = tx * tile; x < (tx + 1) * tile; x++)
                    {
                        var i = row + x * 3;
                        for (var c = 0; c < 3; c++)
                        {
                            int v = px[i + c];
                            if (v < min[c]) min[c] = v;
                            if (v > max[c]) max[c] = v;
                            sum[c] += v;
                        }
                    }
                }
                var isFlat = true;
                for (var c = 0; c < 3; c++)
                {
                    if (max[c] - min[c] > 6) isFlat = false;
                }
                flat[ty, tx] = isFlat;
                var n = tile * tile;
                for (var c = 0; c < 3; c++) key[ty, tx, c] = (int)(sum[c] / n) / 8;
                if (isFlat)
                {
                    var k = (key[ty, tx, 0] << 10) | (key[ty, tx, 1] << 5) | key[ty, tx, 2];
                    counts.TryGetValue(k, out var seen);
                    counts[k] = seen + 1;
                }
            }
        }

        var total = rows * cols;
        var bestArea = 0;
        foreach (var pair in counts)
        {
            if (pair.Value * 20 < total) continue;
            int kr = (pair.Key >> 10) & 31, kg = (pair.Key >> 5) & 31, kb = pair.Key & 31;
            var heights = new int[cols];
            var matched = 0;
            for (var ty = 0; ty < rows; ty++)
            {
                for (var tx = 0; tx < cols; tx++)
                {
                    var match = flat[ty, tx] && Math.Abs(key[ty, tx, 0] - kr) <= 1
                        && Math.Abs(key[ty, tx, 1] - kg) <= 1 && Math.Abs(key[ty, tx, 2] - kb) <= 1;
                    heights[tx] = match ? heights[tx] + 1 : 0;
                    if (match) matched++;
                }
                var starts = new Stack<int>();
                var hs = new Stack<int>();
                for (var x = 0; x <= cols; x++)
                {
                    var h = x < cols ? heights[x] : 0;
                    var start = x;
                    while (hs.Count > 0 && hs.Peek() >= h)
                    {
                        int s = starts.Pop(), sh = hs.Pop();
                        bestArea = Math.Max(bestArea, sh * (x - s));
                        start = s;
                    }
                    starts.Push(start);
                    hs.Push(h);
                }
            }
            colourShare = Math.Max(colourShare, (double)matched / total);
        }
        return (double)bestArea / total;
    }
}
