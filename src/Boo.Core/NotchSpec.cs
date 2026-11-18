using System.Globalization;
using System.Text.Json;

namespace Boo.Core;

// Face lane (U1/U2) contract: spec records and the JSON loader for docs/design/boo-notch-spec.json.

/// <summary>Logical (DIP) size. Multiply by the display scale for physical pixels.</summary>
public readonly record struct SizeD(double Width, double Height);

/// <summary>2-D point or offset (physical px unless stated otherwise).</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>Every visual state of the notch face. Each state has exactly one size in the spec.</summary>
public enum NotchFaceState { Hidden, Hover, Idle, Listening, Thinking, Responding, Capture, Nudge, Paused, Happy }

public enum NotchGrowDirection { Sideways, Down }

public sealed record NotchShapeSpec(double BottomRadius, double Shoulder, NotchGrowDirection Grow, bool Centered);

/// <summary>Eye geometry in DIP. Pupil/Glint are 0 for faces that do not define them.</summary>
public sealed record NotchEyeSpec(string Kind, double Width, double Height, double Pupil, double Glint, double Gap, double GazeX, double GazeY);

public sealed record NotchFaceSpec(string Key, string Name, NotchShapeSpec Shape, NotchEyeSpec Eye, IReadOnlyDictionary<NotchFaceState, SizeD> Sizes)
{
    /// <summary>Spec size (DIP) for a state. Throws KeyNotFoundException if the state is missing.</summary>
    public SizeD SizeFor(NotchFaceState state) => Sizes[state];
}

/// <summary>All timings from spec "timings" (milliseconds unless named otherwise).</summary>
public sealed record NotchTimings(
    int RevealMs,
    int RetractDelayMs,
    int RetractMs,
    int ResizeMs,
    int BlinkCloseMs,
    int BlinkOpenMs,
    int BlinkEveryMinMs,
    int BlinkEveryMaxMs,
    double DoubleBlinkChance,
    double GazeStiffness,
    double GazeDamping,
    int CaptionCharMs);

public sealed class NotchSpecException : Exception
{
    public NotchSpecException(string message) : base(message) { }
    public NotchSpecException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Parsed docs/design/boo-notch-spec.json. Missing or invalid input:
/// <see cref="Load"/> throws <see cref="NotchSpecException"/>;
/// <see cref="LoadOrDefault"/> returns <see cref="Default"/> (values identical to face B of the shipped spec).
/// </summary>
public sealed class NotchSpec
{
    private readonly IReadOnlyDictionary<string, NotchFaceSpec> _faces;

    private NotchSpec(IReadOnlyDictionary<string, NotchFaceSpec> faces, string chosen, SizeD hoverStrip, NotchTimings timings, NotchMotionTimings motion)
    {
        _faces = faces;
        ChosenFaceKey = chosen;
        HoverStrip = hoverStrip;
        Timings = timings;
        Motion = motion;
    }

    private static readonly Lazy<NotchSpec> DefaultSpec = new(BuildDefault);

    /// <summary>Built-in fallback: chosen face "B" only, hover strip 220x8, shipped timings.</summary>
    public static NotchSpec Default => DefaultSpec.Value;

    /// <summary>Parses the spec JSON. Throws NotchSpecException (never JsonException/NullReference) on any malformed, incomplete or inconsistent input.</summary>
    public static NotchSpec Load(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new NotchSpecException("Notch spec is not valid JSON: " + ex.Message, ex);
        }

        using (doc)
        {
            try
            {
                return Parse(doc.RootElement);
            }
            catch (NotchSpecException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException or OverflowException)
            {
                throw new NotchSpecException("Notch spec is malformed: " + ex.Message, ex);
            }
        }
    }

    /// <summary>Load, or <see cref="Default"/> when the stream is null or the content is invalid.</summary>
    public static NotchSpec LoadOrDefault(Stream? json)
    {
        if (json is null) return Default;
        try
        {
            return Load(json);
        }
        catch (NotchSpecException)
        {
            return Default;
        }
        catch (IOException)
        {
            return Default;
        }
    }

    /// <summary>Key of the face in use ("chosen.face" in the JSON).</summary>
    public string ChosenFaceKey { get; }

    /// <summary>The face in use (directions[ChosenFaceKey]).</summary>
    public NotchFaceSpec Face => _faces[ChosenFaceKey];

    /// <summary>All face keys defined under "directions".</summary>
    public IReadOnlyCollection<string> FaceKeys => _faces.Keys.ToArray();

