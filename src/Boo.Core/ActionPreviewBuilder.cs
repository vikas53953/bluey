using System.Collections.Immutable;
using System.Collections.Frozen;

namespace Boo.Core;

public sealed record ActionPreviewField(string Key, string Label, string Value)
{
    public string DisplayValue => Value.Length == 0 ? "(empty value)" : Value;
}

public sealed record ActionPreviewModel(string Title, string RequestedTarget, string Destination,
    ImmutableArray<ActionPreviewField> Fields, ToolRequest Request, bool CanApprove, string? BlockedReason);

public static class ActionPreviewBuilder
{
    public static ActionPreviewModel Create(ToolRequest request, TaskAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);

        var arguments = request.Arguments is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(request.Arguments, StringComparer.Ordinal);
        var frozenArguments = arguments.ToFrozenDictionary(StringComparer.Ordinal);
        var copiedRequest = new ToolRequest(request.Name, request.Target, frozenArguments, request.Generation);

        var fields = arguments
            .OrderBy(argument => argument.Key, StringComparer.Ordinal)
            .Select(argument => new ActionPreviewField(
                argument.Key,
                LabelFor(argument.Key, arguments),
                argument.Value ?? string.Empty))
            .ToImmutableArray();

        var decision = ActionPolicy.Evaluate(copiedRequest, authority);
        var destination = copiedRequest.Target ?? string.Empty;
        string? fileResolutionReason = null;
        if (copiedRequest.Name is "write_file" or "delete_file")
        {
            try
            {
                destination = FileActionResolver.Resolve(copiedRequest, authority).CanonicalPath;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                fileResolutionReason = exception.Message;
            }
        }

        var blockedReason = decision.Verdict == PermissionVerdict.Denied
            ? decision.Reason
            : fileResolutionReason;
        var canApprove = blockedReason is null;

        return new ActionPreviewModel(
            TitleFor(copiedRequest.Name),
            copiedRequest.Target ?? string.Empty,
            destination,
            fields,
            copiedRequest,
            canApprove,
            blockedReason);
    }

    private static string TitleFor(string? toolName) => toolName switch
    {
        "search" => "Search",
        "inspect" => "Inspect window",
        "open_url" => "Open website",
        "open_app" => "Open app",
        "click" => "Click",
        "type_text" => "Type text",
        "write_file" => "Write file",
        "delete_file" => "Delete file",
        _ => "Unsupported action"
    };

    private static string LabelFor(string key, IReadOnlyDictionary<string, string> arguments) => key switch
    {
        "automationId" => "Automation ID",
        "content" when arguments.ContainsKey("contents") => "File content (unused alias; contents takes precedence)",
        "content" => "File content alias",
        "contents" => "File contents",
        "name" => "Name",
        "overwrite" => "Overwrite existing file",
        "path" => "Path override",
        "text" => "Text",
        "x" => "X coordinate",
        "y" => "Y coordinate",
        _ => key
    };
}
