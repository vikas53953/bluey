using Boo.Core;

namespace Boo.Core.Tests;

// FROZEN acceptance tests for the face lane (plan U1/U2). Builders must not edit, weaken, skip or delete.
public sealed class FaceStateResolverTests
{
    [Fact]
    public void Priority_table_is_documented_order()
    {
        Assert.Equal(
            new[]
            {
                NotchFaceState.Capture, NotchFaceState.Listening, NotchFaceState.Responding, NotchFaceState.Thinking,
                NotchFaceState.Nudge, NotchFaceState.Happy, NotchFaceState.Paused, NotchFaceState.Hover,
                NotchFaceState.Idle, NotchFaceState.Hidden,
            },
            FaceStateResolver.Priority);
    }

    public static TheoryData<FaceInputs, NotchFaceState> Single => new()
    {
        { new FaceInputs(), NotchFaceState.Hidden },
        { new FaceInputs(Revealed: true), NotchFaceState.Idle },
        { new FaceInputs(Revealed: true, Hovered: true), NotchFaceState.Hover },
        { new FaceInputs(Hovered: true), NotchFaceState.Hover },
        { new FaceInputs(Listening: true), NotchFaceState.Listening },
        { new FaceInputs(Thinking: true), NotchFaceState.Thinking },
        { new FaceInputs(Responding: true), NotchFaceState.Responding },
        { new FaceInputs(CaptureActive: true), NotchFaceState.Capture },
        { new FaceInputs(NudgeShowing: true), NotchFaceState.Nudge },
        { new FaceInputs(FocusPaused: true), NotchFaceState.Paused },
        { new FaceInputs(Happy: true), NotchFaceState.Happy },
        { new FaceInputs(UserHidden: true), NotchFaceState.Hidden },
    };

    [Theory]
    [MemberData(nameof(Single))]
    public void Single_input_maps_to_its_state(FaceInputs inputs, NotchFaceState expected)
    {
        Assert.Equal(expected, FaceStateResolver.Resolve(inputs));
    }

    public static TheoryData<FaceInputs, NotchFaceState> Conflicts => new()
    {
        { new FaceInputs(Revealed: true, Hovered: true, CaptureActive: true), NotchFaceState.Capture },
        { new FaceInputs(CaptureActive: true, Listening: true, Responding: true, Thinking: true, NudgeShowing: true, Happy: true, FocusPaused: true, Revealed: true, Hovered: true), NotchFaceState.Capture },
        { new FaceInputs(Listening: true, Responding: true, Thinking: true), NotchFaceState.Listening },
        { new FaceInputs(Responding: true, Thinking: true, NudgeShowing: true), NotchFaceState.Responding },
        { new FaceInputs(Thinking: true, NudgeShowing: true, Hovered: true), NotchFaceState.Thinking },
        { new FaceInputs(NudgeShowing: true, Happy: true, FocusPaused: true), NotchFaceState.Nudge },
        { new FaceInputs(Happy: true, FocusPaused: true, Hovered: true), NotchFaceState.Happy },
        { new FaceInputs(FocusPaused: true, Hovered: true, Revealed: true), NotchFaceState.Paused },
        { new FaceInputs(Listening: true, Hovered: true, Revealed: true), NotchFaceState.Listening },
    };

    [Theory]
    [MemberData(nameof(Conflicts))]
    public void Higher_priority_wins(FaceInputs inputs, NotchFaceState expected)
    {
        Assert.Equal(expected, FaceStateResolver.Resolve(inputs));
    }

    [Fact]
    public void Capture_is_never_hidden_by_any_combination()
    {
        // Exhaustive over all 2^9 combinations of the other flags.
        for (var mask = 0; mask < 1 << 9; mask++)
        {
            var inputs = FromMask(mask) with { CaptureActive = true };
            Assert.Equal(NotchFaceState.Capture, FaceStateResolver.Resolve(inputs));
        }
    }

    [Fact]
    public void Every_combination_resolves_to_exactly_the_highest_priority_active_state()
    {
        var priority = FaceStateResolver.Priority;
        for (var mask = 0; mask < 1 << 10; mask++)
        {
            var inputs = FromMask(mask & 0x1FF) with { CaptureActive = (mask & 0x200) != 0 };
            var state = FaceStateResolver.Resolve(inputs);
            Assert.True(Enum.IsDefined(state));
            Assert.True(IsActive(state, inputs), $"{state} is not active for {inputs}");

            // No active state ranked above the result may exist.
            foreach (var higher in priority.TakeWhile(s => s != state))
                Assert.False(IsActive(higher, inputs), $"{higher} outranks {state} for {inputs}");
        }
    }

    [Fact]
    public void User_hidden_hides_everything_except_privacy_disclosures()
    {
        Assert.Equal(NotchFaceState.Hidden, FaceStateResolver.Resolve(new FaceInputs(UserHidden: true, Revealed: true, Hovered: true, Thinking: true, Responding: true, NudgeShowing: true, Happy: true, FocusPaused: true)));
        Assert.Equal(NotchFaceState.Capture, FaceStateResolver.Resolve(new FaceInputs(UserHidden: true, CaptureActive: true)));
        Assert.Equal(NotchFaceState.Listening, FaceStateResolver.Resolve(new FaceInputs(UserHidden: true, Listening: true)));
    }

    private static FaceInputs FromMask(int m) => new(
        Revealed: (m & 1) != 0,
        Hovered: (m & 2) != 0,
        Listening: (m & 4) != 0,
        Thinking: (m & 8) != 0,
        Responding: (m & 16) != 0,
        NudgeShowing: (m & 32) != 0,
        FocusPaused: (m & 64) != 0,
        Happy: (m & 128) != 0,
        UserHidden: (m & 256) != 0);

    private static bool IsActive(NotchFaceState state, FaceInputs i)
    {
        var shown = !i.UserHidden;
        return state switch
        {
            NotchFaceState.Capture => i.CaptureActive,
            NotchFaceState.Listening => i.Listening,
            NotchFaceState.Responding => shown && i.Responding,
            NotchFaceState.Thinking => shown && i.Thinking,
            NotchFaceState.Nudge => shown && i.NudgeShowing,
            NotchFaceState.Happy => shown && i.Happy,
            NotchFaceState.Paused => shown && i.FocusPaused,
            NotchFaceState.Hover => shown && i.Hovered,
            NotchFaceState.Idle => shown && i.Revealed,
            NotchFaceState.Hidden => true,
            _ => false,
        };
    }
}
