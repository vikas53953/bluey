using Boo.Core;
using System.Text.Json;

namespace Boo.Storage;

public sealed class AppearanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _path;

    public AppearanceStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An appearance path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public NotchAppearance Load()
    {
        try
        {
            using var stream = File.OpenRead(_path);
            var appearance = JsonSerializer.Deserialize<NotchAppearance>(stream, JsonOptions);
            if (appearance is null)
                throw new InvalidDataException("Appearance settings are corrupt or invalid.");

            appearance.Validate();
            return appearance;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new NotchAppearance();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Appearance settings are corrupt or invalid.", ex);
        }
    }

    public void Save(NotchAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        appearance.Validate();

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, appearance, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
