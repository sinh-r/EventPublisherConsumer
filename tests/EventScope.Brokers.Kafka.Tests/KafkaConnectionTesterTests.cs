using Confluent.Kafka;
using Xunit;

namespace EventScope.Brokers.Kafka.Tests;

/// <summary>Unit tests against a scripted <see cref="KafkaConnectionTester.MetadataFetcher"/>
/// delegate — no live broker needed, and no fake for the 20-member <see cref="IAdminClient"/>
/// interface either, since <see cref="Metadata"/> and its parts are plain, publicly
/// constructible types (confirmed by reflection before writing this seam, not assumed).</summary>
public sealed class KafkaConnectionTesterTests
{
    private static Metadata BuildMetadata(int brokerCount, TopicMetadata? topic = null) => new(
        brokers: Enumerable.Range(0, brokerCount).Select(i => new BrokerMetadata(i, $"broker{i}", 9092)).ToList(),
        topics: topic is null ? [] : [topic],
        originatingBrokerId: 0,
        originatingBrokerName: "broker0");

    private static TopicMetadata TopicWithPartitions(string name, int partitionCount) => new(
        topic: name,
        partitions: Enumerable.Range(0, partitionCount)
            .Select(i => new PartitionMetadata(i, leader: 0, replicas: [0], inSyncReplicas: [0], error: new Error(ErrorCode.NoError)))
            .ToList(),
        error: new Error(ErrorCode.NoError));

    private static TopicMetadata UnknownTopic(string name) => new(
        topic: name,
        partitions: [],
        error: new Error(ErrorCode.UnknownTopicOrPart, "Unknown topic or partition"));

    [Fact]
    public void A_reachable_cluster_with_the_topic_present_succeeds()
    {
        var options = new KafkaConnectionTestOptions { BootstrapServers = "broker:9092", Topic = "orders" };

        var result = KafkaConnectionTester.Test(
            options, TimeSpan.FromSeconds(5),
            (_, topic, _) => BuildMetadata(3, TopicWithPartitions(topic!, 4)));

        Assert.True(result.Success);
        Assert.Equal(3, result.BrokerCount);
        Assert.Equal(4, result.PartitionCount);
        Assert.NotNull(result.ClientVersion);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void No_topic_requested_reports_broker_count_only()
    {
        var options = new KafkaConnectionTestOptions { BootstrapServers = "broker:9092", Topic = null };

        var result = KafkaConnectionTester.Test(
            options, TimeSpan.FromSeconds(5),
            (_, topic, _) => BuildMetadata(2));

        Assert.True(result.Success);
        Assert.Equal(2, result.BrokerCount);
        Assert.Null(result.PartitionCount);
    }

    [Fact]
    public void An_unknown_topic_fails_with_the_broker_reported_reason()
    {
        var options = new KafkaConnectionTestOptions { BootstrapServers = "broker:9092", Topic = "missing-topic" };

        var result = KafkaConnectionTester.Test(
            options, TimeSpan.FromSeconds(5),
            (_, topic, _) => BuildMetadata(1, UnknownTopic(topic!)));

        Assert.False(result.Success);
        Assert.Contains("Unknown topic", result.ErrorMessage);
    }

    [Fact]
    public void An_unreachable_broker_fails_with_the_kafka_exception_reason_not_a_thrown_exception()
    {
        var options = new KafkaConnectionTestOptions { BootstrapServers = "bogus-host:9092", Topic = "orders" };

        var result = KafkaConnectionTester.Test(
            options, TimeSpan.FromSeconds(5),
            (_, _, _) => throw new KafkaException(new Error(ErrorCode.Local_Transport, "Broker transport failure")));

        Assert.False(result.Success);
        Assert.Equal(0, result.BrokerCount);
        Assert.Equal("Broker transport failure", result.ErrorMessage);
    }

    [Fact]
    public void Security_and_sasl_options_reach_the_admin_client_config()
    {
        AdminClientConfig? captured = null;
        var options = new KafkaConnectionTestOptions
        {
            BootstrapServers = "broker:9092",
            SecurityProtocol = SecurityProtocol.SaslSsl,
            SaslMechanism = SaslMechanism.ScramSha256,
            SaslUsername = "svc",
            SaslPassword = "s3cret",
        };

        KafkaConnectionTester.Test(options, TimeSpan.FromSeconds(5), (config, _, _) =>
        {
            captured = config;
            return BuildMetadata(1);
        });

        Assert.NotNull(captured);
        Assert.Equal("broker:9092", captured!.BootstrapServers);
        Assert.Equal(SecurityProtocol.SaslSsl, captured.SecurityProtocol);
        Assert.Equal(SaslMechanism.ScramSha256, captured.SaslMechanism);
        Assert.Equal("svc", captured.SaslUsername);
        Assert.Equal("s3cret", captured.SaslPassword);
    }
}
