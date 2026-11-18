namespace Boo.Core.Tests;

// Living ink (picked 2026-11-12, bluey-design-assets/living-ink/LIVING_INK_SPEC.md). These tests pin behaviour the
// spec asks for (states exist, shapes stay inside their box and readable, motion is short, cancellable and skipped
// with reduced motion), not pixels.

public sealed class LivingInkSpecTests
{
    private static NotchFaceSpec D => FaceSpecFixture.Load().Face;

    [Fact]
    public void Shipped_face_is_living_ink_with_the_boards_idle_size_and_eyes()
    {
        Assert.Equal("Living ink", D.Name);
        Assert.Equal(new SizeD(156, 58), D.SizeFor(NotchFaceState.Idle));
        Assert.Equal(new SizeD(156, 58), D.SizeFor(NotchFaceState.Hover));
        Assert.Equal(32, D.Eye.Width);
        Assert.Equal(32, D.Eye.Height);
        Assert.Equal(16, D.Eye.Gap);
        Assert.InRange(D.Eye.Pupil / D.Eye.Width, 0.40, 0.50); // pupil ~45 % of the eye at rest
    }

    [Fact]
    public void Mood_sizes_follow_the_spec_wider_to_listen_taller_to_think_widest_to_speak_shorter_to_sleep()
    {
        var idle = D.SizeFor(NotchFaceState.Idle);
        var listening = D.SizeFor(NotchFaceState.Listening);
        var thinking = D.SizeFor(NotchFaceState.Thinking);
        var speaking = D.SizeFor(NotchFaceState.Responding);
        var paused = D.SizeFor(NotchFaceState.Paused);

        Assert.True(listening.Width > idle.Width);
        Assert.True(thinking.Height > idle.Height);
        Assert.True(thinking.Width < idle.Width);
        Assert.True(speaking.Width > listening.Width);
        Assert.True(paused.Height < idle.Height);
        foreach (var state in Enum.GetValues<NotchFaceState>())
            Assert.True(D.Sizes.ContainsKey(state), $"missing size for {state}");
    }

    [Fact]
    public void Hover_reveal_and_retract_keep_the_existing_product_timings()
    {
        var t = FaceSpecFixture.Load().Timings;
        Assert.Equal(240, t.RevealMs);
        Assert.Equal(900, t.RetractDelayMs);
        Assert.Equal(160, t.RetractMs);
    }
}

public sealed class InkBlotTests
{
    public static TheoryData<NotchFaceState> States()
    {
        var data = new TheoryData<NotchFaceState>();
        foreach (var s in Enum.GetValues<NotchFaceState>()) data.Add(s);
        return data;
    }

    private static NotchFaceSpec D => FaceSpecFixture.Load().Face;

