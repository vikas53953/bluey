using Boo.Core;

namespace Boo.Core.Tests;

public sealed class AccessibilityPreferencesTests
{
    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(100, 1.0)]
    [InlineData(125, 1.25)]
    [InlineData(225, 2.25)]
    public void Text_scale_follows_the_windows_percent(int? percent, double expected)
    {
        Assert.Equal(expected, TextScale.FromPercent(percent), precision: 6);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-50, 1.0)]
    [InlineData(99, 1.0)]
    [InlineData(226, 2.25)]
    [InlineData(int.MaxValue, 2.25)]
    [InlineData(int.MinValue, 1.0)]
    public void Text_scale_clamps_bad_registry_values(int percent, double expected)
    {
        Assert.Equal(expected, TextScale.FromPercent(percent), precision: 6);
    }

    [Fact]
    public void Default_scale_is_not_applied()
    {
        Assert.False(TextScale.IsScaled(1.004));
        Assert.True(TextScale.IsScaled(1.25));
    }

    [Fact]
    public void Scaled_minimum_grows_with_the_factor()
    {
        Assert.Equal(760 * 1.5, TextScale.ScaledMinimum(760, 1.5, available: 3000), precision: 6);
    }

    [Fact]
    public void Scaled_minimum_never_exceeds_the_work_area()
    {
        Assert.Equal(1280, TextScale.ScaledMinimum(760, 2.25, available: 1280), precision: 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Scaled_minimum_treats_a_bad_factor_as_one(double factor)
    {
        Assert.Equal(760, TextScale.ScaledMinimum(760, factor, available: 3000), precision: 6);
    }

    [Fact]
    public void Scaled_minimum_ignores_an_unknown_work_area()
    {
        Assert.Equal(760 * 1.25, TextScale.ScaledMinimum(760, 1.25, available: double.NaN), precision: 6);
        Assert.Equal(760 * 1.25, TextScale.ScaledMinimum(760, 1.25, available: 0), precision: 6);
    }

    [Fact]
    public void Scaled_minimum_of_a_bad_base_is_zero()
    {
        Assert.Equal(0, TextScale.ScaledMinimum(double.NaN, 1.25, 1000));
        Assert.Equal(0, TextScale.ScaledMinimum(-5, 1.25, 1000));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void Reduced_motion_when_windows_animations_are_off_or_boo_setting_is_on(bool systemAnimations, bool appReduced, bool expected)
    {
        Assert.Equal(expected, MotionPreference.ReducedMotion(systemAnimations, appReduced));
    }
}
