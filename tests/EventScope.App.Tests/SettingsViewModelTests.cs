using EventScope.App.Settings;
using EventScope.App.ViewModels;
using EventScope.Storage.Retention;
using EventScope.Storage.Sqlite;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>No Avalonia dependency — pure view-model logic, needs no <see cref="HeadlessFixture"/>.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eventscope-settingsvm-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RetentionCapMegabytes_converts_to_and_from_bytes()
    {
        var vm = new SettingsViewModel(new AppSettings(), () => null, () => null, _ => { });

        vm.RetentionCapMegabytes = 500;

        Assert.Equal(500L * 1024 * 1024, vm.RetentionCapBytes);
        Assert.Equal(500, vm.RetentionCapMegabytes);
    }

    [Fact]
    public void Save_persists_to_the_settings_object_and_pushes_live_values_to_a_running_retention_service()
    {
        var settings = new AppSettings();
        using var store = new SessionStore(_root);
        using var retention = new RetentionService(_root, store, capBytes: 1024, retentionDays: 1);

        var vm = new SettingsViewModel(settings, () => store, () => retention, _ => { })
        {
            RetentionCapBytes = 9_999,
            RetentionDays = 7,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(9_999, settings.RetentionCapBytes);
        Assert.Equal(7, settings.RetentionDays);
        Assert.Equal(9_999, retention.CapBytes);
        Assert.Equal(7, retention.RetentionDays);
    }

    [Fact]
    public void AddPinnedField_rejects_an_invalid_name_without_touching_the_session_store()
    {
        var settings = new AppSettings();
        using var store = new SessionStore(_root);
        var vm = new SettingsViewModel(settings, () => store, () => null, _ => { })
        {
            NewPinnedFieldName = "has space",
            NewPinnedFieldPath = "$.x",
        };

        vm.AddPinnedFieldCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Empty(settings.PinnedFields);
        Assert.Empty(store.PinnedFields);
    }

    [Fact]
    public void AddPinnedField_rejects_an_invalid_json_path()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings, () => null, () => null, _ => { })
        {
            NewPinnedFieldName = "region",
            NewPinnedFieldPath = "not a path",
        };

        vm.AddPinnedFieldCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Empty(settings.PinnedFields);
    }

    [Fact]
    public void AddPinnedField_applies_to_a_running_session_store_and_persists_to_settings()
    {
        var settings = new AppSettings();
        using var store = new SessionStore(_root);
        var vm = new SettingsViewModel(settings, () => store, () => null, _ => { })
        {
            NewPinnedFieldName = "region",
            NewPinnedFieldPath = "$.region",
        };

        vm.AddPinnedFieldCommand.Execute(null);

        Assert.False(vm.HasValidationError);
        Assert.Single(settings.PinnedFields);
        Assert.Single(store.PinnedFields);
        Assert.Equal("region", store.PinnedFields[0].Name);
        // The form clears for the next entry.
        Assert.Equal(string.Empty, vm.NewPinnedFieldName);
        Assert.Equal(string.Empty, vm.NewPinnedFieldPath);
    }

    [Fact]
    public void AddPinnedField_rejects_a_duplicate_name()
    {
        var settings = new AppSettings();
        settings.PinnedFields.Add(new PinnedFieldSetting("region", "$.region"));
        var vm = new SettingsViewModel(settings, () => null, () => null, _ => { })
        {
            NewPinnedFieldName = "region",
            NewPinnedFieldPath = "$.otherPath",
        };

        vm.AddPinnedFieldCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Single(settings.PinnedFields); // still just the original
    }

    [Fact]
    public void AddPinnedField_works_before_any_session_is_running()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings, () => null, () => null, _ => { })
        {
            NewPinnedFieldName = "region",
            NewPinnedFieldPath = "$.region",
        };

        vm.AddPinnedFieldCommand.Execute(null);

        Assert.False(vm.HasValidationError);
        Assert.Single(settings.PinnedFields); // saved for the next connection to pick up
    }
}
