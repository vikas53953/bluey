using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Boo.Core;

/// <summary>
/// The local, deliberately small action boundary.  Model output is only a
/// request; it never enlarges this set or supplies its own authority.
/// </summary>
public static class ActionPolicy
{
    public static IReadOnlySet<string> ToolNames { get; } = new[]
    {
        "search", "inspect", "open_url", "open_app", "click", "type_text",
        "write_file", "delete_file"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "search", "inspect", "open_url"
    };

    public static PermissionDecision Evaluate(
        ToolRequest request,
        TaskAuthority authority,
        ApprovalTicket? approval = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);

        if (!ToolNames.Contains(request.Name))
        {
            return new PermissionDecision(PermissionVerdict.Denied, "The requested tool is not in Boo's local allowlist.");
        }

        if (!authority.ExplicitUserTask)
        {
            return new PermissionDecision(PermissionVerdict.Denied, "An explicit user task is required before an action can run.");
        }

        if (request.Generation != authority.Generation)
        {
            return new PermissionDecision(PermissionVerdict.Denied, "The request belongs to a stopped or stale generation.");
        }

        if (authority.AllowedTools is null || !authority.AllowedTools.Contains(request.Name))
        {
            return new PermissionDecision(PermissionVerdict.Denied, "The current task authority does not allow this tool.");
        }

        if (request.Name == "open_url" && !IsSafeHttpUrl(request.Target))
        {
            return new PermissionDecision(PermissionVerdict.Denied, "Only absolute HTTP(S) URLs with a host are allowed.");
        }

        if (ReadOnlyTools.Contains(request.Name))
        {
            return new PermissionDecision(PermissionVerdict.Allowed, "Read-only tool is explicitly authorized for the current task.");
        }

        if (approval is null)
        {
            return new PermissionDecision(PermissionVerdict.NeedsApproval, "This action needs a concrete confirmation for its exact target and arguments.");
        }

        if (approval.Generation != request.Generation || approval.Digest is null ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(approval.Digest),
                Encoding.UTF8.GetBytes(ComputeDigest(request))))
        {
            return new PermissionDecision(PermissionVerdict.Denied, "The approval does not match this action, target, arguments, or generation.");
        }

        return new PermissionDecision(PermissionVerdict.Allowed, "The exact action has a matching approval.");
    }

    public static ApprovalTicket Approve(ToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ApprovalTicket(request.Generation, ComputeDigest(request));
    }

    internal static bool IsSafeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // "https://bank.example@evil.test" shows a trusted name on the approval
        // card but opens a different host, so user-info URLs are never offered.
        return (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    internal static string ComputeDigest(ToolRequest request)
    {
        // Length-prefix every field so concatenation cannot create an
        // alternate representation of the same approval.  Arguments are
        // ordinal-sorted, making dictionary insertion order irrelevant.
        var builder = new StringBuilder();
        AppendField(builder, request.Name);
        AppendField(builder, request.Target);
        AppendField(builder, request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var argument in (request.Arguments ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendField(builder, argument.Key);
            AppendField(builder, argument.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:|");
            return;
        }

        builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }
}

public static class ScopedFiles
{
    public static string Resolve(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A selected file root is required.", nameof(root));
        }

        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("The file path must be a non-empty relative path.", nameof(relativePath));
        }

        if (relativePath.IndexOf(':', StringComparison.Ordinal) >= 0)
        {
            throw new ArgumentException("Alternate data streams and drive-qualified paths are not allowed.", nameof(relativePath));
        }

        var segments = relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Traversal and empty file paths are not allowed.", nameof(relativePath));
        }

        var rootFull = Path.GetFullPath(root);
        if (!Directory.Exists(rootFull))
        {
            throw new DirectoryNotFoundException("The selected file root does not exist.");
        }

        var rootAttributes = File.GetAttributes(rootFull);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The selected file root is a reparse point.");
        }

        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The file path escapes the selected root.");
        }

        // Walk every existing component.  This catches directory junctions,
        // symlinks, and a reparse-point final file before a caller uses it.
        var current = rootFull;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("The file path contains a reparse point.");
                }
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }

        return candidate;
    }
}

