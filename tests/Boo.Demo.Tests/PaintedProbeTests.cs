using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Boo.Demo.Tests;

/// <summary>
/// <see cref="BooAppFixture.IsPainted"/> against a real native window the test owns, so the probe the demo wait relies
/// on is exercised end to end (handle, rectangle, PrintWindow, blank scan) and not only its pixel maths: a window that
/// has drawn nothing but its background is not painted, the same window is painted once it draws content, and a
/// window that is gone is not painted (so a wait on it runs to its timeout instead of returning).
/// </summary>
public sealed class PaintedProbeTests
{
    [Fact]
    public void ABlankWindowIsNotPainted_UntilItDrawsContent() => RunOnStaThread(() =>
    {
        using var automation = new UIA3Automation();
        using var form = new ProbeForm();
        form.Show();
        Pump();
        var window = automation.FromHandle(form.Handle).AsWindow();

        Assert.False(BooAppFixture.IsPainted(window), "a window showing only its white background counted as painted");

        form.Reveal();
        Pump();

        Assert.True(BooAppFixture.IsPainted(window), "a window that has drawn a gradient and text rows counted as blank");
    });

    [Fact]
    public void AClosedWindowIsNotPainted() => RunOnStaThread(() =>
    {
        using var automation = new UIA3Automation();
        var form = new ProbeForm();
        form.Reveal();
        form.Show();
        Pump();
        var window = automation.FromHandle(form.Handle).AsWindow();
        Assert.True(BooAppFixture.IsPainted(window));

        form.Close();
        form.Dispose();
        Pump();

        Assert.False(BooAppFixture.IsPainted(window), "a window that no longer exists counted as painted");
    });

    /// <summary>Windows Forms needs a single-threaded apartment; xunit's worker threads are not one.</summary>
    private static void RunOnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the probe window test did not finish within 60 s");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("Probe window test failed: " + failure.Message, failure);
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
    }

    /// <summary>A borderless 800 x 600 window: white until <see cref="Reveal"/>, then a gradient with text-like rows.</summary>
    private sealed class ProbeForm : Form
    {
        private bool _showContent;

        public void Reveal()
        {
            _showContent = true;
            Invalidate();
        }

        public ProbeForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(0, 0);
            Size = new Size(800, 600);
            BackColor = Color.White;
            ShowInTaskbar = false;
            Text = "Boo demo paint probe";
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_showContent)
            {
                return;
            }
            var area = ClientRectangle;
            using var brush = new LinearGradientBrush(area, Color.FromArgb(20, 80, 160), Color.FromArgb(180, 210, 240), 35f);
            e.Graphics.FillRectangle(brush, area);
            for (var y = 40; y < area.Height; y += 48)
            {
                e.Graphics.FillRectangle(Brushes.White, 40, y, area.Width - 80, 20);
            }
        }
    }
}
