using System.Globalization;
using Boo.Core;
using FsCheck;
using FsCheck.Fluent;
using E = Boo.Core.NotchEvent;

namespace Boo.Properties;

/// <summary>One random sequence of notch events, driven from <see cref="NotchLifecycleState.Initial"/>.</summary>
public sealed class EventWalk
{
    /// <summary>Creates a walk over <paramref name="events"/>.</summary>
    public EventWalk(IReadOnlyList<NotchEvent> events) => Events = events;

    /// <summary>The events, in order.</summary>
    public IReadOnlyList<NotchEvent> Events { get; }

    /// <summary>The events as FsCheck prints a counterexample: numbered, one per step.</summary>
    public override string ToString() =>
        $"{Events.Count} events: [" + string.Join(", ", Events.Select((e, i) => $"{i}:{e}")) + "]";
}

/// <summary>One reducer step of a walk.</summary>
public sealed record WalkStep(int Index, NotchLifecycleState Before, NotchEvent Event, NotchStep Step)
{
    /// <summary>The state after the step.</summary>
    public NotchLifecycleState After => Step.State;

    /// <summary>Throws with the invariant's name, the step and the event when <paramref name="holds"/> is false.</summary>
    public void Require(bool holds, string invariant)
    {
        if (!holds)
            throw new InvalidOperationException($"{invariant} broken at step {Index} ({Event}), handled={Step.Handled}");
    }
}

/// <summary>Generator, shrinker and runner shared by the notch lifecycle properties.</summary>
public static class Walks
{
    /// <summary>Name of the environment variable that sets the number of cases per property.</summary>
    public const string CasesVariable = "BOO_PROPERTY_CASES";

    /// <summary>Every event shape of <c>specs/NotchLifecycle.tla</c> except Quit (it is generated rarely, below).</summary>
    public static IReadOnlyList<NotchEvent> Alphabet { get; } = new NotchEvent[]
    {
        new E.PointerEnter(), new E.PointerLeave(), new E.Tick(),
        new E.ExpireResponding(), new E.ExpireNudge(), new E.ExpireHappy(),
        new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.Send(TypedRequestKind.Ask, TypedRequestPath.Voice),
        new E.Send(TypedRequestKind.Undo, TypedRequestPath.Plain), new E.Send(TypedRequestKind.Undo, TypedRequestPath.Voice),
        new E.TypedCapture(), new E.TypedNoFrame(), new E.TypedReply(false), new E.TypedReply(true, EditTarget.File),
        new E.TypedReply(true, EditTarget.Field), new E.TypedHandOff(), new E.TypedUndoPlan(true), new E.TypedUndoPlan(false),
        new E.TypedEnd(),
        new E.VSpeechStarted(), new E.VSpeechStopped(), new E.VAudioDelta(), new E.VResponseDone(), new E.VDrop(), new E.VGoToSleep(),
        new E.VToolCall(), new E.VToolCapture(), new E.VToolNoFrame(), new E.VToolAnswer(false),
        new E.VToolAnswer(true, EditTarget.File), new E.VToolAnswer(true, EditTarget.Field),
        new E.VToolFail(), new E.VToolRelease(), new E.VUndoStart(), new E.VUndoDone(true), new E.VUndoDone(false),
        new E.TalkButton(), new E.TalkDown(HoldSource.Chord), new E.TalkDown(HoldSource.Eyes),
        new E.TalkUp(HoldSource.Chord), new E.TalkUp(HoldSource.Eyes), new E.HoldTimeout(),
        new E.Stop(),
        new E.Approve(true), new E.Approve(false), new E.Reject(),
        new E.Apply(true, false), new E.Apply(false), new E.Apply(true, true, EditTarget.File), new E.Apply(true, true, EditTarget.Field),
        new E.ExecEnd(),
        new E.FocusStart(), new E.FocusPause(), new E.FocusResume(), new E.FocusComplete(), new E.FocusEnd(),
        new E.FocusCheckStart(), new E.FocusCheckDone(false), new E.FocusCheckDone(true), new E.NudgeBack(), new E.NudgeBreak(),
    };

