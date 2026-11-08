using Boo.Core;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Boo.Core.Tests;

public sealed class ControlObservationTests
{
    [Fact]
    public void Create_publishes_an_exact_read_only_copy_of_an_eligible_candidate()
    {
        var context = ValidContext();
        var runtimeId = new List<int> { 12, -4, 0 };
        var candidate = Candidate(
            automationId: "  button-text  ",
            name: "  Button text  ",
            controlType: "  Edit  ",
            value: "",
            bounds: new RectD(-90, -40, 50, 40),
            runtimeId: runtimeId,
            canInvoke: true,
            canSetValue: false);
        var candidates = new[] { candidate };

        var observation = ControlObservation.Create(context, candidates);

        runtimeId[0] = 999;
        candidates[0] = Candidate("replacement", "replacement", "Text", "changed", new RectD(-80, -30, 10, 10));

        Assert.Equal(context, observation.Context);
        var actual = Assert.Single(observation.Controls);
        Assert.Equal("  button-text  ", actual.AutomationId);
        Assert.Equal("  Button text  ", actual.Name);
        Assert.Equal("  Edit  ", actual.ControlType);
        Assert.Equal("", actual.Value);
        Assert.Equal(new RectD(-90, -40, 50, 40), actual.Bounds);
        Assert.Equal(new[] { 12, -4, 0 }, actual.RuntimeId);
        Assert.True(actual.CanInvoke);
        Assert.False(actual.CanSetValue);

        if (observation.Controls is IList<ObservedControl> controls)
        {
            Assert.True(controls.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => controls[0] = actual with { Name = "mutated" });
        }
        else if (observation.Controls is ICollection<ObservedControl> collection)
        {
            Assert.True(collection.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => collection.Add(actual with { Name = "mutated" }));
        }

        Assert.Equal("  Button text  ", observation.Controls[0].Name);
    }

    [Fact]
    public void Values_up_to_the_limit_are_preserved_and_longer_values_become_unavailable()
    {
        var exactLimit = new string('v', 1024);
        var observation = ControlObservation.Create(
            ValidContext(),
            new[]
            {
                Candidate("empty-value", "", "Text", ""),
                Candidate("at-limit", "", "Text", exactLimit),
                Candidate("over-limit", "", "Text", new string('x', 1025))
            });

        Assert.Equal("", observation.Controls[0].Value);
        Assert.Equal(exactLimit, observation.Controls[1].Value);
        Assert.Null(observation.Controls[2].Value);
    }

    [Fact]
    public void Publishes_at_most_128_eligible_candidates_in_input_order()
    {
        var candidates = Enumerable.Range(1, 200)
            .Select(index => Candidate($"control-{index}", $"Name {index}", "Button", $"value-{index}"))
            .ToArray();

        var observation = ControlObservation.Create(ValidContext(), candidates);

        Assert.Equal(128, observation.Controls.Count);
        Assert.Equal("control-1", observation.Controls[0].AutomationId);
        Assert.Equal("control-128", observation.Controls[^1].AutomationId);
    }

    [Fact]
    public void Duplicate_ids_are_removed_before_publication_limit_and_only_eligible_duplicates_count()
    {
        var first512 = new List<ControlCandidate>
        {
            Candidate("duplicate", "first", "Button", "first"),
        };
        first512.AddRange(Enumerable.Range(1, 128)
            .Select(index => Candidate($"published-{index}", $"Name {index}", "Button", $"value-{index}")));
        first512.Add(Candidate("duplicate", "second", "Button", "second"));
        first512.AddRange(Enumerable.Range(130, 382)
            .Select(index => Candidate($"filler-{index}", $"Filler {index}", "Text", null)));

        Assert.Equal(512, first512.Count);
        var observation = ControlObservation.Create(
            ValidContext(),
            new GuardedCandidateSequence(first512));

        Assert.Equal(128, observation.Controls.Count);
        Assert.Equal("published-1", observation.Controls[0].AutomationId);
        Assert.Equal("published-128", observation.Controls[^1].AutomationId);
        Assert.DoesNotContain(observation.Controls, control => control.AutomationId == "duplicate");
    }

    [Fact]
    public void Duplicate_matching_is_ordinal_and_ineligible_duplicates_do_not_suppress_valid_controls()
    {
        var observation = ControlObservation.Create(
            ValidContext(),
            new[]
            {
                Candidate("same-id", "disabled", "Button", null, isEnabled: false),
                Candidate("same-id", "enabled", "Button", null),
                Candidate("CaseSensitive", "upper", "Text", null),
                Candidate("casesensitive", "lower", "Text", null)
            });

        Assert.Equal(
            new[] { "same-id", "CaseSensitive", "casesensitive" },
            observation.Controls.Select(control => control.AutomationId));
    }

