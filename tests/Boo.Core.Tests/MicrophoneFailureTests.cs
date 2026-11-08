using Boo.Core;

namespace Boo.Core.Tests;

public sealed class MicrophoneFailureTests
{
    [Fact]
    public void No_input_devices_is_no_device()
    {
        Assert.Equal(MicrophoneFailureKind.NoDevice,
            MicrophoneFailures.ClassifyStart(deviceCount: 0, requestedDevice: -1, mmResult: null, accessDenied: false));
        Assert.Contains("No microphone", MicrophoneFailures.Message(MicrophoneFailureKind.NoDevice), StringComparison.Ordinal);
    }

    [Fact]
    public void A_saved_device_index_beyond_the_device_list_is_device_missing()
    {
        Assert.Equal(MicrophoneFailureKind.DeviceMissing,
            MicrophoneFailures.ClassifyStart(deviceCount: 1, requestedDevice: 3, mmResult: null, accessDenied: false));
    }

    [Theory]
    [InlineData(MicrophoneFailures.MmBadDeviceId)]
    [InlineData(MicrophoneFailures.MmNoDriver)]
    [InlineData(MicrophoneFailures.MmNodeNotFound)]
    public void Driver_and_device_id_errors_are_device_missing(int mmResult)
    {
        Assert.Equal(MicrophoneFailureKind.DeviceMissing,
            MicrophoneFailures.ClassifyStart(2, 0, mmResult, false));
    }

    [Fact]
    public void Allocated_means_blocked_by_privacy_or_in_use_and_points_at_the_privacy_setting()
    {
        var kind = MicrophoneFailures.ClassifyStart(1, 0, MicrophoneFailures.MmAllocated, false);
        Assert.Equal(MicrophoneFailureKind.BlockedOrInUse, kind);
        var message = MicrophoneFailures.Message(kind);
        Assert.Contains("Privacy & security > Microphone", message, StringComparison.Ordinal);
        Assert.Contains("Let desktop apps access your microphone", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Access_denied_wins_over_any_mm_result()
    {
        var kind = MicrophoneFailures.ClassifyStart(1, 0, MicrophoneFailures.MmUnspecifiedError, accessDenied: true);
        Assert.Equal(MicrophoneFailureKind.AccessDenied, kind);
        Assert.Contains("Let desktop apps access your microphone", MicrophoneFailures.Message(kind), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MicrophoneFailures.WaveBadFormat)]
    [InlineData(MicrophoneFailures.MmNotSupported)]
    public void Format_errors_are_unsupported(int mmResult)
    {
        Assert.Equal(MicrophoneFailureKind.UnsupportedFormat,
            MicrophoneFailures.ClassifyStart(1, 0, mmResult, false));
    }

    [Fact]
    public void Unrecognized_errors_keep_the_generic_message()
    {
        var kind = MicrophoneFailures.ClassifyStart(1, 0, MicrophoneFailures.MmUnspecifiedError, false);
        Assert.Equal(MicrophoneFailureKind.Unknown, kind);
        Assert.Equal("Microphone capture could not start.", MicrophoneFailures.Message(kind));
        Assert.Equal(MicrophoneFailureKind.Unknown, MicrophoneFailures.ClassifyStart(1, 0, null, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MicrophoneFailures.MmNoDriver)]
    [InlineData(MicrophoneFailures.MmBadDeviceId)]
    [InlineData(MicrophoneFailures.MmUnspecifiedError)]
    public void Recording_stopped_with_an_error_is_a_disconnect(int? mmResult)
    {
        var kind = MicrophoneFailures.ClassifyStopped(mmResult, accessDenied: false);
        Assert.Equal(MicrophoneFailureKind.Disconnected, kind);
        Assert.Contains("disconnected", MicrophoneFailures.Message(kind), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recording_stopped_by_access_revocation_is_access_denied()
    {
        Assert.Equal(MicrophoneFailureKind.AccessDenied, MicrophoneFailures.ClassifyStopped(null, accessDenied: true));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))]
    [InlineData(5)]
    public void Access_denied_codes_are_recognized(int code)
    {
        Assert.True(MicrophoneFailures.IsAccessDeniedCode(code));
        Assert.False(MicrophoneFailures.IsAccessDeniedCode(0));
        Assert.False(MicrophoneFailures.IsAccessDeniedCode(unchecked((int)0x80004005)));
    }

    [Fact]
    public void Every_kind_has_a_short_actionable_message()
    {
        foreach (var kind in Enum.GetValues<MicrophoneFailureKind>())
        {
            var message = MicrophoneFailures.Message(kind);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.True(message.Length <= 160, kind + " message is too long");
        }
    }
}