    /// <summary>Face by key (case-sensitive). Throws NotchSpecException for an unknown key.</summary>
    public NotchFaceSpec GetFace(string key)
    {
        if (key is not null && _faces.TryGetValue(key, out var face)) return face;
        throw new NotchSpecException($"Notch spec has no face '{key}'. Known faces: {string.Join(", ", _faces.Keys)}.");
    }

    /// <summary>Copy of this spec with a different chosen face. Throws NotchSpecException for an unknown key.</summary>
    public NotchSpec WithChosenFace(string key)
    {
        var face = GetFace(key);
        return new NotchSpec(_faces, face.Key, HoverStrip, Timings, Motion);
    }

    /// <summary>Invisible hover strip (DIP), spec "hoverStrip".</summary>
    public SizeD HoverStrip { get; }

    public NotchTimings Timings { get; }

    /// <summary>Remaining spec timings (caption, wander, saccades, shake, indicators, draw stroke, app open).</summary>
    public NotchMotionTimings Motion { get; }

    private static readonly (string Json, NotchFaceState State)[] StateKeys =
    {
        ("hidden", NotchFaceState.Hidden), ("hover", NotchFaceState.Hover), ("idle", NotchFaceState.Idle),
        ("listening", NotchFaceState.Listening), ("thinking", NotchFaceState.Thinking), ("responding", NotchFaceState.Responding),
        ("capture", NotchFaceState.Capture), ("nudge", NotchFaceState.Nudge), ("paused", NotchFaceState.Paused),
        ("happy", NotchFaceState.Happy),
    };

    private static NotchSpec Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new NotchSpecException("Notch spec root must be a JSON object.");

        var directions = Obj(root, "directions", "spec");
        var faces = new Dictionary<string, NotchFaceSpec>(StringComparer.Ordinal);
        foreach (var prop in directions.EnumerateObject())
            faces[prop.Name] = ParseFace(prop.Name, prop.Value);

        var hoverStrip = Size(Req(root, "hoverStrip", "spec"), "hoverStrip");
        var timingsEl = Obj(root, "timings", "spec");
        var timings = ParseTimings(timingsEl);
        var motion = ParseMotion(timingsEl);

        var chosen = Obj(root, "chosen", "spec");
        var chosenKey = Str(chosen, "face", "chosen");
        if (!faces.ContainsKey(chosenKey))
            throw new NotchSpecException($"chosen.face '{chosenKey}' is not defined under directions.");

