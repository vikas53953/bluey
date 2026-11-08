using Boo.Core;
using Boo.Storage;

namespace Boo.Integration.Tests;

public sealed class AppearanceReadFailureTests
{
    [Fact]
    public void Directory_at_configured_path_reports_bounded_error_and_preserves_sentinel()
    {
        var root = CreateOwnedTemporaryDirectory();
        var path = Path.Combine(root, "appearance.json");
        var sentinelPath = Path.Combine(path, "sentinel.txt");
        const string sentinel = "owned sentinel; do not remove";

        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(sentinelPath, sentinel);

            var error = Assert.Throws<InvalidDataException>(() => new AppearanceStore(path).Load());

            Assert.Contains("appearance", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sentinel, File.ReadAllText(sentinelPath));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Missing_parent_returns_defaults_without_creating_parent_directory()
    {
        var root = NewOwnedTemporaryPath();
        var parent = Path.Combine(root, "nested");
        var path = Path.Combine(parent, "appearance.json");

        try
        {
            Assert.False(Directory.Exists(root));

            var restored = new AppearanceStore(path).Load();

            Assert.Equal(new NotchAppearance(), restored);
            Assert.False(Directory.Exists(parent));
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(root);
        }
    }

    [Fact]
    public void Exclusive_read_lock_reports_bounded_error_and_preserves_saved_bytes()
    {
        var root = CreateOwnedTemporaryDirectory();
        var path = Path.Combine(root, "appearance.json");
        var expected = new NotchAppearance("#123456", "#F0F0F0");

        try
        {
            var store = new AppearanceStore(path);
            store.Save(expected);
            var savedBytes = File.ReadAllBytes(path);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Assert.Throws<InvalidDataException>(() => store.Load());

                Assert.Contains("appearance", error.Message, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Equal(savedBytes, File.ReadAllBytes(path));
            Assert.Equal(expected, store.Load());
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(root);
        }
    }

    private static string NewOwnedTemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"boo-shell-failure-{Guid.NewGuid():N}");

    private static string CreateOwnedTemporaryDirectory()
    {
        var path = NewOwnedTemporaryPath();
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOwnedTemporaryDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var leaf = Path.GetFileName(fullPath);
        if (!leaf.StartsWith("boo-shell-failure-", StringComparison.Ordinal)
            || !fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to clean a non-owned test path.");
        }

        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }
}
