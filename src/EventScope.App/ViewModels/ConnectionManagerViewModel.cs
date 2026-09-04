using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventScope.App.Connections;
using EventScope.App.Ingest;
using EventScope.Brokers.Kafka;

namespace EventScope.App.ViewModels;

/// <summary>The "Test connection" three states from UI spec §6 — idle/spinner/result
/// collapse onto this plus <see cref="ConnectionManagerViewModel.TestResultText"/>.</summary>
public enum ConnectionTestState
{
    Idle,
    Testing,
    Success,
    Failure,
}

/// <summary>
/// Drives the connection manager / launcher (UI spec §6): the saved-connections list and the
/// Kafka editor form. ASB and SQS render as disabled buttons with a tooltip in the view
/// rather than having editor state here — their sources don't exist until M4, and the plan's
/// "no <c>if (broker == …)</c> in the view layer" rule is about the workspace (toolbar, grid,
/// banners), not this per-broker editor, which the spec itself draws as three different forms.
/// </summary>
public partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<ConnectionProfile>> _persist;
    private readonly Func<KafkaConnectionTestOptions, TimeSpan, KafkaConnectionTestResult> _kafkaTester;
    private readonly TimeProvider _timeProvider;

    /// <summary>Most-recently-used first — connecting or saving a connection moves it to
    /// index 0 (see <see cref="Connect"/>/<see cref="Save"/>) rather than requiring a
    /// separately maintained sorted projection.</summary>
    public ObservableCollection<ConnectionProfile> SavedConnections { get; } = [];

    /// <summary>Raised when the user picks a saved connection to actually use — the
    /// connection manager only edits and tests; <see cref="MainWindowViewModel"/> owns what
    /// "connect" means (opening/selecting a tab).</summary>
    public event Action<ConnectionProfile>? ConnectRequested;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    private ConnectionProfile? _editingExisting;

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditBootstrapServers { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditTopics { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditPublishTopic { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditGroupIdPrefix { get; set; } = "eventscope";

    /// <summary>Blank means "all partitions" (<c>Subscribe</c>); a number means <c>Assign</c>
    /// to that one. A plain text field rather than a live broker-fetched dropdown — see the
    /// class-level scoping note in <c>Docs/PROGRESS.md</c>'s connection-manager entry.</summary>
    [ObservableProperty]
    public partial string EditPartitionText { get; set; } = string.Empty;

    /// <summary>A <c>StartFromOptions</c> member name.</summary>
    [ObservableProperty]
    public partial string EditStartFrom { get; set; } = "Latest";

    /// <summary>UTC, <c>yyyy-MM-dd HH:mm:ss</c>. Only read when <see cref="EditStartFrom"/> is
    /// <c>Timestamp</c>.</summary>
    [ObservableProperty]
    public partial string EditStartTimestampText { get; set; } = string.Empty;

    /// <summary>Only read when <see cref="EditStartFrom"/> is <c>Offset</c>.</summary>
    [ObservableProperty]
    public partial string EditStartOffsetText { get; set; } = string.Empty;

    /// <summary>Drives the conditional timestamp/offset inputs and the Earliest warning.</summary>
    public bool IsStartFromTimestamp => EditStartFrom == "Timestamp";
    public bool IsStartFromOffset => EditStartFrom == "Offset";
    public bool IsStartFromEarliest => EditStartFrom == "Earliest";

    partial void OnEditStartFromChanged(string value)
    {
        OnPropertyChanged(nameof(IsStartFromTimestamp));
        OnPropertyChanged(nameof(IsStartFromOffset));
        OnPropertyChanged(nameof(IsStartFromEarliest));
    }

    [ObservableProperty]
    public partial string EditSecurityProtocol { get; set; } = NoneOption;

    [ObservableProperty]
    public partial string EditSaslMechanism { get; set; } = NoneOption;

    [ObservableProperty]
    public partial string EditSaslUsername { get; set; } = string.Empty;

    /// <summary>Plaintext while editing only — never what gets persisted directly. Left
    /// blank when editing an existing connection that already has a saved password; leaving
    /// it blank on Save keeps the existing protected value unchanged, same UX as any
    /// credential-manager "leave blank to keep current password" field.</summary>
    [ObservableProperty]
    public partial string EditSaslPasswordInput { get; set; } = string.Empty;

    public bool EditHasExistingPassword => _editingExisting?.SaslPasswordProtected is { Length: > 0 };

    public string PasswordWatermark => EditHasExistingPassword ? "leave blank to keep saved password" : "password";

    /// <summary>Distinguishes "editing a saved connection" from "creating a new one" — the
    /// form's Delete button only makes sense for the former.</summary>
    public bool IsEditingExisting => _editingExisting is not null;

    [ObservableProperty]
    public partial string EditSslCaLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ValidationError { get; set; } = string.Empty;

    public bool HasValidationError => ValidationError.Length > 0;

    partial void OnValidationErrorChanged(string value) => OnPropertyChanged(nameof(HasValidationError));

    [ObservableProperty]
    public partial ConnectionTestState TestState { get; set; } = ConnectionTestState.Idle;

    public bool IsTesting => TestState == ConnectionTestState.Testing;
    public bool IsTestSuccess => TestState == ConnectionTestState.Success;
    public bool IsTestFailure => TestState == ConnectionTestState.Failure;

    partial void OnTestStateChanged(ConnectionTestState value)
    {
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(IsTestSuccess));
        OnPropertyChanged(nameof(IsTestFailure));
    }

    [ObservableProperty]
    public partial string TestResultText { get; set; } = string.Empty;

    public const string NoneOption = "(default)";

    public static IReadOnlyList<string> SecurityProtocolOptions { get; } =
        [NoneOption, "Plaintext", "Ssl", "SaslPlaintext", "SaslSsl"];

    public static IReadOnlyList<string> SaslMechanismOptions { get; } =
        [NoneOption, "Plain", "ScramSha256", "ScramSha512", "Gssapi", "OAuthBearer"];

    /// <summary>KafkaStartFrom member names. Latest is first because it is the default and the
    /// only one that reads nothing already on the topic.</summary>
    public static IReadOnlyList<string> StartFromOptions { get; } =
        ["Latest", "Earliest", "Timestamp", "Offset"];

    /// <param name="includeFakeSource">Whether to prepend the built-in "Fake source" entry.
    /// Defaults to <see langword="true"/> so no existing caller has to think about it; the app
    /// passes <see cref="Settings.DeveloperOptions.ShowFakeSource"/>, which is
    /// <see langword="false"/> in a Release build. Only the list entry is affected — the profile
    /// and its event source still exist and still resolve.</param>
    public ConnectionManagerViewModel(
        IReadOnlyList<ConnectionProfile> initialConnections,
        Action<IReadOnlyList<ConnectionProfile>> persist,
        Func<KafkaConnectionTestOptions, TimeSpan, KafkaConnectionTestResult>? kafkaTester = null,
        TimeProvider? timeProvider = null,
        bool includeFakeSource = true)
    {
        _persist = persist;
        _kafkaTester = kafkaTester ?? ((options, timeout) => KafkaConnectionTester.Test(options, timeout));
        _timeProvider = timeProvider ?? TimeProvider.System;

        // First when present, and never persisted (see Persist()'s own filter) — callers never
        // need to remember to include it.
        if (includeFakeSource)
        {
            SavedConnections.Add(ConnectionProfile.CreateFakeSource());
        }

        foreach (var connection in initialConnections)
        {
            SavedConnections.Add(connection);
        }
    }

    [RelayCommand]
    private void NewKafkaConnection()
    {
        _editingExisting = null;
        EditName = string.Empty;
        EditBootstrapServers = string.Empty;
        EditTopics = string.Empty;
        EditPublishTopic = string.Empty;
        EditGroupIdPrefix = "eventscope";
        EditPartitionText = string.Empty;
        EditStartFrom = "Latest";
        EditStartTimestampText = string.Empty;
        EditStartOffsetText = string.Empty;
        EditSecurityProtocol = NoneOption;
        EditSaslMechanism = NoneOption;
        EditSaslUsername = string.Empty;
        EditSaslPasswordInput = string.Empty;
        EditSslCaLocation = string.Empty;
        ValidationError = string.Empty;
        TestState = ConnectionTestState.Idle;
        TestResultText = string.Empty;
        IsEditing = true;
        NotifyEditingStateChanged();
    }

    [RelayCommand]
    private void EditConnection(ConnectionProfile? profile)
    {
        if (profile is null || profile.Kind != ConnectionKind.Kafka) return;

        _editingExisting = profile;
        EditName = profile.Name;
        EditBootstrapServers = profile.BootstrapServers;
        EditTopics = profile.Topics;
        EditPublishTopic = profile.PublishTopic;
        EditGroupIdPrefix = profile.GroupIdPrefix;
        EditPartitionText = profile.Partition?.ToString() ?? string.Empty;
        EditStartFrom = profile.StartFrom ?? "Latest";
        EditStartTimestampText = profile.StartTimestampUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
        EditStartOffsetText = profile.StartOffset?.ToString() ?? string.Empty;
        EditSecurityProtocol = profile.SecurityProtocol ?? NoneOption;
        EditSaslMechanism = profile.SaslMechanism ?? NoneOption;
        EditSaslUsername = profile.SaslUsername ?? string.Empty;
        EditSaslPasswordInput = string.Empty;
        EditSslCaLocation = profile.SslCaLocation ?? string.Empty;
        ValidationError = string.Empty;
        TestState = ConnectionTestState.Idle;
        TestResultText = string.Empty;
        IsEditing = true;
        NotifyEditingStateChanged();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void DeleteConnection(ConnectionProfile? profile)
    {
        if (profile is null || profile.Id == ConnectionProfile.FakeSourceId) return;

        SavedConnections.Remove(profile);
        Persist();

        if (_editingExisting == profile)
        {
            IsEditing = false;
            _editingExisting = null;
            NotifyEditingStateChanged();
        }
    }

    /// <summary>Deletes whatever connection is currently open in the editor — the form's own
    /// Delete button, which only appears when editing a saved (not brand-new) connection.</summary>
    [RelayCommand]
    private void DeleteEditingConnection() => DeleteConnection(_editingExisting);

    private void NotifyEditingStateChanged()
    {
        OnPropertyChanged(nameof(EditHasExistingPassword));
        OnPropertyChanged(nameof(PasswordWatermark));
        OnPropertyChanged(nameof(IsEditingExisting));
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryValidate(out var error))
        {
            ValidationError = error;
            return;
        }

        ValidationError = string.Empty;
        var profile = BuildProfileFromEditFields(_editingExisting);

        if (_editingExisting is { } existing)
        {
            var index = SavedConnections.IndexOf(existing);
            if (index >= 0) SavedConnections[index] = profile;
        }
        else
        {
            SavedConnections.Insert(0, profile);
        }

        Persist();
        IsEditing = false;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!TryValidate(out var error))
        {
            ValidationError = error;
            return;
        }

        ValidationError = string.Empty;
        TestState = ConnectionTestState.Testing;
        TestResultText = string.Empty;

        var options = BuildTestOptions();

        // GetMetadata blocks synchronously for up to the timeout — Task.Run keeps that off
        // the UI thread so the "Testing…" state actually renders instead of the dispatcher
        // stalling for the same duration it's meant to be showing progress for.
        // ConfigureAwait(true) (matching SearchViewModel/PublisherViewModel's own pattern)
        // resumes on Avalonia's dispatcher in the real app, and simply on the thread pool with
        // no special handling under a plain test host — no SynchronizationContext needed
        // either way.
        var result = await Task.Run(() => _kafkaTester(options, TimeSpan.FromSeconds(10))).ConfigureAwait(true);
        ApplyTestResult(result);
    }

    private void ApplyTestResult(KafkaConnectionTestResult result)
    {
        if (result.Success)
        {
            TestState = ConnectionTestState.Success;
            TestResultText = result.PartitionCount is { } partitions
                ? $"Connected — {result.BrokerCount} broker(s), {partitions} partition(s), client {result.ClientVersion}."
                : $"Connected — {result.BrokerCount} broker(s), client {result.ClientVersion}.";
        }
        else
        {
            TestState = ConnectionTestState.Failure;
            TestResultText = result.ErrorMessage ?? "Connection failed.";
        }
    }

    /// <summary>Fires <see cref="ConnectRequested"/> and moves <paramref name="profile"/> to
    /// the front of <see cref="SavedConnections"/> with a fresh <c>LastUsedUtc</c> — the Fake
    /// source (<see cref="ConnectionProfile.FakeSourceId"/>) is never in this collection, so
    /// it's simply not reordered.</summary>
    [RelayCommand]
    private void Connect(ConnectionProfile? profile)
    {
        if (profile is null) return;

        if (profile.Id != ConnectionProfile.FakeSourceId)
        {
            var index = SavedConnections.IndexOf(profile);
            if (index >= 0)
            {
                var updated = Clone(profile);
                updated.LastUsedUtc = _timeProvider.GetUtcNow().UtcDateTime;
                SavedConnections.RemoveAt(index);
                SavedConnections.Insert(0, updated);
                Persist();
                profile = updated;
            }
        }

        ConnectRequested?.Invoke(profile);
    }

    private void Persist() => _persist(SavedConnections.Where(c => c.Id != ConnectionProfile.FakeSourceId).ToList());

    /// <summary>Parses the start timestamp as UTC. Invariant and exact rather than
    /// locale-dependent: a start position that means a different moment on a differently-configured
    /// machine is worse than one that refuses an ambiguous input.</summary>
    private bool TryParseStartTimestamp(out DateTime value) =>
        DateTime.TryParseExact(
            EditStartTimestampText?.Trim(),
            ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out value);

    private bool TryValidate(out string error)
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            error = "Name is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(EditBootstrapServers))
        {
            error = "Bootstrap servers is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(EditTopics))
        {
            error = "At least one topic is required.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(EditPartitionText) && !int.TryParse(EditPartitionText, out _))
        {
            error = "Partition must be a whole number, or blank for all partitions.";
            return false;
        }

        if (IsStartFromTimestamp && !TryParseStartTimestamp(out _))
        {
            error = "Start timestamp must be UTC in the form yyyy-MM-dd HH:mm:ss.";
            return false;
        }

        if (IsStartFromOffset)
        {
            if (!long.TryParse(EditStartOffsetText, out var offset) || offset < 0)
            {
                error = "Start offset must be a whole number of zero or more.";
                return false;
            }

            // Offsets are per-partition, so "start at 12345" across a subscribed topic would mean a
            // different message in every partition. Refuse rather than quietly do something odd.
            if (!int.TryParse(EditPartitionText, out _))
            {
                error = "Starting at an offset needs an explicit partition — offsets are per-partition.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private ConnectionProfile BuildProfileFromEditFields(ConnectionProfile? existing)
    {
        var profile = Clone(existing) ?? new ConnectionProfile { Kind = ConnectionKind.Kafka };

        profile.Name = EditName.Trim();
        profile.BootstrapServers = EditBootstrapServers.Trim();
        profile.Topics = EditTopics.Trim();
        profile.PublishTopic = EditPublishTopic.Trim();
        profile.GroupIdPrefix = string.IsNullOrWhiteSpace(EditGroupIdPrefix) ? "eventscope" : EditGroupIdPrefix.Trim();
        profile.Partition = int.TryParse(EditPartitionText, out var partition) ? partition : null;

        profile.StartFrom = EditStartFrom;
        // Only the field the chosen mode actually uses is persisted, so a half-filled input left
        // over from trying another mode cannot come back later as a surprise start position.
        profile.StartTimestampUtc = IsStartFromTimestamp && TryParseStartTimestamp(out var startAt) ? startAt : null;
        profile.StartOffset = IsStartFromOffset && long.TryParse(EditStartOffsetText, out var startOffset)
            ? startOffset
            : null;
        profile.SecurityProtocol = EditSecurityProtocol == NoneOption ? null : EditSecurityProtocol;
        profile.SaslMechanism = EditSaslMechanism == NoneOption ? null : EditSaslMechanism;
        profile.SaslUsername = string.IsNullOrWhiteSpace(EditSaslUsername) ? null : EditSaslUsername.Trim();
        profile.SslCaLocation = string.IsNullOrWhiteSpace(EditSslCaLocation) ? null : EditSslCaLocation.Trim();

        if (!string.IsNullOrEmpty(EditSaslPasswordInput))
        {
            profile.SaslPasswordProtected = ConnectionSecretProtector.Protect(EditSaslPasswordInput);
        }
        // else: leave profile.SaslPasswordProtected as copied from `existing` — an empty
        // password field on an edit means "don't change the saved password."

        return profile;
    }

    private KafkaConnectionTestOptions BuildTestOptions() => new()
    {
        BootstrapServers = EditBootstrapServers.Trim(),
        Topic = EditTopics.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(),
        SecurityProtocol = Enum.TryParse<Confluent.Kafka.SecurityProtocol>(
            EditSecurityProtocol == NoneOption ? null : EditSecurityProtocol, out var sp) ? sp : null,
        SaslMechanism = Enum.TryParse<Confluent.Kafka.SaslMechanism>(
            EditSaslMechanism == NoneOption ? null : EditSaslMechanism, out var sm) ? sm : null,
        SaslUsername = string.IsNullOrWhiteSpace(EditSaslUsername) ? null : EditSaslUsername.Trim(),
        // The live edit field already holds plaintext — testing needs no DPAPI round trip.
        SaslPassword = string.IsNullOrEmpty(EditSaslPasswordInput) ? null : EditSaslPasswordInput,
        SslCaLocation = string.IsNullOrWhiteSpace(EditSslCaLocation) ? null : EditSslCaLocation.Trim(),
    };

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(source))]
    private static ConnectionProfile? Clone(ConnectionProfile? source) => source is null ? null : new ConnectionProfile
    {
        Id = source.Id,
        Name = source.Name,
        Kind = source.Kind,
        LastUsedUtc = source.LastUsedUtc,
        BootstrapServers = source.BootstrapServers,
        Topics = source.Topics,
        PublishTopic = source.PublishTopic,
        GroupIdPrefix = source.GroupIdPrefix,
        Partition = source.Partition,
        StartFrom = source.StartFrom,
        StartTimestampUtc = source.StartTimestampUtc,
        StartOffset = source.StartOffset,
        SecurityProtocol = source.SecurityProtocol,
        SaslMechanism = source.SaslMechanism,
        SaslUsername = source.SaslUsername,
        SaslPasswordProtected = source.SaslPasswordProtected,
        SslCaLocation = source.SslCaLocation,
    };
}