        return new NotchSpec(faces, chosenKey, hoverStrip, timings, motion);
    }

    private static NotchFaceSpec ParseFace(string key, JsonElement el)
    {
        var where = "directions." + key;
        if (el.ValueKind != JsonValueKind.Object)
            throw new NotchSpecException($"{where} must be an object.");

        var name = Str(el, "name", where);

        var shapeEl = Obj(el, "shape", where);
        var growText = Str(shapeEl, "grow", where + ".shape");
        var grow = growText switch
        {
            "sideways" => NotchGrowDirection.Sideways,
            "down" => NotchGrowDirection.Down,
            _ => throw new NotchSpecException($"{where}.shape.grow must be 'sideways' or 'down', not '{growText}'."),
        };
        var centered = Req(shapeEl, "centered", where + ".shape");
        if (centered.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new NotchSpecException($"{where}.shape.centered must be true or false.");
        var shape = new NotchShapeSpec(
            NonNegative(shapeEl, "bottomRadius", where + ".shape"),
            NonNegative(shapeEl, "shoulder", where + ".shape"),
            grow,
            centered.GetBoolean());

        var eyeEl = Obj(el, "eye", where);
        var eyeWhere = where + ".eye";
        var eye = new NotchEyeSpec(
            Str(eyeEl, "kind", eyeWhere),
            Positive(eyeEl, "w", eyeWhere),
            Positive(eyeEl, "h", eyeWhere),
            OptionalNonNegative(eyeEl, "pupil", eyeWhere),
            OptionalNonNegative(eyeEl, "glint", eyeWhere),
            NonNegative(eyeEl, "gap", eyeWhere),
            NonNegative(eyeEl, "gazeX", eyeWhere),
            NonNegative(eyeEl, "gazeY", eyeWhere));

        var sizesEl = Obj(el, "sizes", where);
        var sizes = new Dictionary<NotchFaceState, SizeD>();
        foreach (var (json, state) in StateKeys)
        {
            if (!sizesEl.TryGetProperty(json, out var sizeEl))
                throw new NotchSpecException($"{where}.sizes is missing the '{json}' state.");
            sizes[state] = Size(sizeEl, $"{where}.sizes.{json}");
        }

        return new NotchFaceSpec(key, name, shape, eye, sizes);
    }

    private static NotchTimings ParseTimings(JsonElement t)
    {
        const string w = "timings";
        var every = Req(t, "blinkEveryMs", w);
        if (every.ValueKind != JsonValueKind.Array || every.GetArrayLength() != 2)
            throw new NotchSpecException("timings.blinkEveryMs must be [min, max].");
        var min = Int(every[0], "timings.blinkEveryMs[0]");
        var max = Int(every[1], "timings.blinkEveryMs[1]");
        if (min <= 0 || max < min)
            throw new NotchSpecException("timings.blinkEveryMs must be positive with min <= max.");

        var chance = NonNegative(t, "doubleBlinkChance", w);
        if (chance > 1)
            throw new NotchSpecException("timings.doubleBlinkChance must be between 0 and 1.");

        var spring = Obj(t, "gazeSpring", w);

        return new NotchTimings(
            PositiveInt(t, "revealMs"),
            NonNegativeInt(t, "retractDelayMs"),
            PositiveInt(t, "retractMs"),
            PositiveInt(t, "resizeMs"),
            PositiveInt(t, "blinkCloseMs"),
            PositiveInt(t, "blinkOpenMs"),
            min,
            max,
            chance,
            Positive(spring, "stiffness", "timings.gazeSpring"),
            Positive(spring, "damping", "timings.gazeSpring"),
            PositiveInt(t, "captionCharMs"));
    }

    private static NotchMotionTimings ParseMotion(JsonElement t)
    {
        var d = NotchMotionTimings.Shipped;
        int Opt(string name, int fallback, bool allowZero)
        {
            if (!t.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return fallback;
            return allowZero ? NonNegativeInt(t, name) : PositiveInt(t, name);
        }

        var saccadeMin = d.SaccadeEveryMinMs;
        var saccadeMax = d.SaccadeEveryMaxMs;
        if (t.TryGetProperty("saccadeEveryMs", out var saccade) && saccade.ValueKind != JsonValueKind.Null)
        {
            if (saccade.ValueKind != JsonValueKind.Array || saccade.GetArrayLength() != 2)
                throw new NotchSpecException("timings.saccadeEveryMs must be [min, max].");
            saccadeMin = Int(saccade[0], "timings.saccadeEveryMs[0]");
            saccadeMax = Int(saccade[1], "timings.saccadeEveryMs[1]");
            if (saccadeMin <= 0 || saccadeMax < saccadeMin)
                throw new NotchSpecException("timings.saccadeEveryMs must be positive with min <= max.");
        }

        var shake = d.NudgeShake;
        if (t.TryGetProperty("nudgeShake", out var shakeEl) && shakeEl.ValueKind != JsonValueKind.Null)
        {
            if (shakeEl.ValueKind != JsonValueKind.String)
                throw new NotchSpecException("timings.nudgeShake must be a string like \"2 × ±3 px, 320 ms each\".");
            shake = ParseShake(shakeEl.GetString()!);
        }

        return new NotchMotionTimings(
            Opt("captionFadeMs", d.CaptionFadeMs, allowZero: true),
            Opt("captionDelayMs", d.CaptionDelayMs, allowZero: true),
            Opt("entryBlinkDelayMs", d.EntryBlinkDelayMs, allowZero: true),
            Opt("peekRiseMs", d.PeekRiseMs, allowZero: false),
            Opt("peekRiseDelayMs", d.PeekRiseDelayMs, allowZero: true),
            Opt("idleWanderAfterMs", d.IdleWanderAfterMs, allowZero: true),
            saccadeMin,
            saccadeMax,
            Opt("thinkingDotCycleMs", d.ThinkingDotCycleMs, allowZero: false),
            Opt("recordPulseMs", d.RecordPulseMs, allowZero: false),
            Opt("drawStrokeMs", d.DrawStrokeMs, allowZero: true),
            Opt("appOpenMs", d.AppOpenMs, allowZero: true),
            shake);
    }

    private static readonly System.Text.RegularExpressions.Regex ShakePattern = new(
        @"^\s*(?<count>\d+)\s*(?:\u00d7|x|X)\s*(?:\u00b1|\+-|\+/-)\s*(?<amp>\d+(?:\.\d+)?)\s*px\s*,\s*(?<ms>\d+)\s*ms(?:\s+each)?\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static NudgeShakeSpec ParseShake(string text)
    {
        var m = ShakePattern.Match(text);
        if (!m.Success
            || !int.TryParse(m.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || !double.TryParse(m.Groups["amp"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amp)
            || !int.TryParse(m.Groups["ms"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || count <= 0 || ms <= 0 || !double.IsFinite(amp))
            throw new NotchSpecException($"timings.nudgeShake '{text}' must read like \"2 × ±3 px, 320 ms each\" with a positive count and duration.");
        return new NudgeShakeSpec(count, amp, ms);
    }

    private static JsonElement Req(JsonElement parent, string name, string where)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            throw new NotchSpecException($"Notch spec is missing '{where}.{name}'.");
        return value;
    }

    private static JsonElement Obj(JsonElement parent, string name, string where)
    {
        var value = Req(parent, name, where);
        if (value.ValueKind != JsonValueKind.Object)
            throw new NotchSpecException($"'{where}.{name}' must be an object.");
        return value;
    }

    private static string Str(JsonElement parent, string name, string where)
    {
        var value = Req(parent, name, where);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new NotchSpecException($"'{where}.{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static double Num(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var d) || !double.IsFinite(d))
            throw new NotchSpecException($"'{where}' must be a finite number.");
        return d;
    }

    private static int Int(JsonElement value, string where)
    {
        var d = Num(value, where);
        if (d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue)
            throw new NotchSpecException($"'{where}' must be a whole number.");
        return (int)d;
    }

    private static double Positive(JsonElement parent, string name, string where)
    {
        var d = Num(Req(parent, name, where), where + "." + name);
        if (d <= 0) throw new NotchSpecException($"'{where}.{name}' must be greater than 0.");
        return d;
    }

    private static double NonNegative(JsonElement parent, string name, string where)
    {
        var d = Num(Req(parent, name, where), where + "." + name);
        if (d < 0) throw new NotchSpecException($"'{where}.{name}' must not be negative.");
        return d;
    }

    private static double OptionalNonNegative(JsonElement parent, string name, string where)
        => parent.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? NonNegative(parent, name, where) : 0;

    private static int PositiveInt(JsonElement t, string name)
    {
        var i = Int(Req(t, name, "timings"), "timings." + name);
        if (i <= 0) throw new NotchSpecException($"'timings.{name}' must be greater than 0.");
        return i;
    }

    private static int NonNegativeInt(JsonElement t, string name)
    {
        var i = Int(Req(t, name, "timings"), "timings." + name);
        if (i < 0) throw new NotchSpecException($"'timings.{name}' must not be negative.");
        return i;
    }

    private static SizeD Size(JsonElement value, string where)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            throw new NotchSpecException($"'{where}' must be [width, height].");
        var w = Num(value[0], where + "[0]");
        var h = Num(value[1], where + "[1]");
        if (w <= 0 || h <= 0)
            throw new NotchSpecException(string.Create(CultureInfo.InvariantCulture, $"'{where}' must be positive, got [{w}, {h}]."));
        return new SizeD(w, h);
    }

    private static NotchSpec BuildDefault()
    {
        var sizes = new Dictionary<NotchFaceState, SizeD>
        {
            [NotchFaceState.Hidden] = new(84, 36),
            [NotchFaceState.Hover] = new(84, 36),
            [NotchFaceState.Idle] = new(84, 36),
            [NotchFaceState.Listening] = new(172, 36),
            [NotchFaceState.Thinking] = new(128, 36),
            [NotchFaceState.Responding] = new(348, 94),
            [NotchFaceState.Capture] = new(148, 36),
            [NotchFaceState.Nudge] = new(348, 108),
            [NotchFaceState.Paused] = new(120, 36),
            [NotchFaceState.Happy] = new(96, 36),
        };
        var faceB = new NotchFaceSpec(
            "B",
            "Button eyes",
            new NotchShapeSpec(BottomRadius: 18, Shoulder: 10, NotchGrowDirection.Sideways, Centered: false),
            new NotchEyeSpec("button", Width: 16, Height: 16, Pupil: 7, Glint: 2.5, Gap: 7, GazeX: 3.5, GazeY: 3.5),
            sizes);
        var timings = new NotchTimings(
            RevealMs: 240, RetractDelayMs: 900, RetractMs: 160, ResizeMs: 280,
            BlinkCloseMs: 70, BlinkOpenMs: 90, BlinkEveryMinMs: 2800, BlinkEveryMaxMs: 6000,
            DoubleBlinkChance: 0.15, GazeStiffness: 260, GazeDamping: 0.82, CaptionCharMs: 28);
        return new NotchSpec(new Dictionary<string, NotchFaceSpec>(StringComparer.Ordinal) { ["B"] = faceB }, "B", new SizeD(220, 8), timings, NotchMotionTimings.Shipped);
    }
}
