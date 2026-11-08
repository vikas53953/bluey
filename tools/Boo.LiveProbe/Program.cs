using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boo.Core;
using Boo.Providers;
using Boo.Storage;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("responses" or "decisions" or "realtime"))
        {
            Console.Error.WriteLine("Use responses, decisions, or realtime. Each mode performs one bounded paid probe.");
            return 2;
        }
        var fixture = CreateFixture();
        return RunAsync(args[0], fixture).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string mode, CapturedFrame fixture)
    {
        var directory = Environment.GetEnvironmentVariable("BOO_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boo");
        var credential = new CredentialStore(Path.Combine(directory, "credential.bin"));
        var settings = new ProviderSettings();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(55) };
        var timer = Stopwatch.StartNew();
        try
        {
            object evidence;
            bool success;
            switch (mode)
            {
                case "responses":
                    var reply = await new ResponsesClient(http, credential.Load, settings).RespondAsync(
                        new AssistantRequest("Explain this synthetic create-account screen. Draw one ring around the Create account button. Do not propose actions.", 1, fixture),
                        deadline.Token);
                    success = !string.IsNullOrWhiteSpace(reply.Text) && reply.Annotations?.Marks.Count > 0 && reply.Actions.Count == 0;
                    evidence = new { text = reply.Text, marks = reply.Annotations?.Marks, actionCount = reply.Actions.Count, frameWidth = 640, frameHeight = 360 };
                    break;
                case "decisions":
                    var session = new FocusSnapshot(Guid.NewGuid(), "Design a create account screen", 600, DateTimeOffset.UtcNow, 30, 30,
                        FocusStatus.Running, false, false, Array.Empty<FocusCheck>());
                    var decision = await new DecisionsClient(http, credential.Load, settings).ClassifyAsync(fixture, session, deadline.Token);
                    success = decision.Activity == FocusActivity.OnTask && decision.Probabilities.Count == 3;
                    evidence = new { activity = decision.Activity.ToString(), decision.Probability, decision.Confidence, decision.Probabilities };
                    break;
                default:
                    await using (var realtime = new RealtimeClient(credential.Load, settings))
                    {
                        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var transcript = new StringBuilder();
                        var events = new List<string>();
                        long audioBytes = 0;
                        realtime.Received += item =>
                        {
                            events.Add(item.Kind);
                            if (item.Kind == "response.output_audio.delta") audioBytes += item.Audio?.Length ?? 0;
                            if (item.Kind == "response.output_audio_transcript.delta") transcript.Append(item.Text);
                            if (item.Kind == "response.done") done.TrySetResult(true);
                            if (item.Kind is "error" or "disconnect") done.TrySetException(new InvalidOperationException("Realtime probe failed."));
                        };
                        await realtime.ConnectAsync(deadline.Token);
                        await realtime.SendTextAsync("Say exactly: Boo is ready. Do not call any tools.", deadline.Token);
                        await done.Task.WaitAsync(deadline.Token);
                        success = audioBytes > 0 && transcript.Length > 0;
                        evidence = new { transcript = transcript.ToString(), audioBytes, events, pcmRate = 24000, input = "synthetic typed request; no microphone", outputPlayed = false };
                    }
                    break;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success, mode, elapsedMilliseconds = timer.ElapsedMilliseconds,
                proof = "Live production provider adapter with synthetic fixture only; not native app or human speech acceptance",
                settings, evidence, utc = DateTimeOffset.UtcNow
            }));
            return success ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = false, mode, elapsedMilliseconds = timer.ElapsedMilliseconds,
                errorType = error.GetType().Name,
                message = "Probe failed; private provider and credential details omitted.", utc = DateTimeOffset.UtcNow
            }));
            return 1;
        }
    }

    private static CapturedFrame CreateFixture()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 360));
            drawing.DrawText(new FormattedText("Welcome", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 28, Brushes.Black, 1), new Point(60, 55));
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(45, 108, 223)), null, new Rect(220, 200, 200, 54), 8, 8);
            drawing.DrawText(new FormattedText("Create account", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20, Brushes.White, 1), new Point(250, 212));
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var memory = new MemoryStream();
        encoder.Save(memory);
        return new CapturedFrame(new ScreenContext(Guid.NewGuid(), 1,
            new DisplayInfo("synthetic", "Synthetic fixture", new RectD(0, 0, 640, 360), 1),
            640, 360, new WindowIdentity(1, 1, "synthetic-fixture", new RectD(0, 0, 640, 360)), DateTimeOffset.UtcNow), memory.ToArray());
    }
}
