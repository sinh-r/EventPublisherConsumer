using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Settings;
using EventScope.Storage.Retention;
using EventScope.Storage.Sqlite;

namespace EventScope.App.ViewModels;

/// <summary>
/// Drives the settings view (build plan §5 M2: "cap, retention, indexed prefix"). Cap and
/// retention days apply live via <see cref="RetentionService"/>'s settable properties; a
/// newly added pinned field applies live via <see cref="SessionStore.AddPinnedField"/> when
/// a session is running. The indexed-prefix change only takes effect for a fresh connection
/// (a new <see cref="Ingest.IngestPipeline"/>, i.e. after Stop/Start) — unlike the other two,
/// threading a live value into the ingest hot path for something this rarely changed isn't
/// worth the complexity, so this one setting is a deliberate exception to "everything applies
/// immediately."
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Func<SessionStore?> _sessionStoreProvider;
    private readonly Func<RetentionService?> _retentionServiceProvider;
    private readonly Action<AppSettings> _persist;

    [ObservableProperty]
    public partial long RetentionCapBytes { get; set; }

    /// <summary>A friendlier unit for the settings form — <see cref="RetentionCapBytes"/> is
    /// what everything else (RetentionService, persistence) actually uses.</summary>
    public double RetentionCapMegabytes
    {
        get => Math.Round(RetentionCapBytes / (1024.0 * 1024.0), 1);
        set => RetentionCapBytes = (long)(value * 1024 * 1024);
    }

    partial void OnRetentionCapBytesChanged(long value) => OnPropertyChanged(nameof(RetentionCapMegabytes));

    [ObservableProperty]
    public partial int RetentionDays { get; set; }

    [ObservableProperty]
    public partial int IndexedPrefixBytes { get; set; }

    [ObservableProperty]
    public partial string NewPinnedFieldName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPinnedFieldPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidationError { get; set; } = string.Empty;

    public bool HasValidationError => ValidationError.Length > 0;

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));

    [ObservableProperty]
    public partial string SavedMessage { get; set; } = string.Empty;

    public IReadOnlyList<PinnedFieldSetting> PinnedFields => _settings.PinnedFields;

    public SettingsViewModel(
        AppSettings settings,
        Func<SessionStore?> sessionStoreProvider,
        Func<RetentionService?> retentionServiceProvider,
        Action<AppSettings>? persist = null)
    {
        _settings = settings;
        _sessionStoreProvider = sessionStoreProvider;
        _retentionServiceProvider = retentionServiceProvider;
        _persist = persist ?? (s => s.Save());
        RetentionCapBytes = settings.RetentionCapBytes;
        RetentionDays = settings.RetentionDays;
        IndexedPrefixBytes = settings.IndexedPrefixBytes;
    }

    [RelayCommand]
    private void Save()
    {
        _settings.RetentionCapBytes = RetentionCapBytes;
        _settings.RetentionDays = RetentionDays;
        _settings.IndexedPrefixBytes = IndexedPrefixBytes;
        _persist(_settings);

        var retentionService = _retentionServiceProvider();
        if (retentionService is not null)
        {
            retentionService.CapBytes = RetentionCapBytes;
            retentionService.RetentionDays = RetentionDays;
        }

        SavedMessage = "Saved.";
    }

    [RelayCommand]
    private void AddPinnedField()
    {
        ValidationError = string.Empty;

        var name = NewPinnedFieldName.Trim();
        var path = NewPinnedFieldPath.Trim();

        if (!PinnedField.IsValidName(name))
        {
            ValidationError = "Field name must start with a letter or underscore and contain only letters, digits, and underscores.";
            return;
        }

        if (!PinnedField.IsValidJsonPath(path))
        {
            ValidationError = "JSON path must look like $.field or $.a.b[0].";
            return;
        }

        if (_settings.PinnedFields.Any(f => f.Name == name))
        {
            ValidationError = $"A pinned field named '{name}' already exists.";
            return;
        }

        var sessionStore = _sessionStoreProvider();
        sessionStore?.AddPinnedField(new PinnedField(name, path));

        _settings.PinnedFields.Add(new PinnedFieldSetting(name, path));
        _persist(_settings);
        OnPropertyChanged(nameof(PinnedFields));

        NewPinnedFieldName = string.Empty;
        NewPinnedFieldPath = string.Empty;
    }
}
