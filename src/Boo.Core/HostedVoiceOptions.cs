namespace Boo.Core;

/// <summary>Auditable, opt-in CI audio-device configuration. Host variables are policy signals, not attestation.</summary>
public sealed record HostedVoiceOptions(string Input, string Evidence, string TranscriptionModel)
{
    public const string InputVariable = "BOO_QA_VOICE_INPUT";
    public static HostedVoiceOptions? FromEnvironment() => Read(RuntimeEnvironment.Read, OperatingSystem.IsWindows());

    public static HostedVoiceOptions? Read(Func<string, string?> read, bool windows)
    {
        var input = read(InputVariable);
        if (string.IsNullOrEmpty(input)) return null;
        if (!windows || read("GITHUB_ACTIONS") != "true" || read("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("QA voice devices require GitHub-hosted Windows.");
        var evidence = read("BOO_QA_VOICE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence)) throw new InvalidOperationException("QA voice evidence path missing.");
        var model = read("BOO_QA_VOICE_TRANSCRIBE_MODEL") ?? "gpt-4o-mini-transcribe";
        if (!AiProviders.IsValidModelId(model)) throw new InvalidOperationException("Invalid QA transcription model.");
        return new HostedVoiceOptions(input, evidence, model);
    }
}
