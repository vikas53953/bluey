using Boo.Core;
using Boo.Providers;

namespace Boo.App;

public partial class MainWindow
{
    private RealtimeClient EnsureRealtime(bool pushToTalk = false)
    {
        if (_realtime is not null)
        {
            return _realtime;
        }

        _credentialStore ??= StoreFor(AiProvider.OpenAI);
        var qaVoice = HostedVoiceOptions.FromEnvironment();
        var realtime = RealtimeClient.ForNativeVoice(() => _credentialStore.Load(),
            _settings.Provider ?? new ProviderSettings(), pushToTalk, qaVoice);
        _realtimePushToTalk = pushToTalk;
        _realtimeTranscript.Reset();
        _realtimeTranscriptRows.Clear();
        var generation = _generation;
        realtime.Received += value => Realtime_OnReceived(realtime, generation, value);
        AttachVoiceCommands(realtime);
        _realtime = realtime;
        return _realtime;
    }
}
