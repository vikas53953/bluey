using Boo.Core;

namespace Boo.Core.Tests;

public sealed class NotchAppearanceTests
{
    [Fact]
    public void Default_appearance_is_opaque_black_body_with_white_eyes()
    {
        var appearance = new NotchAppearance();

        Assert.Equal("#000000", appearance.BodyColor);
        Assert.Equal("#FFFFFF", appearance.EyeColor);
        appearance.Validate();
    }

    [Fact]
    public void Valid_custom_colors_are_accepted()
    {
        var appearance = new NotchAppearance("#123456", "#F0F0F0");

        appearance.Validate();
    }

    [Theory]
    [InlineData("#000000", "#000000")]
    [InlineData("#123456", "#123456")]
    [InlineData("#777777", "#888888")]
    public void Low_contrast_colors_are_rejected(string bodyColor, string eyeColor)
    {
        var appearance = new NotchAppearance(bodyColor, eyeColor);

        Assert.Throws<ArgumentException>(() => appearance.Validate());
    }

    [Theory]
    [InlineData("000000", "#FFFFFF")]
    [InlineData("#00000", "#FFFFFF")]
    [InlineData("#0000000", "#FFFFFF")]
    [InlineData("#GGGGGG", "#FFFFFF")]
    [InlineData("#00000080", "#FFFFFF")]
    [InlineData("#000000", "FFFFFF")]
    [InlineData("#000000", "#00000")]
    [InlineData("#000000", "#GGGGGG")]
    [InlineData("#000000", "#FFFFFF80")]
    public void Malformed_or_nonopaque_colors_are_rejected(string bodyColor, string eyeColor)
    {
        var appearance = new NotchAppearance(bodyColor, eyeColor);

        Assert.Throws<ArgumentException>(() => appearance.Validate());
    }
}
