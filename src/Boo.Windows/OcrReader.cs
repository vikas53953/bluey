using Boo.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using CoreOcrWord = Boo.Core.OcrWord;

namespace Boo.Windows;

/// <summary>
/// Reads on-screen words from a captured frame with Windows.Media.Ocr. The frame
/// stays in memory (never persisted). Word boxes are returned in physical
/// virtual-screen pixels (via <see cref="AnnotationValidator.MapToDisplay"/>), in
/// reading order, for <see cref="OcrNudgeTargetMatcher"/>. Any failure returns an
/// empty list: no OCR means no pointer, never a guessed one.
/// </summary>
public static class OcrReader
{
    public static async Task<IReadOnlyList<CoreOcrWord>> ReadAsync(CapturedFrame frame, CancellationToken cancellationToken)
    {
        if (frame is null || frame.Jpeg is null || frame.Jpeg.Length == 0
            || frame.Context.FrameWidth <= 0 || frame.Context.FrameHeight <= 0)
        {
            return Array.Empty<CoreOcrWord>();
        }

        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                return Array.Empty<CoreOcrWord>();
            }

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(frame.Jpeg);
                await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
                writer.DetachStream();
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var sourceWidth = (double)decoder.PixelWidth;
            var sourceHeight = (double)decoder.PixelHeight;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return Array.Empty<CoreOcrWord>();
            }

            // OCR rejects images above MaxImageDimension; scale down uniformly if needed.
            var shrink = Math.Min(1d, OcrEngine.MaxImageDimension / Math.Max(sourceWidth, sourceHeight));
            var ocrWidth = Math.Max(1u, (uint)Math.Floor(sourceWidth * shrink));
            var ocrHeight = Math.Max(1u, (uint)Math.Floor(sourceHeight * shrink));
            var transform = new BitmapTransform
            {
                ScaledWidth = ocrWidth,
                ScaledHeight = ocrHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

            // OCR pixels -> captured-frame pixels -> physical display pixels.
            var toFrameX = frame.Context.FrameWidth / (double)ocrWidth;
            var toFrameY = frame.Context.FrameHeight / (double)ocrHeight;
            var words = new List<CoreOcrWord>();
            var lineIndex = 0;
            foreach (var line in result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var box = word.BoundingRect;
                    var frameRect = new RectD(box.X * toFrameX, box.Y * toFrameY, box.Width * toFrameX, box.Height * toFrameY);
                    words.Add(new CoreOcrWord(word.Text ?? string.Empty, AnnotationValidator.MapToDisplay(frameRect, frame.Context), lineIndex));
                }

                lineIndex++;
            }

            return words;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<CoreOcrWord>();
        }
        catch (Exception)
        {
            // Fail closed: OCR is optional guidance.
            return Array.Empty<CoreOcrWord>();
        }
    }
}
