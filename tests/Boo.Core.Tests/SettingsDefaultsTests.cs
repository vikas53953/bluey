using Boo.Core;

namespace Boo.Core.Tests;

public sealed class SettingsDefaultsTests
{
    [Fact]
    public void Chirps_are_on_by_default_per_design_decision()
    {
        Assert.True(new AppSettings().ChirpsEnabled);
    }

    [Fact]
    public void Start_with_windows_is_off_by_default()
    {
        Assert.False(new AppSettings().StartWithWindows);
    }
}