    [Theory]
    [MemberData(nameof(States))]
    public void Every_state_has_a_blot_that_hangs_flush_from_the_top_and_stays_in_its_box(NotchFaceState state)
    {
        var box = D.SizeFor(state);
        var outline = LivingInk.BlotFor(state).Outline(box);

        Assert.All(outline, p =>
        {
            Assert.InRange(p.X, -0.01, box.Width + 0.01);
            Assert.InRange(p.Y, -0.01, box.Height + 0.01);
        });
        // Top edge: a straight run at y = 0 covering most of the width.
        var top = LivingInk.BlotFor(state).Points(box);
        Assert.Equal(0, top[0].Y, 6);
        Assert.Equal(0, top[1].Y, 6);
        Assert.True(top[1].X - top[0].X > box.Width * 0.8);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void The_lower_edge_is_an_irregular_blot_not_a_flat_rounded_rectangle(NotchFaceState state)
    {
        var box = D.SizeFor(state);
        var bottom = LivingInk.BlotFor(state).Outline(box).Where(p => p.Y > box.Height * 0.5).Select(p => p.Y).ToArray();
        // A rounded rectangle has a long flat bottom; the blot's lowest points vary by several DIP across the width.
        Assert.True(bottom.Max() - bottom.Where(y => y > box.Height * 0.6).Min() > 3,
            $"{state}: lower edge is too flat for an ink blot");
    }

    [Theory]
    [MemberData(nameof(States))]
    public void Both_eyes_sit_inside_the_ink_in_every_state(NotchFaceState state)
    {
        var box = D.SizeFor(state);
        var blot = LivingInk.BlotFor(state);
        var parts = LivingInk.Place(D, box, state);
        var mood = InkEyePose.For(LivingInk.MoodFor(state));
        var halfH = D.Eye.Height / 2 * mood.ScaleY;
        var halfW = D.Eye.Width / 2 * mood.ScaleX;
        foreach (var c in new[] { parts.LeftEye, parts.RightEye })
        {
            Assert.True(blot.Contains(c, box), $"{state}: eye centre outside the ink");
            Assert.True(blot.Contains(new PointD(c.X, c.Y + halfH * 0.9), box), $"{state}: eye bottom hangs off the ink");
            Assert.True(blot.Contains(new PointD(c.X - halfW * 0.9, c.Y), box), $"{state}: eye side off the ink");
            Assert.True(blot.Contains(new PointD(c.X + halfW * 0.9, c.Y), box), $"{state}: eye side off the ink");
        }
    }

    [Theory]
    [InlineData(NotchFaceState.Responding, 3)]
    [InlineData(NotchFaceState.Nudge, 2)]
    public void Caption_area_fits_its_lines_and_lies_on_the_ink(NotchFaceState state, int lines)
    {
        var box = D.SizeFor(state);
        var blot = LivingInk.BlotFor(state);
        var caption = LivingInk.Place(D, box, state).Caption;
        var actions = state == NotchFaceState.Nudge ? 30 : 0; // the nudge pills under the caption
        Assert.True(caption.Height - actions >= lines * 18, $"{state}: caption has room for fewer than {lines} lines");
        foreach (var corner in new[]
                 {
                     new PointD(caption.X, caption.Y), new PointD(caption.X + caption.Width, caption.Y),
                     new PointD(caption.X, caption.Y + caption.Height), new PointD(caption.X + caption.Width, caption.Y + caption.Height),
                 })
            Assert.True(blot.Contains(corner, box), $"{state}: caption corner {corner} is off the ink (cream text would vanish on a light desktop)");
    }

    [Fact]
    public void Moods_morph_point_by_point_between_shapes()
    {
        var half = InkBlot.Lerp(InkBlot.Idle, InkBlot.Thinking, 0.5);
        for (var i = 0; i < InkBlot.PointCount; i++)
        {
            Assert.Equal((InkBlot.Idle[i].X + InkBlot.Thinking[i].X) / 2, half[i].X, 9);
            Assert.Equal((InkBlot.Idle[i].Y + InkBlot.Thinking[i].Y) / 2, half[i].Y, 9);
        }
        Assert.Same(InkBlot.Idle, InkBlot.Lerp(InkBlot.Idle, InkBlot.Thinking, 0));
        Assert.Same(InkBlot.Thinking, InkBlot.Lerp(InkBlot.Idle, InkBlot.Thinking, 1));
    }

    [Fact]
    public void Up_to_the_board_sizes_the_blot_is_a_plain_stretch_and_larger_boxes_keep_corners_bounded()
    {
        var small = new SizeD(156, 58);
        var p = InkBlot.Map(new PointD(0.5, 0.5), small);
        Assert.Equal(78, p.X, 9);
        Assert.Equal(29, p.Y, 9);

        // A 360×142 caption blot: the lobed lower band stays as deep as a 64-high blot's (32 DIP), not 71.
        var big = new SizeD(360, 142);
        Assert.Equal(142 - 32, InkBlot.Map(new PointD(0.5, 0.5), big).Y, 9);
        Assert.Equal(142, InkBlot.Map(new PointD(0.5, 1), big).Y, 9);
    }
}

public sealed class InkMoodTests
{
    [Theory]
    [InlineData(NotchFaceState.Idle, InkMood.Idle)]
    [InlineData(NotchFaceState.Hover, InkMood.Curious)]
    [InlineData(NotchFaceState.Listening, InkMood.Listening)]
    [InlineData(NotchFaceState.Thinking, InkMood.Thinking)]
    [InlineData(NotchFaceState.Responding, InkMood.Speaking)]
    [InlineData(NotchFaceState.Nudge, InkMood.Alert)]
    [InlineData(NotchFaceState.Paused, InkMood.Sleepy)]
    [InlineData(NotchFaceState.Happy, InkMood.Happy)]
    public void Each_face_state_maps_to_its_living_ink_mood(NotchFaceState state, InkMood mood)
        => Assert.Equal(mood, LivingInk.MoodFor(state));

