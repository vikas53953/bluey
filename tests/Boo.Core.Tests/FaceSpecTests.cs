using System.Text;
using System.Text.Json.Nodes;
using Boo.Core;

namespace Boo.Core.Tests;

// FROZEN acceptance tests for the face lane (plan U1/U2). Builders must not edit, weaken, skip or delete.
internal static class FaceSpecFixture
{
    public static string SpecPath => Path.Combine(AppContext.BaseDirectory, "Spec", "boo-notch-spec.json");

    public static string Json => File.ReadAllText(SpecPath);

    public static Stream Open() => File.OpenRead(SpecPath);

    public static Stream FromString(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    public static NotchSpec Load() { using var s = Open(); return NotchSpec.Load(s); }

    public static Stream Mutated(Action<JsonNode> mutate)
    {
        var node = JsonNode.Parse(Json)!;
        mutate(node);
        return FromString(node.ToJsonString());
    }

    // Shipped timings, written out literally so tests do not depend on the parser under test.
    public static NotchTimings Timings => new(
        RevealMs: 240, RetractDelayMs: 900, RetractMs: 160, ResizeMs: 280,
        BlinkCloseMs: 70, BlinkOpenMs: 90, BlinkEveryMinMs: 2800, BlinkEveryMaxMs: 6000,
        DoubleBlinkChance: 0.15, GazeStiffness: 260, GazeDamping: 0.82, CaptionCharMs: 28);
}

public sealed class FaceSpecTests
{
    public static TheoryData<NotchFaceState, double, double> FaceBSizes => new()
    {
        { NotchFaceState.Hidden, 84, 36 },
        { NotchFaceState.Hover, 84, 36 },
        { NotchFaceState.Idle, 84, 36 },
        { NotchFaceState.Listening, 172, 36 },
        { NotchFaceState.Thinking, 128, 36 },
        { NotchFaceState.Responding, 348, 94 },
        { NotchFaceState.Capture, 148, 36 },
        { NotchFaceState.Nudge, 348, 108 },
        { NotchFaceState.Paused, 120, 36 },
        { NotchFaceState.Happy, 96, 36 },
    };

    [Fact]
    public void Shipped_spec_chooses_face_B_button_eyes()
    {
        var spec = FaceSpecFixture.Load();

        Assert.Equal("B", spec.ChosenFaceKey);
        Assert.Equal("B", spec.Face.Key);
        Assert.Equal("Button eyes", spec.Face.Name);
        Assert.Equal(new[] { "A", "B", "C" }, spec.FaceKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(FaceBSizes))]
    public void Face_B_size_per_state_matches_spec(NotchFaceState state, double width, double height)
    {
        var spec = FaceSpecFixture.Load();

        Assert.Equal(new SizeD(width, height), spec.Face.SizeFor(state));
        Assert.Equal(new SizeD(width, height), spec.Face.Sizes[state]);
    }

    [Fact]
    public void Face_B_defines_a_size_for_every_state()
    {
        var spec = FaceSpecFixture.Load();

        foreach (var state in Enum.GetValues<NotchFaceState>())
            Assert.True(spec.Face.Sizes.ContainsKey(state), $"missing size for {state}");
    }

    [Fact]
    public void Face_B_eye_parameters_match_spec()
    {
        var eye = FaceSpecFixture.Load().Face.Eye;

        Assert.Equal("button", eye.Kind);
        Assert.Equal(16, eye.Width);
        Assert.Equal(16, eye.Height);
        Assert.Equal(7, eye.Pupil);
        Assert.Equal(2.5, eye.Glint);
        Assert.Equal(7, eye.Gap);
        Assert.Equal(3.5, eye.GazeX);
        Assert.Equal(3.5, eye.GazeY);
    }

    [Fact]
    public void Face_B_shape_is_pocket_with_10px_shoulders_growing_sideways()
    {
        var shape = FaceSpecFixture.Load().Face.Shape;

        Assert.Equal(10, shape.Shoulder);
        Assert.Equal(18, shape.BottomRadius);
        Assert.Equal(NotchGrowDirection.Sideways, shape.Grow);
    }

    [Fact]
    public void Hover_strip_is_220_by_8()
    {
        Assert.Equal(new SizeD(220, 8), FaceSpecFixture.Load().HoverStrip);
    }

    [Fact]
    public void All_timings_match_spec()
    {
        Assert.Equal(FaceSpecFixture.Timings, FaceSpecFixture.Load().Timings);
    }

