using Boo.Core;

namespace Boo.Core.Tests;

public sealed class CaptureExclusionPolicyTests
{
    private static CaptureExclusionPolicy With(string? qaValue) =>
        new(name => name == "BOO_QA_CAPTURABLE" ? qaValue : null);

    [Fact]
    public void Variable_name_and_affinity_values_are_fixed()
    {
        Assert.Equal("BOO_QA_CAPTURABLE", CaptureExclusionPolicy.QaEnvironmentVariable);
        Assert.Equal(0x11u, CaptureExclusionPolicy.ExcludeFromCaptureAffinity);
        Assert.Equal(0x0u, CaptureExclusionPolicy.NoAffinity);
    }

    [Fact]
    public void Exclusion_is_on_when_variable_is_unset()
    {
        var policy = With(null);

        Assert.True(policy.ExcludeFromCapture);
        Assert.Equal(0x11u, policy.DisplayAffinity);
        Assert.Null(policy.Warning);
    }

    [Fact]
    public void Exclusion_is_off_only_when_variable_is_exactly_1()
    {
        var policy = With("1");

        Assert.False(policy.ExcludeFromCapture);
        Assert.Equal(0x0u, policy.DisplayAffinity);
    }

    [Fact]
    public void Qa_mode_reports_a_visible_warning()
    {
        var policy = With("1");

        Assert.False(string.IsNullOrWhiteSpace(policy.Warning));
        Assert.Contains("QA", policy.Warning!, StringComparison.Ordinal);
        Assert.Contains("captur", policy.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("11")]
    public void Any_other_value_keeps_exclusion_on(string value)
    {
        var policy = With(value);

        Assert.True(policy.ExcludeFromCapture);
        Assert.Equal(0x11u, policy.DisplayAffinity);
        Assert.Null(policy.Warning);
    }

    [Fact]
    public void Reads_only_the_qa_variable()
    {
        var asked = new List<string>();
        var policy = new CaptureExclusionPolicy(name =>
        {
            asked.Add(name);
            return "1";
        });

        _ = policy.ExcludeFromCapture;

        Assert.All(asked, n => Assert.Equal("BOO_QA_CAPTURABLE", n));
        Assert.NotEmpty(asked);
    }

    [Fact]
    public void Null_environment_reader_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptureExclusionPolicy(null!));
    }
}