    [Fact]
    public void Happy_is_two_cream_arcs_with_no_pupil_or_shine()
    {
        var happy = InkEyePose.For(InkMood.Happy);
        Assert.True(happy.Happy);
        Assert.Equal(0, happy.Pupil);
        Assert.Equal(0, happy.Shine);
        Assert.Equal(0, happy.Lid);
    }

    [Fact]
    public void Alert_has_large_brick_red_pupils_and_a_taller_eye_only_for_nudge()
    {
        var alert = InkEyePose.For(InkMood.Alert);
        Assert.Equal(1, alert.Alert);
        Assert.InRange(alert.Pupil, 0.75, 0.8);
        Assert.True(alert.ScaleY > 1.1);
        foreach (var mood in Enum.GetValues<InkMood>().Where(m => m != InkMood.Alert))
            Assert.Equal(0, InkEyePose.For(mood).Alert);
    }

    [Fact]
    public void Lids_and_pupils_carry_the_mood()
    {
        Assert.Equal(1, InkEyePose.For(InkMood.Closed).Lid);
        Assert.InRange(InkEyePose.For(InkMood.Peek).Lid, 0.65, 0.75);
        Assert.InRange(InkEyePose.For(InkMood.Sleepy).Lid, 0.5, 0.65);
        Assert.True(InkEyePose.For(InkMood.Sleepy).ScaleY < 0.8);
        Assert.True(InkEyePose.For(InkMood.Listening).Pupil > InkEyePose.For(InkMood.Idle).Pupil);
        Assert.True(InkEyePose.For(InkMood.Curious).ScaleY > 1);
        Assert.True(InkEyePose.For(InkMood.Thinking).Pupil < InkEyePose.For(InkMood.Curious).Pupil);
    }

    [Theory]
    [InlineData(InkMood.Idle)]
    [InlineData(InkMood.Curious)]
    [InlineData(InkMood.Listening)]
    [InlineData(InkMood.Alert)]
    public void Look_direction_never_pushes_the_pupil_out_of_the_eye(InkMood mood)
    {
        var pose = InkEyePose.For(mood);
        Assert.True(pose.Pupil / 2 + pose.PupilTravel <= 0.5 + 1e-9);
    }

    [Fact]
    public void Extras_belong_to_their_moods_blooms_listen_drip_think_bars_speak()
    {
        Assert.True(LivingInk.HasBlooms(NotchFaceState.Listening));
        Assert.True(LivingInk.HasDrip(NotchFaceState.Thinking));
        Assert.True(LivingInk.HasThinkDots(NotchFaceState.Thinking));
        Assert.True(LivingInk.HasBars(NotchFaceState.Responding));
        Assert.Equal(4, LivingInk.BarCount);
        foreach (var state in Enum.GetValues<NotchFaceState>().Where(s => s is not NotchFaceState.Listening))
            Assert.False(LivingInk.HasBlooms(state));
        foreach (var state in Enum.GetValues<NotchFaceState>().Where(s => s is not NotchFaceState.Thinking))
            Assert.False(LivingInk.HasDrip(state));
    }

