using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.Demo.Tests;

/// <summary>
/// Issue #157 / PR #158 on the published Boo.exe with the mock api.openai.com (no AI, no key). The test owns a small
/// target window with one text field; the mock's "qa-visible-type" route proposes typing into it (the same route as
/// <see cref="HiddenTextApprovalCardDemoTests"/>). Each step approves the edit, then asks "undo" and approves it:
/// 0 settings (fake key, display)
/// 1 confirmed Undo: Boo re-reads the restored field, says "observed Undo restored", and a second "undo" says exactly
///   "Nothing to undo: Boo has no recorded edit." (the recorded edit was consumed).
/// 2 Undo Boo cannot confirm: the target window moves the moment the field is restored, so Boo's re-read of the
///   window is refused. Boo must report the Undo as not verified (never "Completed after verification"), and a
///   second "undo" must not say "Nothing to undo" (the recorded edit was kept).
/// A build whose Undo read-back returns the raw typing result, or clears the journal when the re-read fails, fails
/// step 2; a build that keeps a stale journal after a confirmed Undo fails step 1.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class FieldUndoDemoTests : IClassFixture<BooAppFixture>
{
    private const string FieldId = "QaHiddenTextField";
    private const string EditedText = "paypal visible 123";
    private const string FakeKey = "sk-qa-mock-key-000000000000000000000000";
    private const string NothingToUndo = "Nothing to undo: Boo has no recorded edit.";
    private const string Unverified = "Not completed: partial/unverified: Undo ran, but Boo could not re-read the window to confirm it; the recorded edit is kept.";
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);

    private readonly BooAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public FieldUndoDemoTests(BooAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact, Step(0)]
    public void Step0_Settings_FakeKeyAndDisplay()
    {
        Evidence("u0-settings", () =>
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
            var combo = TryFind(panel, "DisplayBox")?.AsComboBox();
            if (combo is not null && combo.SelectedItem is null && combo.Items.Length > 0)
            {
                combo.Select(0);
                Thread.Sleep(500);
            }
            _boo.WriteObservation("u0-settings", $"status='{StatusText(panel)}'");
        });
    }

    [Fact, Step(1)]
    public void Step1_ConfirmedFieldUndo_SecondUndoHasNothingToUndo()
    {
        Evidence("u1-confirmed", () =>
        {
            using var target = TargetWindow.Open("Boo QA field-undo target", FieldId, "ORIGINAL-158-A", moveAfterRestore: false);
            var panel = EditThenUndo(target, "ORIGINAL-158-A", "u1");

            Assert.True(
                BooAppFixture.WaitUntil(() => TranscriptText(panel).Contains("observed Undo restored", StringComparison.Ordinal), StatusTimeout),
                $"Boo did not confirm the restore. Status: '{StatusText(panel)}' Transcript: '{TranscriptText(panel)}'");
            Assert.DoesNotContain("Not completed", StatusText(panel), StringComparison.Ordinal);

            Ask(target, "undo");
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel) == NothingToUndo, StatusTimeout),
                $"The second Undo did not say '{NothingToUndo}'. Status: '{StatusText(panel)}' Transcript: '{TranscriptText(panel)}'");
            Assert.Null(VisibleOrNull(TryFind(panel, "ActionPreview")));
            Assert.Equal("ORIGINAL-158-A", target.FieldText());
            _boo.CaptureScreen("u1-second-undo");
            _boo.WriteObservation("u1-confirmed", $"status='{StatusText(panel)}' field='{target.FieldText()}' transcript='{TranscriptText(panel)}'");
        });
    }

    [Fact, Step(2)]
    public void Step2_FieldUndoBooCannotReRead_IsReportedUnverified_AndKeepsTheRecordedEdit()
    {
        Evidence("u2-unverified", () =>
        {
            using var target = TargetWindow.Open("Boo QA field-undo moving target", FieldId, "ORIGINAL-158-B", moveAfterRestore: true);
            var panel = EditThenUndo(target, "ORIGINAL-158-B", "u2");

            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel) == Unverified, StatusTimeout),
                $"An Undo Boo could not re-read was not reported as unverified. Status: '{StatusText(panel)}' Transcript: '{TranscriptText(panel)}'");
            Assert.True(target.Moved, "precondition: the target window moved after the restore");
            _boo.CaptureScreen("u2-unverified");

            Ask(target, "undo");
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel) != Unverified && StatusText(panel).Length > 0, StatusTimeout),
                $"The second Undo gave no answer. Status: '{StatusText(panel)}'");
            Thread.Sleep(1000);
            Assert.NotEqual(NothingToUndo, StatusText(panel));
            _boo.CaptureScreen("u2-second-undo");
            _boo.WriteObservation("u2-unverified", $"status='{StatusText(panel)}' field='{target.FieldText()}' moved={target.Moved} transcript='{TranscriptText(panel)}'");
        });
    }

    /// <summary>Asks for the edit, approves it, asks "undo", approves the Undo card; returns the panel.</summary>
    private Window EditThenUndo(TargetWindow target, string original, string step)
    {
        var panel = Ask(target, "task qa-visible-type: type into the field");
        Assert.True(
            BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null, UiTimeout),
            $"No edit card. Status: '{StatusText(panel)}' Transcript: '{TranscriptText(panel)}'");
        Assert.StartsWith("Type text", NameOfId(panel, "ActionPreviewText"), StringComparison.Ordinal);
        Approve(target, panel);
        Assert.True(BooAppFixture.WaitUntil(() => target.FieldText() == EditedText, UiTimeout),
            $"The approved edit did not reach the field: '{target.FieldText()}'. Status: '{StatusText(panel)}'");
        _boo.WriteObservation(step + "-edited", $"status='{StatusText(panel)}' field='{target.FieldText()}'");

        panel = Ask(target, "undo");
        Assert.True(
            BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null, UiTimeout),
            $"No Undo card. Status: '{StatusText(panel)}' Transcript: '{TranscriptText(panel)}'");
        Assert.StartsWith("Undo: Type text", NameOfId(panel, "ActionPreviewText"), StringComparison.Ordinal);
        Approve(target, panel);
        Assert.True(BooAppFixture.WaitUntil(() => target.FieldText() == original, UiTimeout),
            $"The approved Undo did not restore the field: '{target.FieldText()}'. Status: '{StatusText(panel)}'");
        return panel;
    }

    private Window Ask(TargetWindow target, string request)
    {
        BringToFront(target);
        var panel = _boo.EnsurePanel();
        if (VisibleOrNull(TryFind(panel, "InputBox")) is null)
        {
            BooAppFixture.PressOpenShortcut();
            panel = _boo.WaitForPanel(TimeSpan.FromSeconds(15));
        }
        var box = Find(panel, "InputBox").AsTextBox();
        box.Text = request;
        BringToFront(target);
        Find(panel, "SendButton").AsButton().Invoke();
        _output.WriteLine($"Sent: {request}");
        return panel;
    }

    private void Approve(TargetWindow target, Window panel)
    {
        BringToFront(target);
        Find(panel, "ApproveButton").AsButton().Invoke();
    }

    private void BringToFront(TargetWindow target)
    {
        var window = _boo.WaitFor(
            () => _boo.Automation.GetDesktop().FindFirstChild(cf => cf.ByName(target.Title))?.AsWindow(),
            UiTimeout, "the QA field-undo target window");
        window.SetForeground();
        Thread.Sleep(400);
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
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private AutomationElement Find(Window panel, string automationId) =>
        _boo.WaitFor(() => TryFind(panel, automationId), UiTimeout, $"element '{automationId}'");

    private static string StatusText(Window panel) => NameOfId(panel, "StatusText");

    private static string NameOfId(Window panel, string automationId) =>
        TryFind(panel, automationId) is { } e ? BooAppFixture.SafeName(e) : string.Empty;

    /// <summary>
    /// A plain window in this test process with one text field (UI Automation ID = its Name), on its own STA thread.
    /// With moveAfterRestore, the window moves 7 px the first time the field changes away from the edited text after
    /// having held it, i.e. right when an Undo restores the field, so Boo's re-read of the window is refused.
    /// </summary>
    private sealed class TargetWindow : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _shown = new();
        private System.Windows.Forms.Form? _form;
        private System.Windows.Forms.TextBox? _field;
        private volatile bool _moved;

        private TargetWindow(string title, string fieldId, string original, bool moveAfterRestore)
        {
            Title = title;
            _thread = new Thread(() =>
            {
                var field = new System.Windows.Forms.TextBox
                {
                    Name = fieldId,
                    Text = original,
                    Location = new System.Drawing.Point(20, 40),
                    Width = 420,
                };
                var form = new System.Windows.Forms.Form
                {
                    Text = title,
                    StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                    Location = new System.Drawing.Point(40, 40),
                    Size = new System.Drawing.Size(480, 160),
                    ShowInTaskbar = true,
                };
                _field = field;
                _form = form;
                var sawEdit = false;
                field.TextChanged += (_, _) =>
                {
                    if (field.Text == EditedText)
                    {
                        sawEdit = true;
                    }
                    else if (moveAfterRestore && sawEdit && !_moved)
                    {
                        form.Left += 7;
                        _moved = true;
                    }
                };
                form.Controls.Add(field);
                form.Shown += (_, _) => _shown.Set();
                System.Windows.Forms.Application.Run(form);
            })
            {
                IsBackground = true,
                Name = "QA field-undo target",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_shown.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The QA field-undo target window did not appear.");
            }
        }

        public string Title { get; }

        public bool Moved => _moved;

        public static TargetWindow Open(string title, string fieldId, string original, bool moveAfterRestore) =>
            new(title, fieldId, original, moveAfterRestore);

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
