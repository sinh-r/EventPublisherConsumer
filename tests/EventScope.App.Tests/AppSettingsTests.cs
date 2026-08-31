using EventScope.App.Settings;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>Plain JSON round-trip and defensive defaults — no Avalonia dependency, needs no
/// <see cref="HeadlessFixture"/>.</summary>
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Directory.CreateTempSubdirectory("eventscope-settings-tests-").FullName, "settings.json");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);

    [Fact]
    public void Loading_a_missing_file_returns_defaults()
    {
        var settings = AppSettings.Load(_path);

        Assert.Equal(2L * 1024 * 1024 * 1024, settings.RetentionCapBytes);
        Assert.Equal(14, settings.RetentionDays);
        Assert.Equal(2048, settings.IndexedPrefixBytes);
        Assert.Empty(settings.PinnedFields);
    }

    [Fact]
    public void Saved_settings_round_trip_exactly()
    {
        var settings = new AppSettings
        {
            RetentionCapBytes = 5_000_000,
            RetentionDays = 30,
            IndexedPrefixBytes = 4096,
        };
        settings.PinnedFields.Add(new PinnedFieldSetting("customerId", "$.customerId"));

        settings.Save(_path);
        var loaded = AppSettings.Load(_path);

        Assert.Equal(5_000_000, loaded.RetentionCapBytes);
        Assert.Equal(30, loaded.RetentionDays);
        Assert.Equal(4096, loaded.IndexedPrefixBytes);
        Assert.Single(loaded.PinnedFields);
        Assert.Equal("customerId", loaded.PinnedFields[0].Name);
        Assert.Equal("$.customerId", loaded.PinnedFields[0].JsonPath);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_rather_than_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not valid json");

        var settings = AppSettings.Load(_path);

        Assert.Equal(14, settings.RetentionDays); // the default, not a thrown exception
    }
}
