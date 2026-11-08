using System.Security.Cryptography;
using System.Text;
using Boo.Core;

namespace Boo.Storage;

public sealed class CredentialStore : ICredentialStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _path;

    public CredentialStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A credential path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public string? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            if (protectedBytes.Length == 0)
                throw new CryptographicException();

            var plaintext = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return StrictUtf8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException or IOException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidDataException("Credential data is corrupt or unreadable. Delete it and configure Boo again.");
        }
    }

    public void Save(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), optionalEntropy: null, DataProtectionScope.CurrentUser);
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.SequentialScan))
            {
                stream.Write(protectedBytes);
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

    public void Delete()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}

