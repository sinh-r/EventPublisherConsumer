using Confluent.Kafka;
using Xunit;

namespace EventScope.Brokers.Kafka.Tests;

/// <summary>
/// <see cref="KafkaStartOffsets"/> — where almost all of the start position's behaviour lives, and
/// the part that can be proven without a broker. Pure: the timestamp lookup is a delegate, so these
/// tests script the broker's answer rather than needing one.
/// </summary>
public class KafkaStartOffsetsTests
{
    private static readonly TopicPartition P0 = new("orders", new Partition(0));
    private static readonly TopicPartition P1 = new("orders", new Partition(1));

    private static KafkaSourceOptions Options(KafkaStartFrom from, DateTimeOffset? at = null, long? offset = null) =>
        new()
        {
            BootstrapServers = "localhost:9092",
            Topics = ["orders"],
            StartFrom = from,
            StartTimestampUtc = at,
            StartOffset = offset,
        };

    /// <summary>Fails the test if the broker is consulted when it must not be.</summary>
    private static IReadOnlyList<TopicPartitionOffset> NeverCalled(IReadOnlyList<TopicPartitionTimestamp> _) =>
        throw new InvalidOperationException("the broker must not be consulted for this start position");

    [Fact]
    public void Latest_leaves_every_partition_unset_so_auto_offset_reset_governs()
    {
        // Unset - not Offset.End - is what keeps the default path byte-for-byte what it was before
        // start positions existed.
        var resolved = KafkaStartOffsets.Resolve([P0, P1], Options(KafkaStartFrom.Latest), NeverCalled);

        Assert.Equal([Offset.Unset, Offset.Unset], resolved.Select(r => r.Offset));
        Assert.Equal([P0, P1], resolved.Select(r => r.TopicPartition));
    }

    [Fact]
    public void Earliest_starts_every_partition_at_the_beginning()
    {
        var resolved = KafkaStartOffsets.Resolve([P0, P1], Options(KafkaStartFrom.Earliest), NeverCalled);

        Assert.All(resolved, r => Assert.Equal(Offset.Beginning, r.Offset));
    }

    [Fact]
    public void An_explicit_offset_is_applied_to_each_assigned_partition()
    {
        var resolved = KafkaStartOffsets.Resolve([P0], Options(KafkaStartFrom.Offset, offset: 12_345), NeverCalled);

        Assert.Equal(new Offset(12_345), Assert.Single(resolved).Offset);
    }

    [Fact]
    public void No_partitions_means_no_work_and_no_broker_call()
    {
        Assert.Empty(KafkaStartOffsets.Resolve([], Options(KafkaStartFrom.Timestamp, DateTimeOffset.UtcNow), NeverCalled));
    }

    [Fact]
    public void A_timestamp_is_asked_of_the_broker_for_every_partition()
    {
        var at = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        List<TopicPartitionTimestamp>? asked = null;

        var resolved = KafkaStartOffsets.Resolve([P0, P1], Options(KafkaStartFrom.Timestamp, at), query =>
        {
            asked = [.. query];
            return [new TopicPartitionOffset(P0, new Offset(80)), new TopicPartitionOffset(P1, new Offset(91))];
        });

        Assert.NotNull(asked);
        Assert.Equal([P0, P1], asked!.Select(q => q.TopicPartition));
        Assert.All(asked!, q => Assert.Equal(at.UtcDateTime, q.Timestamp.UtcDateTime));
        Assert.Equal([new Offset(80), new Offset(91)], resolved.Select(r => r.Offset));
    }

    [Fact]
    public void A_partition_the_broker_did_not_answer_for_tails_rather_than_replaying_everything()
    {
        // The dangerous direction is Beginning: silently turning "the last hour" into "the entire
        // retained topic". Failing toward End costs at most an empty grid.
        var resolved = KafkaStartOffsets.Resolve(
            [P0, P1],
            Options(KafkaStartFrom.Timestamp, DateTimeOffset.UtcNow),
            _ => [new TopicPartitionOffset(P0, new Offset(80))]);

        Assert.Equal(new Offset(80), resolved[0].Offset);
        Assert.Equal(Offset.End, resolved[1].Offset);
        Assert.NotEqual(Offset.Beginning, resolved[1].Offset);
    }

    [Fact]
    public void A_partition_with_no_message_at_or_after_the_timestamp_tails()
    {
        var resolved = KafkaStartOffsets.Resolve(
            [P0],
            Options(KafkaStartFrom.Timestamp, DateTimeOffset.UtcNow),
            _ => [new TopicPartitionOffset(P0, Offset.End)]);

        Assert.Equal(Offset.End, Assert.Single(resolved).Offset);
    }

    [Fact]
    public void An_unset_or_error_answer_tails_rather_than_replaying_everything()
    {
        var resolved = KafkaStartOffsets.Resolve(
            [P0, P1],
            Options(KafkaStartFrom.Timestamp, DateTimeOffset.UtcNow),
            _ =>
            [
                new TopicPartitionOffset(P0, Offset.Unset),
                new TopicPartitionOffset(P1, new Offset(-1001)),
            ]);

        Assert.All(resolved, r => Assert.Equal(Offset.End, r.Offset));
    }

    [Fact]
    public void Asking_for_a_timestamp_without_giving_one_tails_rather_than_replaying_everything()
    {
        var resolved = KafkaStartOffsets.Resolve([P0], Options(KafkaStartFrom.Timestamp, at: null), NeverCalled);

        Assert.Equal(Offset.End, Assert.Single(resolved).Offset);
    }
}
