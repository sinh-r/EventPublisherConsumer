using EventScope.App.Connections;
using EventScope.App.ViewModels;
using EventScope.Brokers.Kafka;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>No Avalonia dependency (plain <c>ObservableObject</c>/<c>RelayCommand</c>), needs
/// no <see cref="HeadlessFixture"/>. Persistence is captured via a delegate rather than a real
/// <see cref="ConnectionStore"/> file, matching <c>SettingsViewModelTests</c>' own style for
/// the equivalent settings-persistence seam.</summary>
public sealed class ConnectionManagerViewModelTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ConnectionManagerViewModel Build(
        out List<IReadOnlyList<ConnectionProfile>> persisted,
        IReadOnlyList<ConnectionProfile>? initial = null,
        Func<KafkaConnectionTestOptions, TimeSpan, KafkaConnectionTestResult>? tester = null,
        TimeProvider? timeProvider = null)
    {
        var log = new List<IReadOnlyList<ConnectionProfile>>();
        persisted = log;
        return new ConnectionManagerViewModel(initial ?? [], profiles => log.Add(profiles), tester, timeProvider);
    }

    [Fact]
    public void The_fake_source_is_always_present_first_and_not_editable()
    {
        var vm = Build(out _);

        var fake = Assert.Single(vm.SavedConnections, c => c.Id == ConnectionProfile.FakeSourceId);
        Assert.Equal(0, vm.SavedConnections.IndexOf(fake));
        Assert.False(fake.IsEditable);
    }

    [Fact]
    public void Saving_without_required_fields_reports_a_validation_error_and_does_not_add_a_connection()
    {
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.True(vm.IsEditing); // stays open so the user can fix it
        Assert.Empty(persisted);
    }

    [Fact]
    public void Saving_a_valid_new_connection_inserts_it_first_and_persists_without_the_fake_source()
    {
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        vm.EditName = "prod";
        vm.EditBootstrapServers = "broker:9092";
        vm.EditTopics = "orders";

        vm.SaveCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("prod", vm.SavedConnections[0].Name);
        Assert.Equal(ConnectionProfile.FakeSourceId, vm.SavedConnections[1].Id);

        var lastPersisted = Assert.Single(persisted);
        Assert.Single(lastPersisted);
        Assert.Equal("prod", lastPersisted[0].Name);
    }

    [Fact]
    public void An_unparsable_partition_is_rejected_before_save()
    {
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        vm.EditName = "prod";
        vm.EditBootstrapServers = "broker:9092";
        vm.EditTopics = "orders";
        vm.EditPartitionText = "not-a-number";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Empty(persisted);
    }

    [Fact]
    public void Editing_an_existing_connection_populates_fields_but_never_the_password()
    {
        var existing = new ConnectionProfile
        {
            Name = "prod",
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            Partition = 2,
            SaslPasswordProtected = ConnectionSecretProtector.Protect("old-secret"),
        };
        var vm = Build(out _, [existing]);

        vm.EditConnectionCommand.Execute(existing);

        Assert.Equal("prod", vm.EditName);
        Assert.Equal("broker:9092", vm.EditBootstrapServers);
        Assert.Equal("2", vm.EditPartitionText);
        Assert.Equal(string.Empty, vm.EditSaslPasswordInput);
        Assert.True(vm.EditHasExistingPassword);
        Assert.True(vm.IsEditingExisting);
    }

    [Fact]
    public void Saving_an_edit_with_a_blank_password_field_keeps_the_saved_password()
    {
        var protectedOriginal = ConnectionSecretProtector.Protect("old-secret");
        var existing = new ConnectionProfile
        {
            Name = "prod",
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            SaslPasswordProtected = protectedOriginal,
        };
        var vm = Build(out _, [existing]);
        vm.EditConnectionCommand.Execute(existing);
        vm.EditName = "prod-renamed";

        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(vm.SavedConnections, c => c.Kind == ConnectionKind.Kafka);
        Assert.Equal("prod-renamed", saved.Name);
        Assert.Equal(protectedOriginal, saved.SaslPasswordProtected);
    }

    [Fact]
    public void Saving_an_edit_with_a_new_password_replaces_the_saved_one()
    {
        var existing = new ConnectionProfile
        {
            Name = "prod",
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker:9092",
            Topics = "orders",
            SaslPasswordProtected = ConnectionSecretProtector.Protect("old-secret"),
        };
        var vm = Build(out _, [existing]);
        vm.EditConnectionCommand.Execute(existing);
        vm.EditSaslPasswordInput = "new-secret";

        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(vm.SavedConnections, c => c.Kind == ConnectionKind.Kafka);
        Assert.True(ConnectionSecretProtector.TryUnprotect(saved.SaslPasswordProtected, out var recovered));
        Assert.Equal("new-secret", recovered);
    }

    [Fact]
    public void Deleting_the_fake_source_is_a_no_op()
    {
        var vm = Build(out var persisted);
        var fake = vm.SavedConnections[0];

        vm.DeleteConnectionCommand.Execute(fake);

        Assert.Contains(vm.SavedConnections, c => c.Id == ConnectionProfile.FakeSourceId);
        Assert.Empty(persisted);
    }

    [Fact]
    public void Deleting_a_real_connection_removes_and_persists_it()
    {
        var existing = new ConnectionProfile { Name = "prod", Kind = ConnectionKind.Kafka };
        var vm = Build(out var persisted, [existing]);

        vm.DeleteConnectionCommand.Execute(existing);

        Assert.DoesNotContain(vm.SavedConnections, c => c.Name == "prod");
        Assert.Single(persisted);
        Assert.Empty(persisted[0]);
    }

    [Fact]
    public async Task Test_connection_reports_success_from_the_injected_tester()
    {
        var vm = Build(out _, tester: (_, _) => new KafkaConnectionTestResult(true, 3, 8, "2.15.0", null));
        vm.NewKafkaConnectionCommand.Execute(null);
        vm.EditName = "prod";
        vm.EditBootstrapServers = "broker:9092";
        vm.EditTopics = "orders";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsTestSuccess);
        Assert.Contains("3 broker", vm.TestResultText);
        Assert.Contains("8 partition", vm.TestResultText);
    }

    [Fact]
    public async Task Test_connection_reports_failure_from_the_injected_tester()
    {
        var vm = Build(out _, tester: (_, _) => KafkaConnectionTestResult.Failure("Broker transport failure"));
        vm.NewKafkaConnectionCommand.Execute(null);
        vm.EditName = "prod";
        vm.EditBootstrapServers = "bogus-host:9092";
        vm.EditTopics = "orders";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.IsTestFailure);
        Assert.Equal("Broker transport failure", vm.TestResultText);
    }

    [Fact]
    public void Test_connection_without_required_fields_reports_validation_instead_of_calling_the_tester()
    {
        var called = false;
        var vm = Build(out _, tester: (_, _) => { called = true; return KafkaConnectionTestResult.Failure("unused"); });
        vm.NewKafkaConnectionCommand.Execute(null);

        vm.TestConnectionCommand.Execute(null);

        Assert.False(called);
        Assert.True(vm.HasValidationError);
    }

    [Fact]
    public void Connecting_a_saved_profile_moves_it_to_the_front_with_a_fresh_last_used_timestamp()
    {
        var older = new ConnectionProfile { Name = "older", Kind = ConnectionKind.Kafka, LastUsedUtc = DateTime.UtcNow.AddDays(-5) };
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var vm = Build(out var persisted, [older], timeProvider: new FixedTimeProvider(now));

        ConnectionProfile? requested = null;
        vm.ConnectRequested += p => requested = p;

        vm.ConnectCommand.Execute(older);

        Assert.NotNull(requested);
        Assert.Equal(now.UtcDateTime, requested!.LastUsedUtc);
        Assert.Equal(0, vm.SavedConnections.IndexOf(requested));
        Assert.NotEmpty(persisted);
    }

    [Fact]
    public void Connecting_the_fake_source_never_reorders_or_persists()
    {
        var vm = Build(out var persisted);
        var fake = vm.SavedConnections[0];

        ConnectionProfile? requested = null;
        vm.ConnectRequested += p => requested = p;
        vm.ConnectCommand.Execute(fake);

        Assert.Same(fake, requested);
        Assert.Empty(persisted);
    }

    [Fact]
    public void Canceling_an_edit_leaves_saved_connections_unchanged()
    {
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        vm.EditName = "abandoned";

        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.DoesNotContain(vm.SavedConnections, c => c.Name == "abandoned");
        Assert.Empty(persisted);
    }

    // --- Start position ---

    private static void FillRequiredFields(ConnectionManagerViewModel vm, string name = "prod")
    {
        vm.EditName = name;
        vm.EditBootstrapServers = "broker:9092";
        vm.EditTopics = "orders";
    }

    [Fact]
    public void A_new_connection_defaults_to_tailing_from_now()
    {
        var vm = Build(out _);
        vm.NewKafkaConnectionCommand.Execute(null);

        Assert.Equal("Latest", vm.EditStartFrom);
        Assert.False(vm.IsStartFromEarliest);
    }

    [Fact]
    public void Starting_at_an_offset_without_a_partition_is_rejected_with_the_reason()
    {
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        FillRequiredFields(vm);
        vm.EditStartFrom = "Offset";
        vm.EditStartOffsetText = "12345";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Contains("per-partition", vm.ValidationError);
        Assert.Empty(persisted);
    }

    [Fact]
    public void An_unparseable_start_timestamp_is_rejected()
    {
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        FillRequiredFields(vm);
        vm.EditStartFrom = "Timestamp";
        vm.EditStartTimestampText = "last tuesday";

        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasValidationError);
        Assert.Empty(persisted);
    }

    [Fact]
    public void A_start_position_survives_save_and_reopening_the_editor()
    {
        // The failure this guards is a field missing from Clone: the form looks right, saves
        // right, and silently loses the value the next time the connection is edited.
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        FillRequiredFields(vm);
        vm.EditStartFrom = "Timestamp";
        vm.EditStartTimestampText = "2026-08-29 10:00:00";

        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(persisted[^1]);
        Assert.Equal("Timestamp", saved.StartFrom);
        Assert.Equal(new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc), saved.StartTimestampUtc);

        vm.EditConnectionCommand.Execute(saved);

        Assert.Equal("Timestamp", vm.EditStartFrom);
        Assert.Equal("2026-08-29 10:00:00", vm.EditStartTimestampText);
    }

    [Fact]
    public void Only_the_field_the_chosen_mode_uses_is_persisted()
    {
        // A leftover offset from trying one mode must not come back as a start position later.
        var vm = Build(out var persisted);
        vm.NewKafkaConnectionCommand.Execute(null);
        FillRequiredFields(vm);
        vm.EditPartitionText = "3";
        vm.EditStartOffsetText = "999";
        vm.EditStartFrom = "Earliest";

        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(persisted[^1]);
        Assert.Equal("Earliest", saved.StartFrom);
        Assert.Null(saved.StartOffset);
        Assert.Null(saved.StartTimestampUtc);
    }

    /// <summary>
    /// A build handed to someone else hides the Fake source: it is a synthetic stream dressed as
    /// a saved connection, and to a new user it reads as real traffic from a broker they never
    /// configured. <see cref="Settings.DeveloperOptions.ShowFakeSource"/> decides, and is false
    /// in Release. Everything else about the list is unchanged — this is a display decision, not
    /// a capability one, so the profile, its <c>ConnectionKind</c> and its event source all still
    /// exist and still resolve.
    /// </summary>
    [Fact]
    public void The_fake_source_can_be_left_out_without_disturbing_the_saved_connections()
    {
        var saved = new List<ConnectionProfile>
        {
            new() { Name = "prod", Kind = ConnectionKind.Kafka },
            new() { Name = "staging", Kind = ConnectionKind.Kafka },
        };

        var log = new List<IReadOnlyList<ConnectionProfile>>();
        var vm = new ConnectionManagerViewModel(
            saved, profiles => log.Add(profiles), kafkaTester: null, timeProvider: null,
            includeFakeSource: false);

        Assert.DoesNotContain(vm.SavedConnections, c => c.Id == ConnectionProfile.FakeSourceId);
        Assert.Equal(["prod", "staging"], vm.SavedConnections.Select(c => c.Name));
    }

    /// <summary>The default keeps every existing caller — and every other test in this file,
    /// several of which index <c>SavedConnections[0]</c> expecting it — working untouched.</summary>
    [Fact]
    public void Omitting_the_flag_still_includes_the_fake_source()
    {
        var vm = Build(out _, initial: [new ConnectionProfile { Name = "prod", Kind = ConnectionKind.Kafka }]);

        Assert.Equal(ConnectionProfile.FakeSourceId, vm.SavedConnections[0].Id);
        Assert.Equal("prod", vm.SavedConnections[1].Name);
    }
}
