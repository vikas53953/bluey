using System.Globalization;

namespace Boo.Core;

public sealed record NotchAppearance(string BodyColor = "#000000", string EyeColor = "#FFFFFF")
{
    public void Validate()
    {
        if (!TryParseRgb(BodyColor, out var bodyRed, out var bodyGreen, out var bodyBlue))
            throw new ArgumentException("BodyColor must be an opaque six-digit RGB color in #RRGGBB form.", nameof(BodyColor));

        if (!TryParseRgb(EyeColor, out var eyeRed, out var eyeGreen, out var eyeBlue))
            throw new ArgumentException("EyeColor must be an opaque six-digit RGB color in #RRGGBB form.", nameof(EyeColor));

        var bodyLuminance = RelativeLuminance(bodyRed, bodyGreen, bodyBlue);
        var eyeLuminance = RelativeLuminance(eyeRed, eyeGreen, eyeBlue);
        var lighter = Math.Max(bodyLuminance, eyeLuminance);
        var darker = Math.Min(bodyLuminance, eyeLuminance);
        var contrast = (lighter + 0.05) / (darker + 0.05);
        if (contrast < 3.0)
            throw new ArgumentException("BodyColor and EyeColor must have a contrast ratio of at least 3:1.");
    }

    private static bool TryParseRgb(string? color, out int red, out int green, out int blue)
    {
        red = green = blue = 0;
        if (color is null || color.Length != 7 || color[0] != '#')
            return false;

        if (!byte.TryParse(color.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsedRed)
            || !byte.TryParse(color.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsedGreen)
            || !byte.TryParse(color.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsedBlue))
            return false;

        red = parsedRed;
        green = parsedGreen;
        blue = parsedBlue;
        return true;
    }

    private static double RelativeLuminance(int red, int green, int blue)
    {
        return 0.2126 * Linearize(red)
            + 0.7152 * Linearize(green)
            + 0.0722 * Linearize(blue);
    }

    private static double Linearize(int channel)
    {
        var srgb = channel / 255.0;
        return srgb <= 0.04045
            ? srgb / 12.92
            : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }
}
