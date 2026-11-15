using Boo.Core;

namespace Boo.Core.Tests;

public sealed class CaptureFaultPolicyTests
{
    private static CaptureFaultPolicy With(string? qaValue) =>
        new(name => name == "BOO_QA_CAPTURE_FAULT" ? qaValue : null);

    [Fact]
    public void Variable_name_is_fixed()
    {
        Assert.Equal("BOO_QA_CAPTURE_FAULT", CaptureFaultPolicy.QaEnvironmentVariable);
    }

    [Fact]
    public void Capture_is_not_faulted_when_variable_is_unset()
    {
        Assert.False(With(null).Faulted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("1 ")]
    [InlineData("01")]
    public void Capture_is_not_faulted_for_any_value_other_than_exactly_1(string value)
    {
        Assert.False(With(value).Faulted);
    }

    [Fact]
    public void Capture_is_faulted_only_when_variable_is_exactly_1()
    {
        var policy = With("1");

        Assert.True(policy.Faulted);
        Assert.Contains("BOO_QA_CAPTURE_FAULT=1", policy.Message, StringComparison.Ordinal);
        Assert.Contains("No frame was sent", policy.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_variables_never_fault_capture()
    {
        var policy = new CaptureFaultPolicy(name => name == "BOO_QA_CAPTURABLE" ? "1" : null);

        Assert.False(policy.Faulted);
    }

    [Fact]
    public void Null_reader_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new CaptureFaultPolicy(null!));
    }
}
