using Boo.Core;
using Boo.Windows;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Boo.Integration.Tests;

/// <summary>
/// Issue #127, executor backstop: even with a valid approval ticket, DesktopTools must refuse to type text that
/// carries an invisible Unicode Format character (U+061C, U+200E, U+200F, a tag character outside the BMP) or an
/// unpaired surrogate, before any input is sent. The target is a made-up notepad window: the refusal must happen
/// before Boo looks for it, so nothing on this PC receives input.
/// </summary>
public sealed class TypeTextExecutorInvisibleCharacterTests
{
    [Theory]
    [InlineData("pay؜pal")]
    [InlineData("pay‎pal")]
    [InlineData("pay‏pal")]
    [InlineData("pay\U000E0041pal")]
    [InlineData("pay\uD800pal")]
    public async Task Executor_refuses_approved_text_with_an_invisible_character(string text)
    {
        using var targets = new TargetObserver();
        using var coordinator = new RequestCoordinator();
        var lease = coordinator.Begin();
        var request = new ToolRequest("type_text", "notepad",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["text"] = text }, lease.Generation);
        var authority = new TaskAuthority(lease.Generation, true, new HashSet<string>(StringComparer.Ordinal) { "type_text" });
        var approval = ActionPolicy.Approve(request);
        var fakeTarget = new WindowIdentity(0x7FFF_0001, int.MaxValue - 7, "notepad", new RectD(0, 0, 800, 600));

        var outcome = await new DesktopTools(coordinator, targets).ExecuteAsync(request, authority, approval, fakeTarget, lease.Token);

        Assert.False(outcome.Success);
        Assert.Contains("hidden control characters", outcome.Message);
    }
}
