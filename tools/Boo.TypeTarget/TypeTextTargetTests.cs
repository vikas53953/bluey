using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Boo.Core;
using Boo.Windows;
using Xunit.Abstractions;

namespace Boo.TypeTarget;

/// <summary>
/// Issue #125: an approved <c>type_text</c> names field A (automationId "FieldA") of a window, but A is gone, read-only
/// or not a value field, while field B of the same window has keyboard focus. Boo must type into A or nowhere: B must
/// stay empty and the result must say nothing was typed. A control case (A present and editable) proves the harness
/// sees typing land, so an empty B is not a false pass. Boo's real DesktopTools drives a real second process
/// (tools/Boo.TypeTarget.App); each case first checks that the window is in front with B focused, so a refused
/// activation cannot pass as "nothing typed".
/// </summary>
public sealed class TypeTextTargetTests
{
    private const string Title = "Boo type target";
    private const string Text = "boo-125-typed";
    private readonly ITestOutputHelper _output;

    public TypeTextTargetTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("missing")]
    [InlineData("readonly")]
    [InlineData("nonvalue")]
    public async Task ApprovedFieldUnusable_TypesNowhere_AndSaysSo(string mode)
    {
        var run = await RunAsync(mode);

        Assert.True(run.FieldB.Length == 0,
            $"text approved for FieldA landed in FieldB (the focused field): FieldB='{run.FieldB}'. {run.Summary}");
        Assert.False(run.Outcome.Success, run.Summary);
        Assert.False(run.Outcome.Committed, run.Summary);
        Assert.Contains("Nothing was typed", run.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("FieldA", run.Outcome.Message, StringComparison.Ordinal);
        if (run.FieldA is not null)
        {
            Assert.True(run.FieldA.Length == 0, $"FieldA changed although it cannot be set safely: '{run.FieldA}'. {run.Summary}");
        }
    }

    [Fact]
    public async Task ApprovedFieldPresent_TypesIntoItOnly()
    {
        var run = await RunAsync("present");

        Assert.True(run.FieldA == Text, $"FieldA should hold the approved text. {run.Summary}");
        Assert.True(run.FieldB.Length == 0, $"FieldB changed: '{run.FieldB}'. {run.Summary}");
        Assert.True(run.Outcome.Success, run.Summary);
    }

    /// <summary>
    /// Issue #134: the approved field is matched by its exact, case-sensitive automationId, never by a field whose ID
    /// differs only by case or contains it. casefirst puts the decoy "fielda" before the real "FieldA" (only FieldA may
    /// change); caseonly ("fielda") and partialonly ("FieldA2") have no exact match, so Boo must refuse and type nowhere.
    /// </summary>
    [Theory]
    [InlineData("casefirst", "fielda")]
    [InlineData("caseonly", "fielda")]
    [InlineData("partialonly", "FieldA2")]
    public async Task ApprovedFieldMatchedOnlyByExactId(string mode, string decoyId)
    {
        var run = await RunAsync(mode, decoyId);

        Assert.True(run.Decoy == "", $"text approved for FieldA landed in the decoy '{decoyId}': '{run.Decoy}'. {run.Summary}");
        Assert.True(run.FieldB.Length == 0, $"FieldB changed: '{run.FieldB}'. {run.Summary}");
        if (mode == "casefirst")
        {
            Assert.True(run.FieldA == Text, $"the exact FieldA should hold the approved text. {run.Summary}");
            Assert.True(run.Outcome.Success, run.Summary);
            return;
        }

        Assert.Null(run.FieldA);
        Assert.False(run.Outcome.Success, run.Summary);
        Assert.False(run.Outcome.Committed, run.Summary);
        Assert.Contains("Nothing was typed", run.Outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review of PR #132 (P1): an approval that names no field (no automationId, or a blank one) must not type at all,
    /// not even into the focused field of the approved window. B has focus; nothing may reach A or B.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task NoFieldNamed_TypesNowhere_AndSaysSo(string? approvedId)
    {
        var run = await RunAsync("present", approvedId: approvedId, label: approvedId is null ? "nofield-absent" : "nofield-blank");

        Assert.True(run.FieldB.Length == 0, $"text with no named field landed in the focused FieldB: '{run.FieldB}'. {run.Summary}");
        Assert.True(run.FieldA == "", $"FieldA changed: '{run.FieldA}'. {run.Summary}");
        Assert.False(run.Outcome.Success, run.Summary);
        Assert.False(run.Outcome.Committed, run.Summary);
        Assert.Contains("names no field", run.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was typed", run.Outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review of PR #132 (P2): FieldA accepts the write and is removed from the window at once, so it cannot be read
    /// back. The text really was written, so Boo must report a committed, unverified write, never "Nothing was typed",
    /// and must not type anywhere else.
    /// </summary>
    [Fact]
    public async Task FieldRemovedAfterWrite_ReportsCommittedUnverified_NotNothingTyped()
    {
        var run = await RunAsync("removeonwrite");

        Assert.True(run.FieldB.Length == 0, $"FieldB changed: '{run.FieldB}'. {run.Summary}");
        Assert.True(run.Outcome.Committed, $"a real write must be reported as committed. {run.Summary}");
        Assert.False(run.Outcome.Success, run.Summary);
        Assert.StartsWith("partial/unverified", run.Outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was typed", run.Outcome.Message, StringComparison.Ordinal);
    }

    private sealed record Run(ToolOutcome Outcome, string? FieldA, string FieldB, string? Decoy, string Summary);

    private async Task<Run> RunAsync(string mode, string? decoyId = null, string? approvedId = "FieldA", string? label = null)
    {
        label ??= mode;
        var exe = Environment.GetEnvironmentVariable("BOO_TYPE_TARGET_EXE");
        Assert.False(string.IsNullOrWhiteSpace(exe), "BOO_TYPE_TARGET_EXE must name tools/Boo.TypeTarget.App's exe");
        Assert.True(File.Exists(exe), $"target app not found: {exe}");

        using var app = Process.Start(new ProcessStartInfo(exe!, mode) { UseShellExecute = false })!;
        try
        {
            var hwnd = WaitForMainWindow(app);
            var root = AutomationElement.FromHandle(hwnd);
            var fieldB = Find(root, "FieldB") ?? throw new InvalidOperationException("FieldB not found");

            // A real click on B brings the window to the front and gives B keyboard focus, as a user would.
            ClickCentre(fieldB);
            Assert.True(WaitUntil(() => GetForegroundWindow() == hwnd && FocusedAutomationId() == "FieldB", TimeSpan.FromSeconds(10)),
                $"precondition: the target window must be in front with FieldB focused (foreground={GetForegroundWindow()}, focused='{FocusedAutomationId()}')");

            using var targets = new TargetObserver();
            var target = targets.Displays().Select(d => targets.Snapshot(d)).FirstOrDefault(w => w is not null);
            Assert.True(target is not null && target.Handle == hwnd.ToInt64(),
                $"precondition: Boo's observer must see the target window (got {target?.Handle})");

            using var coordinator = new RequestCoordinator();
            var lease = coordinator.Begin();
            var arguments = new Dictionary<string, string> { ["text"] = Text };
            if (approvedId is not null)
            {
                arguments["automationId"] = approvedId;
            }
            var request = new ToolRequest("type_text", Title, arguments, lease.Generation);
            var authority = new TaskAuthority(lease.Generation, true, new HashSet<string> { "type_text" });
            var approval = ActionPolicy.Approve(request);

            var outcome = await new DesktopTools(coordinator, targets).ExecuteAsync(request, authority, approval, target, lease.Token);

            Thread.Sleep(750); // let any keystrokes Windows accepted reach the field before reading it back
            var fieldA = ReadValue(Find(root, "FieldA"));
            var fieldBValue = ReadValue(fieldB) ?? "";
            var decoy = decoyId is null ? null : ReadValue(Find(root, decoyId)) ?? throw new InvalidOperationException($"decoy '{decoyId}' not found");
            var summary = $"mode={label} outcome=(success={outcome.Success}, committed={outcome.Committed}, message='{outcome.Message}') " +
                          $"FieldA='{fieldA ?? "(no value field)"}' FieldB='{fieldBValue}'" +
                          (decoyId is null ? "" : $" {decoyId}='{decoy}'");
            _output.WriteLine(summary);
            Evidence(label, summary, hwnd);
            return new Run(outcome, fieldA, fieldBValue, decoy, summary);
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill();
                app.WaitForExit(5000);
            }
        }
    }

    private static IntPtr WaitForMainWindow(Process app)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            app.Refresh();
            if (app.HasExited)
            {
                throw new InvalidOperationException($"target app exited with code {app.ExitCode}");
            }
            if (app.MainWindowHandle != IntPtr.Zero && Find(AutomationElement.FromHandle(app.MainWindowHandle), "FieldB") is not null)
            {
                return app.MainWindowHandle;
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("target app window did not appear within 30 s");
    }

    private static AutomationElement? Find(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static string? ReadValue(AutomationElement? element) =>
        element is not null && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
            ? ((ValuePattern)pattern).Current.Value
            : null;

    private static string FocusedAutomationId()
    {
        try
        {
            return AutomationElement.FocusedElement?.Current.AutomationId ?? "";
        }
        catch (ElementNotAvailableException)
        {
            return "";
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return condition();
    }

    private static void ClickCentre(AutomationElement element)
    {
        var bounds = element.Current.BoundingRectangle;
        SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        var inputs = new[]
        {
            new INPUT { Type = 0, Mouse = new MOUSEINPUT { Flags = 0x0002 } },
            new INPUT { Type = 0, Mouse = new MOUSEINPUT { Flags = 0x0004 } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private void Evidence(string mode, string summary, IntPtr hwnd)
    {
        var dir = Environment.GetEnvironmentVariable("BOO_TYPE_TARGET_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{mode}.txt"), summary + Environment.NewLine);
        try
        {
            if (GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
            {
                using var bitmap = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, bitmap.Size);
                }
                bitmap.Save(Path.Combine(dir, $"{mode}.png"), ImageFormat.Png);
            }
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            _output.WriteLine($"screenshot skipped: {ex.Message}");
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MOUSEINPUT Mouse;
    }
}