    /// <summary>
    /// Short user flows that uniform random events rarely line up (an approved apply, an undo, a stale card, the F10
    /// hold, a focus check, a hover). They are only building blocks: the shrinker cuts them apart like any events.
    /// </summary>
    public static IReadOnlyList<NotchEvent[]> Flows { get; } = new[]
    {
        new NotchEvent[] { new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve(), new E.Apply() },
        new NotchEvent[] { new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(), new E.TypedReply(true, EditTarget.Field), new E.Approve(), new E.Apply() },
        new NotchEvent[] { new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve() },
        new NotchEvent[] { new E.Send(TypedRequestKind.Undo, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedUndoPlan(true), new E.Approve(), new E.Apply() },
        new NotchEvent[] { new E.Send(TypedRequestKind.Ask, TypedRequestPath.Voice), new E.TypedNoFrame(), new E.TypedHandOff() },
        new NotchEvent[] { new E.TalkButton(), new E.VToolCall(), new E.VToolCapture(), new E.VToolAnswer(true, EditTarget.File), new E.Approve(), new E.Apply() },
        new NotchEvent[] { new E.TalkButton(), new E.VUndoStart(), new E.VUndoDone(true), new E.Approve(), new E.Apply() },
        new NotchEvent[] { new E.TalkDown(HoldSource.Chord), new E.TalkButton(), new E.TalkButton(), new E.TalkUp(HoldSource.Chord) },
        new NotchEvent[] { new E.TalkDown(HoldSource.Eyes), new E.VSpeechStarted(), new E.VSpeechStopped(), new E.TalkUp(HoldSource.Eyes) },
        new NotchEvent[] { new E.FocusStart(), new E.FocusCheckStart(), new E.FocusCheckDone(true), new E.NudgeBreak(), new E.FocusResume(), new E.FocusComplete() },
        new NotchEvent[] { new E.PointerEnter(), new E.Tick(), new E.PointerLeave(), new E.Tick(), new E.Tick(), new E.Tick() },
    };

    /// <summary>A walk: up to 60 pieces, each a single event (mostly), a flow, or (rarely) Quit.</summary>
    public static Arbitrary<EventWalk> Generate()
    {
        var single = Gen.Elements(Alphabet.ToArray()).Select(e => new[] { e });
        var flow = Gen.Elements(Flows.ToArray());
        var quit = Gen.Constant(new NotchEvent[] { new E.Quit() });
        var piece = Gen.Frequency(new (int, Gen<NotchEvent[]>)[] { (85, single), (14, flow), (1, quit) });
        var walk =
            from count in Gen.Choose(1, 60)
            from pieces in Gen.ArrayOf(piece, count)
            select new EventWalk(pieces.SelectMany(p => p).ToArray());
        return Arb.From(walk, Shrink);
    }

    /// <summary>Shorter walks: drop runs of events, halves first, down to single events.</summary>
    public static IEnumerable<EventWalk> Shrink(EventWalk walk)
    {
        var events = walk.Events;
        for (var size = events.Count / 2; size >= 1; size /= 2)
        {
            for (var start = 0; start + size <= events.Count; start += size)
                yield return new EventWalk(events.Take(start).Concat(events.Skip(start + size)).ToArray());
        }
    }

    /// <summary>Every step of <paramref name="walk"/> from the initial state.</summary>
    public static IEnumerable<WalkStep> Steps(EventWalk walk)
    {
        var state = NotchLifecycleState.Initial;
        for (var i = 0; i < walk.Events.Count; i++)
        {
            var step = NotchLifecycle.Reduce(state, walk.Events[i]);
            yield return new WalkStep(i, state, walk.Events[i], step);
            state = step.State;
        }
    }

    /// <summary>Cases per property from <see cref="CasesVariable"/>: unset = 200; a bad value fails closed.</summary>
    public static int Cases()
    {
        var raw = Environment.GetEnvironmentVariable(CasesVariable);
        if (string.IsNullOrEmpty(raw))
            return 200;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var cases) || cases < 1 || cases > 1_000_000)
            throw new InvalidOperationException($"{CasesVariable}='{raw}' is not a whole number from 1 to 1000000");
        return cases;
    }

    /// <summary>
    /// Checks <paramref name="check"/> on <see cref="Cases"/> random walks; on failure FsCheck shrinks the walk and
    /// throws with the shrunk counterexample. Returns a line for the log; fails if fewer walks ran than asked.
    /// </summary>
    public static string Run(string name, Action<EventWalk> check)
    {
        var cases = Cases();
        var ran = 0;
        Action<EventWalk> body = walk =>
        {
            Interlocked.Increment(ref ran);
            check(walk);
        };
        var config = Config.QuickThrowOnFailure.WithMaxTest(cases);
        FsCheck.Check.One(config, Prop.ForAll(Generate(), body));
        if (ran < cases)
            throw new InvalidOperationException($"{name}: only {ran} of {cases} cases ran");
        return $"{name}: {cases} cases passed ({ran} walks checked)";
    }
}
