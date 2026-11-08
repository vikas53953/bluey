namespace Boo.Core;
public readonly record struct RectD(double X, double Y, double Width, double Height);
public sealed record DisplayInfo(string Id, string Name, RectD PixelBounds, double Scale);
public sealed record WindowIdentity(long Handle, int ProcessId, string ProcessName, RectD Bounds);
public sealed record ScreenContext(Guid ObservationId, long Generation, DisplayInfo Display, int FrameWidth, int FrameHeight, WindowIdentity Target, DateTimeOffset CapturedAt);
public sealed record CapturedFrame(ScreenContext Context, byte[] Jpeg);
public sealed record AnnotationMark(string Kind, RectD Bounds, string Label, double EndX = 0, double EndY = 0);
public sealed record AnnotationProposal(Guid ObservationId, long Generation, string DisplayId, IReadOnlyList<AnnotationMark> Marks);
public sealed record ValidationResult(bool Valid, string Reason);
public enum FocusActivity { OnTask, OffTask, Uncertain, Unavailable, Paused }
public enum FocusStatus { Running, Paused, Ended }
public sealed record FocusCheck(double Elapsed, FocusActivity Activity, double Probability = 0, double Confidence = 0, IReadOnlyDictionary<string,double>? Probabilities = null, double SampleDuration = 30, bool Nudged = false);
public sealed record FocusSnapshot(Guid Id, string Task, double Duration, DateTimeOffset StartedAt, double Elapsed, double RealElapsed, FocusStatus Status, bool DeveloperMode, bool UsedDeveloperMode, IReadOnlyList<FocusCheck> Checks);
public sealed record FocusDecision(FocusActivity Activity, double Probability, double Confidence, IReadOnlyDictionary<string,double> Probabilities);
public sealed record RequestLease(long Generation, CancellationToken Token);
public sealed record ToolRequest(string Name, string Target, IReadOnlyDictionary<string,string> Arguments, long Generation);
public sealed record TaskAuthority(long Generation, bool ExplicitUserTask, IReadOnlySet<string> AllowedTools, string? FileRoot = null);
public enum PermissionVerdict { Allowed, NeedsApproval, Denied }
public sealed record PermissionDecision(PermissionVerdict Verdict, string Reason);
public sealed record ApprovalTicket(long Generation, string Digest);
public sealed record SourceLink(string Title, string Url);
public sealed record AssistantRequest(string Text, long Generation, CapturedFrame? Frame = null, bool Research = false, bool AllowActions = false);
public sealed record AssistantReply(string Text, IReadOnlyList<SourceLink> Sources, AnnotationProposal? Annotations, IReadOnlyList<ToolRequest> Actions);
public sealed record ToolOutcome(bool Success, bool Committed, string Message);
public sealed record ProviderSettings(string ReasoningModel = "gpt-6.1-sol", string RealtimeModel = "gpt-realtime-2.1", string FocusModel = "gpt-6-luna", string Voice = "marin");
public sealed record AppSettings(string? DisplayId = null, bool SpeechEnabled = true, bool ChirpsEnabled = false, bool ReducedMotion = false, double VerticalOffset = 0, int MicrophoneDevice = -1, string? FileRoot = null, ProviderSettings? Provider = null);
public interface ICredentialStore { string? Load(); void Save(string key); void Delete(); }
public interface ISessionStore { void Save(FocusSnapshot session); IReadOnlyList<FocusSnapshot> List(); void Delete(Guid id); }