    [Fact]
    public void Disabled_offscreen_and_password_controls_are_omitted()
    {
        var observation = ControlObservation.Create(
            ValidContext(),
            new[]
            {
                Candidate("enabled", "enabled", "Button", null),
                Candidate("disabled", "disabled", "Button", null, isEnabled: false),
                Candidate("offscreen", "offscreen", "Button", null, isOffscreen: true),
                Candidate("password", "password", "Edit", null, isPassword: true)
            });

        var actual = Assert.Single(observation.Controls);
        Assert.Equal("enabled", actual.AutomationId);
    }

    [Fact]
    public void Negative_display_origins_and_a_target_crossing_the_display_boundary_are_supported()
    {
        var context = ValidContext(
            display: new DisplayInfo("display", "Display", new RectD(-100, -100, 500, 400), 1),
            target: new WindowIdentity(42, 7, "fixture", new RectD(-150, -50, 300, 200)));

        var observation = ControlObservation.Create(
            context,
            new[] { Candidate("intersection", "inside both rectangles", "Button", null, new RectD(-90, -40, 50, 40)) });

        var actual = Assert.Single(observation.Controls);
        Assert.Equal(new RectD(-90, -40, 50, 40), actual.Bounds);
    }

    [Fact]
    public void Candidate_bounds_must_be_finite_positive_and_fully_inside_display_and_target()
    {
        var valid = Candidate("valid", "valid", "Text", null, new RectD(-90, -40, 50, 40));
        var observation = ControlObservation.Create(
            ValidContext(),
            new[]
            {
                valid,
                Candidate("zero-width", "invalid", "Text", null, new RectD(-90, -40, 0, 10)),
                Candidate("negative-height", "invalid", "Text", null, new RectD(-90, -40, 10, -1)),
                Candidate("nan-origin", "invalid", "Text", null, new RectD(double.NaN, -40, 10, 10)),
                Candidate("infinite-size", "invalid", "Text", null, new RectD(-90, -40, double.PositiveInfinity, 10)),
                Candidate("overflowing-edge", "invalid", "Text", null, new RectD(double.MaxValue, 0, double.MaxValue, 1)),
                Candidate("outside-display", "invalid", "Text", null, new RectD(390, -40, 20, 10)),
                Candidate("outside-target", "invalid", "Text", null, new RectD(151, -40, 20, 10)),
                Candidate("straddles-display", "invalid", "Text", null, new RectD(-110, -40, 20, 10)),
                Candidate("straddles-target", "invalid", "Text", null, new RectD(140, -40, 20, 10))
            });

        var actual = Assert.Single(observation.Controls);
        Assert.Equal("valid", actual.AutomationId);
    }

    [Fact]
    public void Selector_and_name_limits_reject_invalid_candidates_without_normalizing_valid_text()
    {
        var observation = ControlObservation.Create(
            ValidContext(),
            new[]
            {
                Candidate("  exact-id  ", "  exact name  ", "  Button  ", null),
                Candidate("empty-name", "", "Text", null),
                Candidate("   ", "invalid", "Text", null),
                Candidate(new string('i', 257), "invalid", "Text", null),
                Candidate("null-name", null!, "Text", null),
                Candidate("long-name", new string('n', 513), "Text", null),
                Candidate("null-role", "invalid", null!, null),
                Candidate("blank-role", "invalid", "   ", null),
                Candidate("long-role", "invalid", new string('r', 129), null)
            });

        Assert.Equal(new[] { "  exact-id  ", "empty-name" }, observation.Controls.Select(control => control.AutomationId));
        Assert.Equal("  exact name  ", observation.Controls[0].Name);
        Assert.Equal("  Button  ", observation.Controls[0].ControlType);
        Assert.Equal("", observation.Controls[1].Name);
    }

    [Fact]
    public void Exact_selector_name_role_and_runtime_id_limits_are_accepted()
    {
        var automationId = new string('i', 256);
        var name = new string('n', 512);
        var controlType = new string('r', 128);
        var runtimeId = Enumerable.Range(1, 64).ToArray();

        var observation = ControlObservation.Create(
            ValidContext(),
            new[] { Candidate(automationId, name, controlType, "value", runtimeId: runtimeId) });

        var actual = Assert.Single(observation.Controls);
        Assert.Equal(automationId, actual.AutomationId);
        Assert.Equal(name, actual.Name);
        Assert.Equal(controlType, actual.ControlType);
        Assert.Equal(runtimeId, actual.RuntimeId);
    }

    [Fact]
    public void Runtime_ids_must_contain_between_one_and_64_integers()
    {
        var observation = ControlObservation.Create(
            ValidContext(),
            new[]
            {
                Candidate("valid", "valid", "Text", null, runtimeId: new[] { 1 }),
                Candidate("empty", "invalid", "Text", null, runtimeId: Array.Empty<int>()),
                Candidate("too-many", "invalid", "Text", null, runtimeId: Enumerable.Range(1, 65).ToArray()),
                new ControlCandidate(
                    "null-runtime",
                    "invalid",
                    "Text",
                    null,
                    new RectD(-90, -40, 50, 40),
                    null!,
                    IsEnabled: true,
                    IsOffscreen: false,
                    IsPassword: false,
                    CanInvoke: false,
                    CanSetValue: false)
            });

        var actual = Assert.Single(observation.Controls);
        Assert.Equal("valid", actual.AutomationId);
        Assert.Equal(new[] { 1 }, actual.RuntimeId);
    }