    [Fact]
    public void Speaking_bars_keep_time_with_the_voice_and_stand_still_with_reduced_motion()
    {
        for (var i = 0; i < LivingInk.BarCount; i++)
        {
            Assert.Equal(4, LivingInk.BarHeight(i, 1234, level: 0, reducedMotion: false), 9);
            var loud = Enumerable.Range(0, 50).Select(k => LivingInk.BarHeight(i, k * 37, 1, false)).ToArray();
            Assert.True(loud.Max() - loud.Min() > 1.5, "bars should move with a loud voice");
            Assert.Equal(LivingInk.BarHeight(i, 0, 1, true), LivingInk.BarHeight(i, 999, 0.3, true));
        }
    }

    [Fact]
    public void Think_is_slower_than_listen_and_every_morph_is_short()
    {
        Assert.True(LivingInk.MorphMs(NotchFaceState.Thinking) > LivingInk.MorphMs(NotchFaceState.Listening));
        Assert.InRange(LivingInk.MorphMs(NotchFaceState.Listening), 180, 240);
        foreach (var state in Enum.GetValues<NotchFaceState>())
            Assert.InRange(LivingInk.MorphMs(state), 150, 400);
    }
}

public sealed class InkMorphTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);
    private static TimeSpan Ms(double ms) => T0 + TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Morph_reaches_the_target_pose_in_its_duration()
    {
        var morph = new InkMorph(new SizeD(156, 58));
        morph.Retarget(InkBlot.Listening, InkEyePose.For(InkMood.Listening), new SizeD(200, 62), T0, 220, snap: false);

        var mid = morph.Sample(Ms(110));
        Assert.False(mid.Settled);
        Assert.InRange(mid.Size.Width, 157, 199);
        var end = morph.Sample(Ms(220));
        Assert.True(end.Settled);
        Assert.Equal(new SizeD(200, 62), end.Size);
        Assert.Same(InkBlot.Listening, end.Blot);
    }

    [Fact]
    public void A_new_state_cuts_the_running_morph_from_where_it_is_without_a_jump()
    {
        var morph = new InkMorph(new SizeD(156, 58));
        morph.Retarget(InkBlot.Listening, InkEyePose.For(InkMood.Listening), new SizeD(200, 62), T0, 220, snap: false);
        var before = morph.Sample(Ms(100));

        morph.Retarget(InkBlot.Thinking, InkEyePose.For(InkMood.Thinking), new SizeD(120, 68), Ms(100), 360, snap: false);
        var after = morph.Sample(Ms(100));

        Assert.Equal(before.Size, after.Size);
        Assert.Equal(before.Pose, after.Pose);
        Assert.Equal(new SizeD(120, 68), morph.Sample(Ms(460)).Size);
    }

    [Fact]
    public void Reduced_motion_jumps_straight_to_the_end_pose()
    {
        var morph = new InkMorph(new SizeD(156, 58));
        morph.Retarget(InkBlot.Thinking, InkEyePose.For(InkMood.Thinking), new SizeD(120, 68), T0, 360, snap: true);

        var now = morph.Sample(T0);
        Assert.True(now.Settled);
        Assert.Equal(new SizeD(120, 68), now.Size);
        Assert.Same(InkBlot.Thinking, now.Blot);
    }
}

public sealed class InkWakeUpTests
{
    private static readonly SizeD Rest = new(156, 58);
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(3);
    private static TimeSpan Ms(double ms) => T0 + TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Wake_up_runs_stain_bead_eyes_shut_peek_blink_looks_over_in_about_520_ms()
    {
        var wake = new InkWakeUp(Rest);
        wake.Start(T0, reducedMotion: false);

        var seen = new List<InkWakePhase>();
        for (var ms = 0; ms <= 600; ms += 5)
        {
            var phase = wake.At(Ms(ms)).Phase;
            if (seen.Count == 0 || seen[^1] != phase) seen.Add(phase);
        }

        Assert.Equal(new[]
        {
            InkWakePhase.Stain, InkWakePhase.Bead, InkWakePhase.EyesShut, InkWakePhase.Peek,
            InkWakePhase.Blink, InkWakePhase.LooksOver, InkWakePhase.Done,
        }, seen);
        Assert.InRange(InkWakeUp.TotalMs, 480, 560);
        Assert.Equal(180, InkWakeUp.StainToPocketMs);
        Assert.False(wake.Active);
    }

