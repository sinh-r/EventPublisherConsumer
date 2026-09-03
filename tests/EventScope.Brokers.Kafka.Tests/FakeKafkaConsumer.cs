using Confluent.Kafka;

namespace EventScope.Brokers.Kafka.Tests;

/// <summary>
/// Hand-rolled <see cref="IConsumer{TKey,TValue}"/> test double — matches the existing style
/// in this repo (<c>ManualTicker</c>, <c>FiniteEventSource</c>) rather than pulling in a
/// mocking package. Scripted with a queue of results to hand back from <see cref="Consume"/>;
/// every member <see cref="KafkaEventSource"/> must never touch throws
/// <see cref="NotSupportedException"/>, which is half the point of this fake — it proves the
/// source's surface area, not just its happy path.
/// </summary>
internal sealed class FakeKafkaConsumer : IConsumer<byte[], byte[]>
{
    private readonly Lock _gate = new();
    private readonly Queue<ConsumeResult<byte[], byte[]>?> _results = new();
    private readonly List<string> _subscribedTopics = [];
    private readonly List<TopicPartition> _assignedPartitions = [];
    private readonly List<TopicPartitionOffset> _assignedOffsets = [];

    public int ConsumeCallCount { get; private set; }

    public bool Closed { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<string> SubscribedTopics => _subscribedTopics;

    /// <summary>Populated by <see cref="Assign(IEnumerable{TopicPartition})"/> — the explicit
    /// single-partition path <see cref="KafkaEventSource"/> takes when
    /// <see cref="KafkaSourceOptions.Partition"/> is set, instead of <see cref="Subscribe(IEnumerable{string})"/>.</summary>
    public IReadOnlyList<TopicPartition> AssignedPartitions => _assignedPartitions;

    /// <summary>Populated by <see cref="Assign(IEnumerable{TopicPartitionOffset})"/> — the path
    /// taken only when a non-default start position needs an explicit seek. Latest must leave this
    /// empty, which is how "the default path is unchanged" is actually proven.</summary>
    public IReadOnlyList<TopicPartitionOffset> AssignedOffsets => _assignedOffsets;

    /// <summary>Scripts <see cref="OffsetsForTimes"/>. Left null, that method keeps throwing, so a
    /// start position that must not consult the broker is proven not to.</summary>
    public List<TopicPartitionOffset>? OffsetsForTimesResult { get; set; }

    public List<TopicPartitionTimestamp>? OffsetsForTimesQuery { get; private set; }

    /// <summary>Queues a result (or null, simulating a poll timeout) to be returned by the
    /// next <see cref="Consume"/> call.</summary>
    public void Enqueue(ConsumeResult<byte[], byte[]>? result)
    {
        lock (_gate) _results.Enqueue(result);
    }

    private readonly Queue<Exception> _exceptions = new();

    /// <summary>Queues an exception to be thrown by the next <see cref="Consume"/> call.</summary>
    public void EnqueueThrow(Exception exception)
    {
        lock (_gate) _exceptions.Enqueue(exception);
    }

    public ConsumeResult<byte[], byte[]>? Consume(int millisecondsTimeout)
    {
        lock (_gate)
        {
            ConsumeCallCount++;

            if (_exceptions.Count > 0)
            {
                throw _exceptions.Dequeue();
            }

            return _results.Count > 0 ? _results.Dequeue() : null;
        }
    }

    public void Subscribe(IEnumerable<string> topics)
    {
        lock (_gate) _subscribedTopics.AddRange(topics);
    }

    public void Subscribe(string topic)
    {
        lock (_gate) _subscribedTopics.Add(topic);
    }

    public void Close() => Closed = true;

    public void Dispose() => Disposed = true;

    // --- Everything below is surface KafkaEventSource never calls. ---

    public string Name => throw new NotSupportedException();

    public Handle Handle => throw new NotSupportedException();

    public string MemberId => throw new NotSupportedException();

    public List<TopicPartition> Assignment => throw new NotSupportedException();

    public List<string> Subscription => throw new NotSupportedException();

    public IConsumerGroupMetadata ConsumerGroupMetadata => throw new NotSupportedException();

    public int AddBrokers(string brokers) => throw new NotSupportedException();

    public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

    public ConsumeResult<byte[], byte[]> Consume(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ConsumeResult<byte[], byte[]> Consume(TimeSpan timeout) => throw new NotSupportedException();

    public void Unsubscribe() => throw new NotSupportedException();

    public void Assign(TopicPartition partition) => throw new NotSupportedException();

    public void Assign(TopicPartitionOffset partition) => throw new NotSupportedException();

    public void Assign(IEnumerable<TopicPartitionOffset> partitions)
    {
        _assignedOffsets.AddRange(partitions);
        _assignedPartitions.AddRange(_assignedOffsets.Select(o => o.TopicPartition));
    }

    public void Assign(IEnumerable<TopicPartition> partitions)
    {
        lock (_gate) _assignedPartitions.AddRange(partitions);
    }

    public void IncrementalAssign(IEnumerable<TopicPartitionOffset> partitions) => throw new NotSupportedException();

    public void IncrementalAssign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void IncrementalUnassign(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void Unassign() => throw new NotSupportedException();

    public void StoreOffset(ConsumeResult<byte[], byte[]> result) => throw new NotSupportedException();

    public void StoreOffset(TopicPartitionOffset offset) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Commit() => throw new NotSupportedException();

    public void Commit(IEnumerable<TopicPartitionOffset> offsets) => throw new NotSupportedException();

    public void Commit(ConsumeResult<byte[], byte[]> result) => throw new NotSupportedException();

    public void Seek(TopicPartitionOffset tpo) => throw new NotSupportedException();

    public void Pause(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public void Resume(IEnumerable<TopicPartition> partitions) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Committed(TimeSpan timeout) => throw new NotSupportedException();

    public List<TopicPartitionOffset> Committed(IEnumerable<TopicPartition> partitions, TimeSpan timeout) =>
        throw new NotSupportedException();

    public Offset Position(TopicPartition partition) => throw new NotSupportedException();

    public List<TopicPartitionOffset> OffsetsForTimes(
        IEnumerable<TopicPartitionTimestamp> timestampsToSearch, TimeSpan timeout)
    {
        OffsetsForTimesQuery = [.. timestampsToSearch];
        return OffsetsForTimesResult ?? throw new NotSupportedException();
    }

    public WatermarkOffsets GetWatermarkOffsets(TopicPartition topicPartition) => throw new NotSupportedException();

    public WatermarkOffsets QueryWatermarkOffsets(TopicPartition topicPartition, TimeSpan timeout) =>
        throw new NotSupportedException();
}
