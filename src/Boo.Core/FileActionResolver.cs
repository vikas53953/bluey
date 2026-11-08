using System.Text;

namespace Boo.Core;

public sealed record FileActionPlan(string CanonicalPath, string? Contents, bool Overwrite);

public static class FileActionResolver
{
    public const int MaximumWriteBytes = 4 * 1024 * 1024;

    public static FileActionPlan Resolve(ToolRequest request, TaskAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authority);

        if (request.Name is not ("write_file" or "delete_file"))
        {
            throw new ArgumentException("The requested action is not a supported file action.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(authority.FileRoot))
        {
            throw new ArgumentException("A user-selected file root is required for this file action.", nameof(authority));
        }

        var relativePath = GetRelativePath(request);
        string canonicalPath;
        try
        {
            canonicalPath = ScopedFiles.Resolve(authority.FileRoot, relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"The file path was rejected: {exception.Message}", nameof(request), exception);
        }

        var parent = Path.GetDirectoryName(canonicalPath);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The file path must be below an existing directory.");
        }

        if (request.Name == "delete_file")
        {
            EnsureRegularFile(canonicalPath, "The delete target must be an existing regular file.");
            return new FileActionPlan(canonicalPath, Contents: null, Overwrite: false);
        }

        var contents = GetContents(request);
        var bytes = Encoding.UTF8.GetBytes(contents);
        if (bytes.Length > MaximumWriteBytes)
        {
            throw new ArgumentException(
                $"File contents exceed Boo's bounded {MaximumWriteBytes}-byte write limit.",
                nameof(request));
        }

        var overwrite = GetOverwrite(request);
        EnsureWritableTarget(canonicalPath, overwrite);
        return new FileActionPlan(canonicalPath, contents, overwrite);
    }

    private static string GetRelativePath(ToolRequest request)
    {
        var arguments = request.Arguments;
        if (arguments is not null &&
            arguments.TryGetValue("path", out var path) &&
            !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(request.Target))
        {
            throw new ArgumentException("A non-empty relative file path is required.", nameof(request));
        }

        return request.Target;
    }

    private static string GetContents(ToolRequest request)
    {
        var arguments = request.Arguments;
        if (arguments is null || !arguments.TryGetValue("contents", out var contents))
        {
            if (arguments is null || !arguments.TryGetValue("content", out contents))
            {
                throw new ArgumentException("File contents are required for write_file.", nameof(request));
            }
        }

        if (contents is null)
        {
            throw new ArgumentException("File contents cannot be null for write_file.", nameof(request));
        }

        return contents;
    }

    private static bool GetOverwrite(ToolRequest request)
    {
        if (request.Arguments is null || !request.Arguments.TryGetValue("overwrite", out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!bool.TryParse(value, out var overwrite))
        {
            throw new ArgumentException("The overwrite option must be 'true' or 'false'.", nameof(request));
        }

        return overwrite;
    }

    private static void EnsureWritableTarget(string canonicalPath, bool overwrite)
    {
        if (!TryGetAttributes(canonicalPath, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new IOException("The write target is a directory, not a regular file.");
        }

        if ((attributes & FileAttributes.Device) != 0)
        {
            throw new IOException("The write target is a device and cannot be replaced.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The write target is a reparse point and cannot be replaced.");
        }

        if (!overwrite)
        {
            throw new IOException("The target file exists; an explicit overwrite approval is required.");
        }
    }

    private static void EnsureRegularFile(string canonicalPath, string reason)
    {
        if (!TryGetAttributes(canonicalPath, out var attributes) ||
            (attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
        {
            throw new FileNotFoundException(reason, canonicalPath);
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
