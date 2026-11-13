using System.IO;
using System.Net;
using System.Net.Sockets;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.VerifyLive.Regressions;

/// <summary>
/// A fresh Boo whose network never answers: HTTPS_PROXY points Boo's process (only) at a local listener that accepts
/// and stays silent, so a typed question stays pending (Boo's own 45 s timeout) and NOTHING reaches OpenAI (the CONNECT
/// is never answered, so no TLS starts and the seeded wrong key never leaves the PC). Speech off: text path only.
/// This stands in for a slow network, so its evidence is labelled mock (no AI) in docs/FEATURE_MAP.md.
/// </summary>
public sealed class StalledNetworkBooFixture : BooAppFixture, IDisposable
{
    private static TcpListener? s_listener;
    private static readonly List<TcpClient> Held = new();

    public StalledNetworkBooFixture() : base(dir =>
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        s_listener = new TcpListener(IPAddress.Loopback, 0);
        s_listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { var c = await s_listener.AcceptTcpClientAsync(); lock (Held) Held.Add(c); }
                catch (Exception) { return; }
            }
        });
        var port = ((IPEndPoint)s_listener.LocalEndpoint).Port;
        // Read by Boo.exe at launch (BooAppFixture copies this process's environment); removed right after launch.
        Environment.SetEnvironmentVariable("HTTPS_PROXY", $"http://127.0.0.1:{port}");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
        new CredentialStore(Path.Combine(dir, AiProviders.CredentialFileName(AiProvider.OpenAI)))
            .Save("boo-regression-wrong-key-" + Guid.NewGuid().ToString("N"));
    })
    {
        // Never leak the stalled proxy into a later fixture in this test run (the live journeys need the real network).
        Environment.SetEnvironmentVariable("HTTPS_PROXY", null);
    }

    public static int HeldConnections { get { lock (Held) return Held.Count; } }

    // Interface re-implementation: BooAppFixture.Dispose is not virtual, and xunit disposes through IDisposable.
    void IDisposable.Dispose()
    {
        Dispose();
        s_listener?.Stop();
        lock (Held) { Held.ForEach(c => c.Dispose()); Held.Clear(); }
    }
}

/// <summary>
/// Regression for the app explorer's finding (issue linked in docs/FEATURE_MAP.md row S6): pressing Stop
/// (Ctrl+Alt+F12) while a typed question was pending cancelled it (the notch went back to rest) but the panel's status
/// line kept saying "Thinking about the selected context…" for good, so Boo looked busy when it was not. Found by run
/// https://github.com/vikas53953/bluey/actions/runs/43468157115 (l04-stop-mid-reply-FAILED.txt).
/// The rule this test holds: after Stop, no status line may still claim work is in progress (the class: any busy
/// message set by a request that Stop cancelled).
/// </summary>
public sealed class StopStatusRegressionTests : IClassFixture<StalledNetworkBooFixture>
{
    private readonly StalledNetworkBooFixture _boo;
    private readonly ITestOutputHelper _output;

    public StopStatusRegressionTests(StalledNetworkBooFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void StopDuringPendingQuestion_StatusNoLongerSaysThinking()
    {
        _boo.WaitForNotch(TimeSpan.FromSeconds(60));
        Thread.Sleep(1000); // the open hotkey is registered by the notch window
        var panel = _boo.EnsurePanel();
        try
        {
            var combo = panel.FindFirstDescendant(cf => cf.ByAutomationId("DisplayBox"))?.AsComboBox();
            if (combo is not null && combo.SelectedItem is null && combo.Items.Length > 0) combo.Select(0);
        }
        catch (Exception ex) { _output.WriteLine("DisplayBox: " + ex.Message); }

        Element(panel, "InputBox").AsTextBox().Text = "Regression: a question that never gets an answer";
        Element(panel, "SendButton").AsButton().Invoke();
        var pending = BooAppFixture.WaitUntil(() => Status(panel).StartsWith("Thinking", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        Thread.Sleep(2000);
        var beforeStop = Status(panel);
        Assert.True(pending && beforeStop.StartsWith("Thinking", StringComparison.Ordinal),
            $"TEST SETUP: the question was not pending before Stop (status '{beforeStop}', proxy connections {StalledNetworkBooFixture.HeldConnections}). Nothing about Stop was checked.");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.F12);
        Thread.Sleep(5000);
        var afterStop = Status(panel);
        _boo.CaptureScreen("regression-stop-status");
        _boo.WriteObservation("regression-stop-status",
            $"beforeStop='{beforeStop}' afterStop5s='{afterStop}' proxyConnections={StalledNetworkBooFixture.HeldConnections}");
        _output.WriteLine($"afterStop='{afterStop}'");

        Assert.False(afterStop.StartsWith("Thinking", StringComparison.Ordinal) || afterStop.Contains("waiting for the response", StringComparison.OrdinalIgnoreCase),
            $"5 s after Stop the status still says Boo is working: '{afterStop}'.");
    }

    private AutomationElement Element(Window panel, string id) =>
        _boo.WaitFor(() => panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(30), $"element '{id}'");

    private static string Status(Window panel) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId("StatusText")) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
