using Confluent.Kafka;

namespace EventScope.Brokers.Kafka;

/// <summary>Where a consume run begins.</summary>
public enum KafkaStartFrom
{
    /// <summary>Tail from now — nothing already on the topic is read. The default, and what this
    /// tool did exclusively before start positions existed.</summary>
    Latest,

    /// <summary>Everything the topic still retains, oldest first.</summary>
    Earliest,

    /// <summary>The first message at or after <see cref="KafkaSourceOptions.StartTimestampUtc"/>,
    /// resolved per partition by the broker.</summary>
    Timestamp,

    /// <summary>An explicit offset. Only meaningful with an explicit partition — offsets are
    /// per-partition, so the same number means a different message in each.</summary>
    Offset,
}

/// <summary>
/// Works out the offset to start each assigned partition at. Pure: no consumer, no broker, no I/O
/// beyond the <paramref name="offsetsForTimes"/> delegate the caller supplies — which is what makes
/// the interesting cases testable without a broker, and this is where nearly all of the start
/// position's behaviour actually lives.
/// </summary>
public static class KafkaStartOffsets
{
    /// <summary>
    /// The starting <see cref="TopicPartitionOffset"/> for each of <paramref name="partitions"/>.
    /// </summary>
    /// <param name="offsetsForTimes">The broker lookup used for
    /// <see cref="KafkaStartFrom.Timestamp"/>. Never called for the other modes.</param>
    public static IReadOnlyList<TopicPartitionOffset> Resolve(
        IReadOnlyList<TopicPartition> partitions,
        KafkaSourceOptions options,
        Func<IReadOnlyList<TopicPartitionTimestamp>, IReadOnlyList<TopicPartitionOffset>> offsetsForTimes)
    {
        if (partitions.Count == 0) return [];

        switch (options.StartFrom)
        {
            case KafkaStartFrom.Latest:
                // Offset.Unset leaves the starting position to auto.offset.reset, which is exactly
                // the behaviour this source had before start positions existed - so the default
                // path is unchanged rather than merely equivalent.
                return [.. partitions.Select(p => new TopicPartitionOffset(p, Offset.Unset))];

            case KafkaStartFrom.Earliest:
                return [.. partitions.Select(p => new TopicPartitionOffset(p, Offset.Beginning))];

            case KafkaStartFrom.Offset:
            {
                var offset = new Offset(options.StartOffset ?? Offset.End.Value);
                return [.. partitions.Select(p => new TopicPartitionOffset(p, offset))];
            }

            case KafkaStartFrom.Timestamp:
                return ResolveByTimestamp(partitions, options, offsetsForTimes);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.StartFrom, "Unknown start position.");
        }
    }

    private static IReadOnlyList<TopicPartitionOffset> ResolveByTimestamp(
        IReadOnlyList<TopicPartition> partitions,
        KafkaSourceOptions options,
        Func<IReadOnlyList<TopicPartitionTimestamp>, IReadOnlyList<TopicPartitionOffset>> offsetsForTimes)
    {
        if (options.StartTimestampUtc is not { } startAt)
        {
            // Asking to start at a timestamp without giving one is a caller bug, but failing shut
            // (tail from now) is the safe direction - see the fallback reasoning below.
            return [.. partitions.Select(p => new TopicPartitionOffset(p, Offset.End))];
        }

        var timestamp = new Timestamp(startAt.UtcDateTime, TimestampType.CreateTime);
        var query = partitions.Select(p => new TopicPartitionTimestamp(p, timestamp)).ToList();

        var resolved = offsetsForTimes(query);
        var byPartition = resolved
            .Where(r => r is not null)
            .ToDictionary(r => r.TopicPartition, r => r.Offset);

        return
        [
            .. partitions.Select(p =>
                new TopicPartitionOffset(p, UsableOffsetOr(byPartition, p, fallback: Offset.End)))
        ];
    }

    /// <summary>
    /// The broker's answer for a partition, or <paramref name="fallback"/> when it did not give a
    /// usable one.
    ///
    /// <para>
    /// <b>The fallback is <see cref="Offset.End"/>, deliberately, and never
    /// <see cref="Offset.Beginning"/>.</b> A partition with no message at or after the requested
    /// time correctly has nothing to replay, and a partition the broker failed to answer for is
    /// unknown — in both cases starting at the beginning would silently turn "the last hour" into
    /// "the entire retained topic". Reading too little is a visibly empty grid; reading everything
    /// by accident floods the ingest path and churns the on-disk cap. Fail toward the quiet
    /// direction.
    /// </para>
    /// </summary>
    private static Offset UsableOffsetOr(
        IReadOnlyDictionary<TopicPartition, Offset> resolved, TopicPartition partition, Offset fallback)
    {
        if (!resolved.TryGetValue(partition, out var offset)) return fallback;

        // Unset and the error sentinel both mean "no usable answer".
        if (offset == Offset.Unset || offset.Value < 0) return fallback;

        return offset;
    }
}