    [Fact]
    public void Stain_is_a_faint_thin_mark_without_eyes_then_eyes_appear_shut_then_open()
    {
        var wake = new InkWakeUp(Rest);
        wake.Start(T0, reducedMotion: false);

        var stain = wake.At(Ms(10));
        Assert.True(stain.Opacity < 0.5);
        Assert.False(stain.EyesVisible);
        Assert.True(stain.Size.Height < 10);
        Assert.True(stain.Size.Width > stain.Size.Height * 5);

        var shut = wake.At(Ms(250));
        Assert.True(shut.EyesVisible);
        Assert.True(shut.Lid > 0.95);

        var blink = wake.At(Ms(420));
        Assert.True(blink.EyeScaleY < 0.3, "the hard blink squashes the eyes into thin lines");

        var end = wake.At(Ms(519));
        Assert.True(end.Lid < 0.05);
        Assert.Equal(Rest.Width, end.Size.Width, 3);
    }

    [Fact]
    public void Wake_up_is_cancellable_and_reduced_motion_never_plays_it()
    {
        var wake = new InkWakeUp(Rest);
        wake.Start(T0, reducedMotion: false);
        Assert.True(wake.Active);
        wake.Cancel();
        Assert.False(wake.Active);
        Assert.Equal(InkWakePhase.Done, wake.At(Ms(50)).Phase);

        var reduced = new InkWakeUp(Rest);
        reduced.Start(T0, reducedMotion: true);
        Assert.False(reduced.Active);
        var frame = reduced.At(T0);
        Assert.Equal(InkWakePhase.Done, frame.Phase);
        Assert.Equal(Rest, frame.Size);
        Assert.Equal(1, frame.Opacity);
    }
}

public sealed class InkCelebrateTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(5);
    private static TimeSpan Ms(double ms) => T0 + TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Focus_done_squashes_shows_splatters_then_settles_within_about_a_second()
    {
        var c = new InkCelebrate();
        c.Start(T0, reducedMotion: false);

        var squash = c.At(Ms(200));
        Assert.True(squash.SquashY < 0.95 && squash.SquashX > 1.05, "squash");
        Assert.True(squash.SplatterOpacity > 0.9 && squash.SplatterScale > 0.9, "splatters visible");

        var fading = c.At(Ms(800));
        Assert.True(fading.SplatterOpacity < squash.SplatterOpacity);

        var settled = c.At(Ms(InkCelebrate.DurationMs));
        Assert.True(settled.Done);
        Assert.Equal(0, settled.SplatterOpacity);
        Assert.Equal(1, settled.SquashX);
        Assert.Equal(1, settled.SquashY);
        Assert.InRange(InkCelebrate.DurationMs, 900, 1200);
        Assert.False(c.Active);
    }

    [Fact]
    public void Reduced_motion_skips_the_celebration_and_its_splatters()
    {
        var c = new InkCelebrate();
        c.Start(T0, reducedMotion: true);
        var frame = c.At(Ms(150));
        Assert.True(frame.Done);
        Assert.Equal(0, frame.SplatterOpacity);
        Assert.Equal(1, frame.SquashY);
    }

    [Fact]
    public void A_new_state_cuts_the_celebration()
    {
        var c = new InkCelebrate();
        c.Start(T0, reducedMotion: false);
        c.Cancel();
        Assert.True(c.At(Ms(100)).Done);
    }
}

