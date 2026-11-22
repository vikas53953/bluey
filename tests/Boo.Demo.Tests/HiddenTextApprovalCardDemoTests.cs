using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.Demo.Tests;

/// <summary>
/// Issue #127 on the REAL approval card of the published Boo.exe (mock api.openai.com, tools/Boo.QaMockAi).
/// The test owns a small target window (a WinForms text field, automation ID <see cref="FieldId"/>) so Boo
/// captures a real frame and observes a real settable control; the mock then proposes type_text into that field:
/// 0 settings (fake key, display)
/// 1 control: visible text ("qa-visible-type") -> the card shows the exact text and Approve is ENABLED
///   (so the hidden-text step cannot pass because cards are simply never approvable here); Cancel, nothing typed
/// 2 hidden text ("qa-hidden-type"): U+200E, U+061C, U+200F and the tag character U+E0041 -> the card shows each
///   as a visible [U+XXXX] marker, holds no invisible character, Approve is DISABLED with the hidden-character
///   reason, and the target field stays empty.
/// A build whose card path skips the hidden-character check enables Approve in step 2, so step 2 fails.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class HiddenTextApprovalCardDemoTests : IClassFixture<BooAppFixture>
{
    public const string FieldId = "QaHiddenTextField";
    public const string TargetTitle = "Boo QA hidden-text target";

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(3);
    private const string FakeKey = "sk-qa-mock-key-000000000000000000000000";
    private const string HiddenReason = "hidden control characters";

    /// <summary>Same text as tools/Boo.QaMockAi QaHiddenText.VisibleText.</summary>
    private const string VisibleText = "paypal visible 123";

    /// <summary>Same text as tools/Boo.QaMockAi QaHiddenText.HiddenText, built from code points.</summary>
    private static readonly string HiddenText =
        "pay" + Cp(0x200E) + "pal " + Cp(0x061C) + "x" + Cp(0x200F) + " tag" + Cp(0xE0041);

    private static readonly string[] ExpectedMarkers = ["[U+200E]", "[U+061C]", "[U+200F]", "[U+E0041]"];

    private static TargetWindow? s_target;

    private readonly BooAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public HiddenTextApprovalCardDemoTests(BooAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact, Step(0)]
    public void Step0_Settings_FakeKeyAndDisplay()
    {
        Evidence("h0-settings", () =>
        {
            _boo.WaitForNotch(UiTimeout);
            var panel = _boo.EnsurePanel();

            var credential = Find(panel, "CredentialBox");
            credential.Focus();
            credential.Click();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(FakeKey);
            Thread.Sleep(200);
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel).StartsWith("Settings saved", StringComparison.Ordinal), UiTimeout),
                $"Status did not report 'Settings saved'. Status: '{StatusText(panel)}'");
            EnsureDisplaySelected(panel);
            _boo.WriteObservation("h0-settings", $"status='{StatusText(panel)}'");
            _boo.CaptureScreen("h0-settings-saved");
        });
    }

    [Fact, Step(1)]
    public void Step1_VisibleText_CardShowsItExactly_AndApproveIsEnabled()
    {
        Evidence("h1-visible", () =>
        {
            var card = AskForTyping("task qa-visible-type: type the visible text into the field");
            var shown = CardText(card.Panel);
            var approve = Find(card.Panel, "ApproveButton");
            _boo.CaptureScreen("h1-visible-card");
            _boo.WriteObservation("h1-visible", Describe(card.Panel, shown, approve));

            Assert.StartsWith("Type text", card.Title, StringComparison.Ordinal);
            Assert.Equal(VisibleText, shown);
            Assert.True(approve.IsEnabled, $"Approve must be enabled for visible text. Reason: '{NameOfId(card.Panel, "ActionBlockedReason")}'");

            CancelAndConfirmNothingTyped(card.Panel, "h1-visible");
        });
    }

    [Fact, Step(2)]
    public void Step2_HiddenText_CardShowsMarkers_AndApproveIsDisabled()
    {
        try
        {
            HiddenTextStep();
        }
        finally
        {
            s_target?.Dispose();
            s_target = null;
        }
    }

    private void HiddenTextStep()
    {
        Evidence("h2-hidden", () =>
        {
            var card = AskForTyping("task qa-hidden-type: type the text into the field");
            var shown = CardText(card.Panel);
            var approve = Find(card.Panel, "ApproveButton");
            var reason = NameOfId(card.Panel, "ActionBlockedReason");
            _boo.CaptureScreen("h2-hidden-card");
            _boo.WriteObservation("h2-hidden", Describe(card.Panel, shown, approve));

            Assert.StartsWith("Type text", card.Title, StringComparison.Ordinal);
            foreach (var marker in ExpectedMarkers)
            {
                Assert.True(shown.Contains(marker, StringComparison.Ordinal), $"The card text '{Escape(shown)}' does not show {marker}.");
            }
            var invisible = shown.EnumerateRunes()
                .Where(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control)
                .Select(rune => "U+" + rune.Value.ToString("X4", CultureInfo.InvariantCulture))
                .ToList();
            Assert.True(invisible.Count == 0, $"The card still displays invisible characters: {string.Join(", ", invisible)}");
            Assert.False(approve.IsEnabled, $"Approve must be disabled for hidden text. Card text: '{Escape(shown)}'");
            Assert.Contains(HiddenReason, reason, StringComparison.Ordinal);

            CancelAndConfirmNothingTyped(card.Panel, "h2-hidden");
        });
    }

    private (Window Panel, string Title) AskForTyping(string request)
    {
        s_target ??= TargetWindow.Open(TargetTitle, FieldId);

        // Bring the target window to the front so Boo records it as the window that would receive the typing,
        // then reopen the panel (Ctrl+Alt+B) and ask. Boo never treats its own windows as the target.
        var target = _boo.WaitFor(
            () => _boo.Automation.GetDesktop().FindFirstChild(cf => cf.ByName(TargetTitle))?.AsWindow(),
            UiTimeout, "the QA target window");
        target.SetForeground();
        target.Focus();
        Thread.Sleep(500);
        var panel = _boo.EnsurePanel();
        if (VisibleOrNull(TryFind(panel, "InputBox")) is null)
        {
            BooAppFixture.PressOpenShortcut();
            panel = _boo.WaitForPanel(TimeSpan.FromSeconds(15));
        }

        SetText(panel, "InputBox", request);
        Find(panel, "SendButton").AsButton().Invoke();
        _output.WriteLine($"Sent: {request}");

        Assert.True(
            BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null, UiTimeout),
            $"No approval card appeared. Status: '{StatusText(panel)}' Capture: '{NameOfId(panel, "CaptureDetail")}' Transcript: '{TranscriptText(panel)}'");
        return (panel, NameOfId(panel, "ActionPreviewText"));
    }

    private void CancelAndConfirmNothingTyped(Window panel, string step)
    {
        Find(panel, "CancelButton").AsButton().Invoke();
        Assert.True(
            BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is null, UiTimeout),
            $"The card did not close after Cancel. Status: '{StatusText(panel)}'");
        Assert.False(BooAppFixture.WaitUntil(() => s_target!.FieldText().Length > 0, SettleTimeout),
            $"Text reached the target field: '{Escape(s_target!.FieldText())}'");
        _boo.WriteObservation(step + "-cancelled", $"status='{StatusText(panel)}' targetField='{Escape(s_target!.FieldText())}'");
    }

    /// <summary>The card's "Text" field as the reader sees it (the TextBox value bound to DisplayValue).</summary>
    private string CardText(Window panel)
    {
        var preview = Find(panel, "ActionPreview");
        var field = _boo.WaitFor(
            () => preview.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                .FirstOrDefault(e => string.Equals(BooAppFixture.SafeName(e), "Text", StringComparison.Ordinal)),
            UiTimeout, "the card's Text field");
        return field.AsTextBox().Text ?? string.Empty;
    }

    private static string Describe(Window panel, string shown, AutomationElement approve) =>
        $"title='{NameOfId(panel, "ActionPreviewText")}' destination='{DestinationText(panel)}' cardText='{Escape(shown)}' " +
        $"approveEnabled={approve.IsEnabled} reason='{NameOfId(panel, "ActionBlockedReason")}' status='{StatusText(panel)}'";

    private static string DestinationText(Window panel) =>
        panel.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
            .FirstOrDefault(e => BooAppFixture.SafeName(e) == "Resolved destination")?.AsTextBox().Text ?? string.Empty;

    /// <summary>Plain ASCII for logs: every character outside printable ASCII as \uXXXX (or \UXXXXXXXX).</summary>
    private static string Escape(string value)
    {
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            builder.Append(rune.Value is >= 0x20 and <= 0x7E
                ? rune.ToString()
                : rune.Value > 0xFFFF
                    ? "\\U" + rune.Value.ToString("X8", CultureInfo.InvariantCulture)
                    : "\\u" + rune.Value.ToString("X4", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static string Cp(int codePoint) => char.ConvertFromUtf32(codePoint);

    private void EnsureDisplaySelected(Window panel)
    {
        var combo = TryFind(panel, "DisplayBox")?.AsComboBox();
        if (combo is null) return;
        try
        {
            if (combo.SelectedItem is null && combo.Items.Length > 0)
            {
                combo.Select(0);
                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"DisplayBox selection skipped: {ex.Message}");
        }
    }

    private static string TranscriptText(Window panel)
    {
        var list = TryFind(panel, "TranscriptList");
        return list is null ? string.Empty : string.Join(" || ", list.FindAllDescendants().Select(BooAppFixture.SafeName).Where(s => s.Length > 0));
    }

    private static AutomationElement? VisibleOrNull(AutomationElement? element)
    {
        try
        {
            return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0 ? element : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Evidence(string step, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            var shot = _boo.CaptureScreen(step + "-FAILED");
            _boo.WriteObservation(step + "-FAILED", ex.ToString());
            _output.WriteLine($"Failure screenshot: {shot}");
            throw;
        }
    }

    private static AutomationElement? TryFind(Window panel, string automationId) =>
        BooAppFixture.FindInView(panel, automationId);

    private AutomationElement Find(Window panel, string automationId) =>
        _boo.WaitFor(() => TryFind(panel, automationId), UiTimeout, $"element '{automationId}'");

    private void SetText(Window panel, string automationId, string value)
    {
        var box = Find(panel, automationId).AsTextBox();
        box.Text = value;
        Assert.True(
            BooAppFixture.WaitUntil(() => string.Equals(box.Text, value, StringComparison.OrdinalIgnoreCase), UiTimeout),
            $"Could not set {automationId} to {value}; it reads '{box.Text}'.");
    }

    private static string StatusText(Window panel) => NameOfId(panel, "StatusText");

    private static string NameOfId(Window panel, string automationId) =>
        TryFind(panel, automationId) is { } e ? BooAppFixture.SafeName(e) : string.Empty;

    /// <summary>
    /// A plain window in this test process (not Boo, not a shell) with one editable text field whose UI Automation ID
    /// is the field's Name. It runs its own message loop on an STA thread.
    /// </summary>
    private sealed class TargetWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _shown = new();
        private System.Windows.Forms.Form? _form;
        private System.Windows.Forms.TextBox? _field;

        private TargetWindow(string title, string fieldId)
        {
            _thread = new Thread(() =>
            {
                _field = new System.Windows.Forms.TextBox
                {
                    Name = fieldId,
                    Location = new System.Drawing.Point(20, 40),
                    Width = 420,
                };
                _form = new System.Windows.Forms.Form
                {
                    Text = title,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Location = new System.Drawing.Point(40, 40),
                    Size = new System.Drawing.Size(480, 160),
                    ShowInTaskbar = true,
                };
                _form.Controls.Add(_field);
                _form.Shown += (_, _) => _shown.Set();
                System.Windows.Forms.Application.Run(_form);
            })
            {
                IsBackground = true,
                Name = "QA hidden-text target",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_shown.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The QA target window did not appear.");
            }
        }

        public static TargetWindow Open(string title, string fieldId) => new(title, fieldId);

        public string FieldText() => _form!.Invoke(() => _field!.Text);

        public void Dispose()
        {
            try
            {
                _form?.Invoke(() => _form.Close());
            }
            catch (InvalidOperationException)
            {
                // Already closed.
            }
            _thread.Join(TimeSpan.FromSeconds(10));
            _shown.Dispose();
        }
    }
}
