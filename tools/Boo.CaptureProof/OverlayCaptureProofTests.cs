using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Boo.CaptureProof;

/// <summary>
/// The demo harness with Boo's data folder seeded before launch (the mock's fake OpenAI key, speech off) and with
/// window-only screenshots switched on for the whole process, exactly as the live AI smoke runs.
/// </summary>
public sealed class ProofAppFixture : BooAppFixture
{
    public const string FakeKey = "sk-qa-mock-key-000000000000000000000000";

    public ProofAppFixture()
        : base(Seed)
    {
    }

    private static void Seed(string dataDirectory)
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(AiProvider.OpenAI))).Save(FakeKey);
        new SettingsStore(Path.Combine(dataDirectory, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }
}

/// <summary>
/// Proves that a window-only screenshot taken while Boo's transparent, full-screen explanation overlay is open shows
/// Boo's windows and nothing of the desktop behind them. A borderless magenta window covers the primary screen behind
/// Boo; the mock AI's explain answer opens the overlay; the screenshots must include the overlay and must not contain
/// a single magenta pixel. A small rectangle of the decoy is copied from the real screen first, to prove the decoy was
/// visible (so the test cannot pass vacuously).
/// </summary>
public sealed class OverlayCaptureProofTests : IClassFixture<ProofAppFixture>
{
    private const string OverlayTitle = "Boo screen explanation";
    private static readonly Color Decoy = Color.FromArgb(255, 0, 255);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);

    private readonly ProofAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public OverlayCaptureProofTests(ProofAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void WindowOnlyScreenshot_WithOverlayOpen_ContainsTheOverlayAndNoDesktopPixels()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner(),
            "This proof runs only on a GitHub-hosted runner (it puts a full-screen window up and copies a screen corner).");
        Assert.False(BooAppFixture.FullDesktopCaptureAllowed(), "BOO_CAPTURE_SCOPE must be set, so screenshots are window-only.");

        _boo.WaitForNotch(UiTimeout);
        using var decoy = DecoyWindow.Show(Decoy);

        // 1. The decoy is really on screen: before Boo's panel opens (on a small VM screen it covers almost everything),
        //    copy a corner away from the top-centre notch and expect magenta.
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var corner = new Rectangle(screen.Left + 4, screen.Bottom - 24, 20, 20);
        var cornerPath = _boo.CaptureRectangle("proof-00-decoy-corner", corner);
        Assert.True(CountColour(cornerPath, Decoy) > 0, "The magenta decoy is not visible on screen, so the proof would be vacuous.");

        var panel = _boo.EnsurePanel();
        SelectFirstDisplay(panel);

        // 2. Open the explanation overlay through the mock AI (its explain answer always carries one mark).
        Find(panel, "InputBox").AsTextBox().Text = "Explain what is on my screen";
        Find(panel, "SendButton").AsButton().Invoke();
        _boo.WaitFor(() => TryFind(panel, "AnnotationDetail"), UiTimeout, "the explanation outcome text");
        var overlayOpen = BooAppFixture.WaitUntil(
            () => _boo.App.GetAllTopLevelWindows(_boo.Automation).Any(w => SafeTitle(w) == OverlayTitle && !w.IsOffscreen),
            TimeSpan.FromSeconds(10));
        Assert.True(overlayOpen, $"The '{OverlayTitle}' window did not open. Status: '{BooAppFixture.SafeName(TryFind(panel, "StatusText")!)}'");
        Thread.Sleep(1800); // pointer flight + ring stroke, inside the overlay's 10 s hold

        // 3. Window-only screenshot while the overlay is open.
        var requested = _boo.CaptureScreen("proof-01-overlay-open");
        var directory = Path.GetDirectoryName(requested)!;
        var stem = Path.GetFileNameWithoutExtension(requested);
        Assert.False(File.Exists(requested), "Window-only mode must not write a whole-screen file at the requested path.");
        var notes = File.ReadAllLines(Path.Combine(directory, stem + ".capture.txt"));
        foreach (var line in notes)
        {
            _output.WriteLine(line);
        }
        var renders = Directory.GetFiles(directory, stem + ".boo-*.png");
        Assert.NotEmpty(renders);
        Assert.Contains(notes, line => line.Contains($"'{OverlayTitle}'", StringComparison.Ordinal));

        // 4. Not one desktop (magenta) pixel in any Boo render, including the full-screen transparent overlay.
        var report = new List<string>();
        var leaked = 0;
        foreach (var render in renders.Order(StringComparer.Ordinal))
        {
            var count = CountColour(render, Decoy);
            using var image = Image.FromFile(render);
            report.Add($"{Path.GetFileName(render)}: {image.Width} x {image.Height}, decoy pixels {count}");
            leaked += count;
        }
        _boo.WriteObservation("proof-01-overlay-open", string.Join(Environment.NewLine, notes.Concat(report)));
        foreach (var line in report)
        {
            _output.WriteLine(line);
        }
        Assert.True(leaked == 0, $"{leaked} desktop (decoy) pixels appear in Boo's window-only screenshots: " + string.Join("; ", report));
    }

    private static int CountColour(string path, Color colour)
    {
        using var bitmap = new Bitmap(path);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var count = 0;
            for (var y = 0; y < data.Height; y++)
            {
                for (var x = 0; x < data.Width; x++)
                {
                    var i = y * data.Stride + x * 4; // BGRA
                    if (Math.Abs(bytes[i] - colour.B) <= 8 && Math.Abs(bytes[i + 1] - colour.G) <= 8 && Math.Abs(bytes[i + 2] - colour.R) <= 8)
                    {
                        count++;
                    }
                }
            }
            return count;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void SelectFirstDisplay(Window panel)
    {
        var combo = TryFind(panel, "DisplayBox")?.AsComboBox();
        if (combo is not null && combo.SelectedItem is null && combo.Items.Length > 0)
        {
            combo.Select(0);
            Thread.Sleep(500);
        }
    }

    private static string SafeTitle(Window window)
    {
        try
        {
            return window.Title ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static AutomationElement Find(Window panel, string automationId) =>
        TryFind(panel, automationId) ?? throw new InvalidOperationException($"No element '{automationId}' in Boo's panel.");

    private static AutomationElement? TryFind(Window panel, string automationId) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
}

/// <summary>A borderless, non-topmost window filling the primary screen with one colour, on its own UI thread.</summary>
internal sealed class DecoyWindow : IDisposable
{
    private readonly Thread _thread;
    private System.Windows.Forms.Form? _form;

    private DecoyWindow(Color colour)
    {
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _form = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                BackColor = colour,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds,
                ShowInTaskbar = false,
                TopMost = false,
                Text = "Capture proof decoy",
            };
            _form.Shown += (_, _) => ready.Set();
            System.Windows.Forms.Application.Run(_form);
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("The decoy window did not appear.");
        }
        Thread.Sleep(500);
    }

    public static DecoyWindow Show(Color colour) => new(colour);

    public void Dispose()
    {
        var form = _form;
        if (form is not null && !form.IsDisposed)
        {
            form.BeginInvoke(new Action(form.Close));
        }
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}
