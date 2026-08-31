using Confluent.Kafka;

namespace EventScope.Brokers.Kafka.Tests;

/// <summary>
/// Hand-rolled <see cref="IProducer{TKey,TValue}"/> test double — same style as
/// <see cref="FakeKafkaConsumer"/> rather than a mocking package. Every member
/// <see cref="KafkaEventSink"/> never touches throws <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class FakeKafkaProducer : IProducer<byte[], byte[]>
{
    private readonly Queue<Exception> _exceptions = new();

    public List<(string Topic, Message<byte[], byte[]> Message)> Produced { get; } = [];

    public bool Flushed { get; private set; }

    public bool Disposed { get; private set; }

    public void EnqueueThrow(Exception exception) => _exceptions.Enqueue(exception);

    public Task<DeliveryResult<byte[], byte[]>> ProduceAsync(
        string topic, Message<byte[], byte[]> message, CancellationToken cancellationToken = default)
    {
        if (_exceptions.Count > 0) throw _exceptions.Dequeue();

        Produced.Add((topic, message));
        return Task.FromResult(new DeliveryResult<byte[], byte[]> { Topic = topic, Message = message });
    }

    public int Flush(TimeSpan timeout)
    {
        Flushed = true;
        return 0;
    }

    public void Dispose() => Disposed = true;

    // --- Everything below is surface KafkaEventSink never calls. ---

    public string Name => throw new NotSupportedException();

    public Handle Handle => throw new NotSupportedException();

    public int AddBrokers(string brokers) => throw new NotSupportedException();

    public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

    public Task<DeliveryResult<byte[], byte[]>> ProduceAsync(
        TopicPartition topicPartition, Message<byte[], byte[]> message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public void Produce(
        string topic, Message<byte[], byte[]> message, Action<DeliveryReport<byte[], byte[]>>? deliveryHandler = null) =>
        throw new NotSupportedException();

    public void Produce(
        TopicPartition topicPartition, Message<byte[], byte[]> message, Action<DeliveryReport<byte[], byte[]>>? deliveryHandler = null) =>
        throw new NotSupportedException();

    public int Poll(TimeSpan timeout) => throw new NotSupportedException();

    public void Flush(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public void InitTransactions(TimeSpan timeout) => throw new NotSupportedException();

    public void BeginTransaction() => throw new NotSupportedException();

    public void CommitTransaction(TimeSpan timeout) => throw new NotSupportedException();

    public void CommitTransaction() => throw new NotSupportedException();

    public void AbortTransaction(TimeSpan timeout) => throw new NotSupportedException();

    public void AbortTransaction() => throw new NotSupportedException();

    public void SendOffsetsToTransaction(
        IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout) =>
        throw new NotSupportedException();
}