    [Fact]
    public void Null_context_and_candidate_sequence_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ControlObservation.Create(null!, Array.Empty<ControlCandidate>()));
        Assert.Throws<ArgumentNullException>(() => ControlObservation.Create(ValidContext(), null!));
    }

    [Theory]
    [InlineData("display-id")]
    [InlineData("display-scale-zero")]
    [InlineData("display-scale-nan")]
    [InlineData("display-bounds")]
    [InlineData("display-right-edge")]
    [InlineData("target-process")]
    [InlineData("target-handle")]
    [InlineData("target-process-id")]
    [InlineData("target-bounds")]
    [InlineData("target-bottom-edge")]
    [InlineData("frame-width")]
    [InlineData("frame-height")]
    public void Invalid_context_geometry_and_identity_are_rejected(string invalidPart)
    {
        var context = invalidPart switch
        {
            "display-id" => ValidContext(display: new DisplayInfo("  ", "Display", new RectD(-100, -100, 500, 400), 1)),
            "display-scale-zero" => ValidContext(display: new DisplayInfo("display", "Display", new RectD(-100, -100, 500, 400), 0)),
            "display-scale-nan" => ValidContext(display: new DisplayInfo("display", "Display", new RectD(-100, -100, 500, 400), double.NaN)),
            "display-bounds" => ValidContext(display: new DisplayInfo("display", "Display", new RectD(-100, -100, 0, 400), 1)),
            "display-right-edge" => ValidContext(display: new DisplayInfo("display", "Display", new RectD(double.MaxValue, 0, double.MaxValue, 1), 1)),
            "target-process" => ValidContext(target: new WindowIdentity(42, 7, "\t", new RectD(-150, -50, 300, 200))),
            "target-handle" => ValidContext(target: new WindowIdentity(0, 7, "fixture", new RectD(-150, -50, 300, 200))),
            "target-process-id" => ValidContext(target: new WindowIdentity(42, 0, "fixture", new RectD(-150, -50, 300, 200))),
            "target-bounds" => ValidContext(target: new WindowIdentity(42, 7, "fixture", new RectD(-150, -50, -1, 200))),
            "target-bottom-edge" => ValidContext(target: new WindowIdentity(42, 7, "fixture", new RectD(0, double.MaxValue, 1, double.MaxValue))),
            "frame-width" => ValidContext(frameWidth: 0),
            "frame-height" => ValidContext(frameHeight: -1),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidPart))
        };

        Assert.Throws<ArgumentException>(() => ControlObservation.Create(context, Array.Empty<ControlCandidate>()));
    }

    private static ScreenContext ValidContext(
        DisplayInfo? display = null,
        WindowIdentity? target = null,
        int frameWidth = 500,
        int frameHeight = 400)
    {
        return new ScreenContext(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Generation: 17,
            display ?? new DisplayInfo("display", "Display", new RectD(-100, -100, 500, 400), 1),
            frameWidth,
            frameHeight,
            target ?? new WindowIdentity(42, 7, "fixture", new RectD(-150, -50, 300, 200)),
            new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    }

    private static ControlCandidate Candidate(
        string automationId,
        string name,
        string controlType,
        string? value,
        RectD? bounds = null,
        IReadOnlyList<int>? runtimeId = null,
        bool isEnabled = true,
        bool isOffscreen = false,
        bool isPassword = false,
        bool canInvoke = false,
        bool canSetValue = false)
    {
        return new ControlCandidate(
            automationId,
            name,
            controlType,
            value,
            bounds ?? new RectD(-90, -40, 50, 40),
            runtimeId ?? new[] { 1, 2, 3 },
            isEnabled,
            isOffscreen,
            isPassword,
            canInvoke,
            canSetValue);
    }

    private sealed class GuardedCandidateSequence : IEnumerable<ControlCandidate>
    {
        private readonly IReadOnlyList<ControlCandidate> _first512;

        public GuardedCandidateSequence(IReadOnlyList<ControlCandidate> first512)
        {
            _first512 = first512;
        }

        public IEnumerator<ControlCandidate> GetEnumerator() => new Enumerator(_first512);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<ControlCandidate>
        {
            private readonly IReadOnlyList<ControlCandidate> _items;
            private int _index = -1;

            public Enumerator(IReadOnlyList<ControlCandidate> items)
            {
                _items = items;
            }

            public ControlCandidate Current => _items[_index];

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                _index++;
                if (_index >= _items.Count)
                {
                    throw new InvalidOperationException("The candidate source was enumerated beyond the input limit.");
                }

                return true;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