    [Fact]
    public void Face_A_and_C_are_selectable_by_key_without_code_changes()
    {
        var spec = FaceSpecFixture.Load();

        var a = spec.WithChosenFace("A");
        Assert.Equal("A", a.ChosenFaceKey);
        Assert.Equal("pebble", a.Face.Eye.Kind);
        Assert.Equal(new SizeD(96, 32), a.Face.SizeFor(NotchFaceState.Idle));
        Assert.Equal(new SizeD(360, 90), a.Face.SizeFor(NotchFaceState.Responding));
        Assert.Equal(3, a.Face.Eye.GazeX);
        Assert.Equal(2, a.Face.Eye.GazeY);

        var c = spec.GetFace("C");
        Assert.Equal("Peek", c.Name);
        Assert.Equal(NotchGrowDirection.Down, c.Shape.Grow);
        Assert.Equal(12, c.Shape.BottomRadius);
        Assert.Equal(new SizeD(104, 48), c.SizeFor(NotchFaceState.Listening));

        // Original is unchanged.
        Assert.Equal("B", spec.ChosenFaceKey);
    }

    [Fact]
    public void Chosen_face_key_in_json_drives_the_face_in_use()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["chosen"]!["face"] = "C");

        var spec = NotchSpec.Load(stream);

        Assert.Equal("C", spec.ChosenFaceKey);
        Assert.Equal(new SizeD(104, 26), spec.Face.SizeFor(NotchFaceState.Idle));
        Assert.Equal(4, spec.Face.Eye.GazeX);
        Assert.Equal(1.5, spec.Face.Eye.GazeY);
    }

    [Fact]
    public void Faces_without_pupil_or_glint_report_zero()
    {
        var a = FaceSpecFixture.Load().GetFace("A");

        Assert.Equal(0, a.Eye.Pupil);
        Assert.Equal(0, a.Eye.Glint);
    }

    [Fact]
    public void Unknown_face_key_throws_spec_exception()
    {
        var spec = FaceSpecFixture.Load();

        Assert.Throws<NotchSpecException>(() => spec.GetFace("Z"));
        Assert.Throws<NotchSpecException>(() => spec.WithChosenFace("Z"));
    }

    public static TheoryData<string> InvalidSpecs => new()
    {
        "",
        "not json",
        "[]",
        "{}",
        // chosen face not defined
        """{"directions":{},"hoverStrip":[220,8],"timings":{},"chosen":{"face":"B"}}""",
    };

    [Theory]
    [MemberData(nameof(InvalidSpecs))]
    public void Load_throws_clear_spec_exception_on_invalid_content(string json)
    {
        using var stream = FaceSpecFixture.FromString(json);

        var ex = Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Load_throws_when_a_state_size_is_missing()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["directions"]!["B"]!["sizes"]!.AsObject().Remove("capture"));

        var ex = Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
        Assert.Contains("capture", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_throws_when_a_size_is_not_positive()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["directions"]!["B"]!["sizes"]!["idle"] = new JsonArray(0, 36));

        Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
    }

    [Fact]
    public void Load_throws_when_a_timing_is_missing()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["timings"]!.AsObject().Remove("retractDelayMs"));

        Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
    }

    [Fact]
    public void Load_throws_on_null_stream()
    {
        Assert.Throws<ArgumentNullException>(() => NotchSpec.Load(null!));
    }

    [Fact]
    public void Default_equals_shipped_face_B()
    {
        var shipped = FaceSpecFixture.Load();
        var fallback = NotchSpec.Default;

        Assert.Equal("B", fallback.ChosenFaceKey);
        Assert.Equal(shipped.Face.Name, fallback.Face.Name);
        Assert.Equal(shipped.Face.Shape, fallback.Face.Shape);
        Assert.Equal(shipped.Face.Eye, fallback.Face.Eye);
        foreach (var state in Enum.GetValues<NotchFaceState>())
            Assert.Equal(shipped.Face.SizeFor(state), fallback.Face.SizeFor(state));
        Assert.Equal(shipped.HoverStrip, fallback.HoverStrip);
        Assert.Equal(shipped.Timings, fallback.Timings);
    }

    [Fact]
    public void LoadOrDefault_falls_back_to_face_B_on_missing_or_invalid_file()
    {
        var fromNull = NotchSpec.LoadOrDefault(null);
        using var garbage = FaceSpecFixture.FromString("{ broken");
        var fromGarbage = NotchSpec.LoadOrDefault(garbage);

        foreach (var spec in new[] { fromNull, fromGarbage })
        {
            Assert.Equal("B", spec.ChosenFaceKey);
            Assert.Equal(new SizeD(84, 36), spec.Face.SizeFor(NotchFaceState.Idle));
            Assert.Equal(new SizeD(348, 94), spec.Face.SizeFor(NotchFaceState.Responding));
            Assert.Equal(FaceSpecFixture.Timings, spec.Timings);
        }
    }

    [Fact]
    public void LoadOrDefault_uses_valid_file_content()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["chosen"]!["face"] = "A");

        Assert.Equal("A", NotchSpec.LoadOrDefault(stream).ChosenFaceKey);
    }
}
