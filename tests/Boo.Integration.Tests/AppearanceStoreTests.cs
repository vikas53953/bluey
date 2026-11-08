using Boo.Core;
using Boo.Storage;

namespace Boo.Integration.Tests;

public sealed class AppearanceStoreTests
{
    [Fact]
    public void Missing_file_loads_default_appearance()
    {
        var path = TemporaryPath();
        try
        {
            var restored = new AppearanceStore(path).Load();

            Assert.Equal(new NotchAppearance(), restored);
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Valid_custom_appearance_round_trips_through_a_real_file()
    {
        var path = TemporaryPath();
        var expected = new NotchAppearance("#123456", "#F0F0F0");
        try
        {
            var store = new AppearanceStore(path);
            store.Save(expected);

            Assert.Equal(expected, store.Load());
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Corrupt_json_fails_with_a_bounded_invalid_data_error()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, "{not valid json");

            var error = Assert.Throws<InvalidDataException>(() => new AppearanceStore(path).Load());

            Assert.Contains("corrupt", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Fact]
    public void Persisted_valid_json_with_invalid_colors_fails_load_validation()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllText(path, "{\"BodyColor\":\"#000000\",\"EyeColor\":\"#000000\"}");

            Assert.Throws<InvalidDataException>(() => new AppearanceStore(path).Load());
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    [Theory]
    [InlineData("#000000", "#000000")]
    [InlineData("#GGGGGG", "#FFFFFF")]
    [InlineData("#00000080", "#FFFFFF")]
    public void Invalid_save_preserves_the_previous_good_file(string bodyColor, string eyeColor)
    {
        var path = TemporaryPath();
        var expected = new NotchAppearance("#123456", "#F0F0F0");
        try
        {
            var store = new AppearanceStore(path);
            store.Save(expected);

            Assert.Throws<ArgumentException>(() => store.Save(new NotchAppearance(bodyColor, eyeColor)));

            Assert.Equal(expected, store.Load());
        }
        finally
        {
            DeleteTemporary(path);
        }
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"boo-appearance-{Guid.NewGuid():N}.json");

    private static void DeleteTemporary(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
