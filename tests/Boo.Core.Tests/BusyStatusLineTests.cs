using Boo.Core;

namespace Boo.Core.Tests;

public sealed class BusyStatusLineTests
{
    private const string Stopped = "Stopped. Nothing is running now; ask again when you are ready.";

    [Fact]
    public void Current_request_shows_its_working_message_and_Stop_replaces_it()
    {
        using var coordinator = new RequestCoordinator();
        var line = new BusyStatusLine(coordinator.IsCurrent);
        var lease = coordinator.Begin();

        Assert.Equal("Thinking…", line.Show("Thinking…", lease.Generation));
        Assert.Equal(Stopped, line.AfterStop("Thinking…", Stopped));
    }

    [Fact]
    public void Stale_request_delivered_last_neither_shows_nor_takes_ownership()
    {
        using var coordinator = new RequestCoordinator();
        var line = new BusyStatusLine(coordinator.IsCurrent);
        var old = coordinator.Begin();
        var current = coordinator.Begin();

        Assert.Equal("Thinking about the selected context…", line.Show("Thinking about the selected context…", current.Generation));
        // The superseded request's continuation resumes after an await and tries to write its own working message.
        Assert.Null(line.Show("Sent to the voice conversation; waiting for the response…", old.Generation));
        // The current request still owns the line, so Stop replaces the current request's message.
        Assert.Equal(Stopped, line.AfterStop("Thinking about the selected context…", Stopped));
    }

    [Fact]
    public void Nothing_is_shown_for_a_request_after_Stop()
    {
        using var coordinator = new RequestCoordinator();
        var line = new BusyStatusLine(coordinator.IsCurrent);
        var lease = coordinator.Begin();
        coordinator.Stop();

        Assert.Null(line.Show("Voice turn sent; waiting for the reply…", lease.Generation));
        Assert.Null(line.AfterStop("Voice turn sent; waiting for the reply…", Stopped));
    }

    [Fact]
    public void Stop_leaves_an_error_or_newer_message_alone()
    {
        using var coordinator = new RequestCoordinator();
        var line = new BusyStatusLine(coordinator.IsCurrent);
        var lease = coordinator.Begin();
        line.Show("Thinking…", lease.Generation);

        Assert.Null(line.AfterStop("OpenAI rejected the API key. Paste a valid key in Settings.", Stopped));
    }

    [Fact]
    public void Stop_releases_ownership_so_a_second_Stop_changes_nothing()
    {
        using var coordinator = new RequestCoordinator();
        var line = new BusyStatusLine(coordinator.IsCurrent);
        var lease = coordinator.Begin();
        line.Show("Thinking…", lease.Generation);

        Assert.Equal(Stopped, line.AfterStop("Thinking…", Stopped));
        Assert.Null(line.AfterStop("Thinking…", Stopped));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new BusyStatusLine(null!));
        var line = new BusyStatusLine(_ => true);
        Assert.Throws<ArgumentNullException>(() => line.Show(null!, 1));
        Assert.Throws<ArgumentNullException>(() => line.AfterStop("x", null!));
    }
}