public sealed class InkBlinkTests
{
    [Fact]
    public void Soft_blink_every_4_to_6_s_down_80_hold_60_up_80()
    {
        var blink = new InkBlink(new Random(3));
        blink.Restart(TimeSpan.Zero);
        var starts = new List<double>();
        var wasShut = false;
        for (var ms = 0.0; ms < 60_000; ms += 2)
        {
            var lid = blink.LidAt(TimeSpan.FromMilliseconds(ms), sleepy: false, reducedMotion: false);
            Assert.InRange(lid, 0, 1);
            if (lid > 0 && !wasShut) starts.Add(ms);
            wasShut = lid > 0;
        }
        Assert.InRange(starts.Count, 9, 15);
        Assert.InRange(starts[0], 4000, 6002);
        for (var i = 1; i < starts.Count; i++)
            Assert.InRange(starts[i] - starts[i - 1], 3998, 6002);

        // Shape of one blink (relative to its start).
        var b = new InkBlink(new Random(3));
        b.Restart(TimeSpan.Zero);
        var first = b.NextBlink;
        Assert.Equal(0.5, b.LidAt(first + TimeSpan.FromMilliseconds(40), false, false), 6);
        Assert.Equal(1, b.LidAt(first + TimeSpan.FromMilliseconds(110), false, false), 6);
        Assert.Equal(0.5, b.LidAt(first + TimeSpan.FromMilliseconds(180), false, false), 6);
        Assert.Equal(0, b.LidAt(first + TimeSpan.FromMilliseconds(221), false, false), 6);
    }

    [Fact]
    public void Sleepy_blinks_are_slower()
    {
        var b = new InkBlink(new Random(9));
        b.Restart(TimeSpan.Zero);
        var first = b.NextBlink;
        Assert.True(b.LidAt(first + TimeSpan.FromMilliseconds(260), sleepy: true, reducedMotion: false) > 0,
            "a sleepy blink is still closed after a normal blink has finished");
    }

    [Fact]
    public void Reduced_motion_has_no_lid_animation()
    {
        var b = new InkBlink(new Random(1));
        b.Restart(TimeSpan.Zero);
        for (var ms = 0.0; ms < 20_000; ms += 5)
            Assert.Equal(0, b.LidAt(TimeSpan.FromMilliseconds(ms), false, reducedMotion: true));
    }
}

public sealed class LivingInkPaletteTests
{
    [Fact]
    public void Ink_and_cream_are_readable_together()
    {
        LivingInkPalette.Appearance.Validate(); // throws below 3:1 contrast
        Assert.Equal("#0C0A09", LivingInkPalette.Ink);
        Assert.Equal("#F7F4EE", LivingInkPalette.Cream);
        Assert.Equal("#9B1B1B", LivingInkPalette.Alert);
    }

    [Fact]
    public void Untouched_default_draws_living_ink_and_user_colours_are_kept()
    {
        Assert.Equal(LivingInkPalette.Appearance, LivingInkPalette.Resolve(new NotchAppearance()));
        Assert.Equal(LivingInkPalette.Appearance, LivingInkPalette.Resolve(null));
        var custom = new NotchAppearance("#123456", "#FFEEDD");
        Assert.Same(custom, LivingInkPalette.Resolve(custom));
    }

    [Fact]
    public void Theme_never_turns_the_blot_white_only_a_quiet_rim_is_added_in_dark_mode()
    {
        Assert.Null(LivingInkPalette.RimFor(darkTheme: false));
        var rim = LivingInkPalette.RimFor(darkTheme: true);
        Assert.NotNull(rim);
        Assert.StartsWith("#2E", rim); // 18 % alpha: a hint, not an outline
        // The palette has no theme-dependent body or eye colour at all.
        Assert.Equal(LivingInkPalette.Ink, LivingInkPalette.Resolve(new NotchAppearance()).BodyColor);
        Assert.Equal(LivingInkPalette.Cream, LivingInkPalette.Resolve(new NotchAppearance()).EyeColor);
    }
}
