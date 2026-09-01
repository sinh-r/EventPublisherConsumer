using EventScope.App.Connections;
using Xunit;

namespace EventScope.App.Tests;

/// <summary>Plain JSON round-trip, defensive defaults, and the DPAPI secret-protection
/// contract — no Avalonia dependency, needs no <see cref="HeadlessFixture"/>. Mirrors
/// <c>AppSettingsTests</c>'s own pattern for the same reasons.</summary>
public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Directory.CreateTempSubdirectory("eventscope-connections-tests-").FullName, "connections.json");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);

    [Fact]
    public void Loading_a_missing_file_returns_an_empty_list()
    {
        var connections = ConnectionStore.Load(_path);

        Assert.Empty(connections);
    }

    [Fact]
    public void Saved_connections_round_trip_exactly()
    {
        var profile = new ConnectionProfile
        {
            Name = "prod-orders",
            Kind = ConnectionKind.Kafka,
            BootstrapServers = "broker1:9092,broker2:9092",
            Topics = "orders,orders-dlq",
            PublishTopic = "orders",
            GroupIdPrefix = "eventscope-prod",
            Partition = 3,
            SecurityProtocol = "SaslSsl",
            SaslMechanism = "ScramSha512",
            SaslUsername = "svc-eventscope",
            SslCaLocation = "C:\\certs\\ca.pem",
        };

        ConnectionStore.Save([profile], _path);
        var loaded = ConnectionStore.Load(_path);

        var reloaded = Assert.Single(loaded);
        Assert.Equal(profile.Id, reloaded.Id);
        Assert.Equal("prod-orders", reloaded.Name);
        Assert.Equal(ConnectionKind.Kafka, reloaded.Kind);
        Assert.Equal("broker1:9092,broker2:9092", reloaded.BootstrapServers);
        Assert.Equal("orders,orders-dlq", reloaded.Topics);
        Assert.Equal("orders", reloaded.PublishTopic);
        Assert.Equal("eventscope-prod", reloaded.GroupIdPrefix);
        Assert.Equal(3, reloaded.Partition);
        Assert.Equal("SaslSsl", reloaded.SecurityProtocol);
        Assert.Equal("ScramSha512", reloaded.SaslMechanism);
        Assert.Equal("svc-eventscope", reloaded.SaslUsername);
        Assert.Equal("C:\\certs\\ca.pem", reloaded.SslCaLocation);
    }

    [Fact]
    public void A_corrupt_connections_file_falls_back_to_an_empty_list_rather_than_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not valid json");

        var connections = ConnectionStore.Load(_path);

        Assert.Empty(connections); // not a thrown exception
    }
}

/// <summary>The DPAPI round trip and its "never write plaintext" contract. Windows-only, like
/// the type under test — <see cref="ConnectionSecretProtector"/>'s own remarks explain why
/// that's acceptable for this desktop-only app.</summary>
public sealed class ConnectionSecretProtectorTests
{
    [Fact]
    public void A_protected_password_round_trips_exactly()
    {
        var protectedValue = ConnectionSecretProtector.Protect("hunter2-super-secret");

        Assert.NotNull(protectedValue);
        Assert.True(ConnectionSecretProtector.TryUnprotect(protectedValue, out var recovered));
        Assert.Equal("hunter2-super-secret", recovered);
    }

    [Fact]
    public void The_protected_value_never_contains_the_plaintext_password()
    {
        const string secret = "correct-horse-battery-staple";

        var protectedValue = ConnectionSecretProtector.Protect(secret);

        Assert.NotNull(protectedValue);
        Assert.DoesNotContain(secret, protectedValue);
    }

    [Fact]
    public void Unprotecting_a_null_or_empty_value_fails_without_throwing()
    {
        Assert.False(ConnectionSecretProtector.TryUnprotect(null, out var plaintext1));
        Assert.Equal(string.Empty, plaintext1);

        Assert.False(ConnectionSecretProtector.TryUnprotect(string.Empty, out var plaintext2));
        Assert.Equal(string.Empty, plaintext2);
    }

    [Fact]
    public void Unprotecting_a_corrupt_value_fails_without_throwing()
    {
        var ok = ConnectionSecretProtector.TryUnprotect("not-actually-protected-data==", out var plaintext);

        Assert.False(ok);
        Assert.Equal(string.Empty, plaintext);
    }
}
