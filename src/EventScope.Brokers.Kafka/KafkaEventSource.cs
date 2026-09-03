using System.Threading.Channels;
using Confluent.Kafka;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;

namespace EventScope.Brokers.Kafka;

/// <summary>
/// <see cref="IEventSource"/> over a Kafka topic. Per the build plan's threading table
/// (§3.6), <c>Consume()</c> is blocking sync, so the consume loop owns a dedicated
/// <see cref="TaskCreationOptions.LongRunning"/> thread rather than running on the thread
/// pool.
/// </summary>
/// <remarks>
/// <para><b>Throwaway consumer group.</b> Each instance generates its own random group id
/// (<see cref="KafkaSourceOptions.GroupIdPrefix"/> + a GUID) and never reuses one. Combined
/// with <c>enable.auto.commit=false</c> and <c>auto.offset.reset=latest</c>, that is what
/// makes this tool safe to point at a real topic: a fresh group has no committed offsets, so
/// "latest" means "tail from now", auto-commit is off so nothing is ever written back to the
/// broker, and no other consumer's partition assignment or lag is affected by this one
/// existing.</para>
/// <para><b>Why the channel write blocks synchronously inside the loop.</b> This is the
/// back-pressure edge, not sync-over-async sloppiness: the loop owns a dedicated thread (see
/// <see cref="RunAsync"/>), so blocking it costs nothing else. A blocked write means
/// <c>Consume()</c> is not called again until the reader drains, so lag builds on the broker
/// and nothing is dropped — exactly what the byte budget's broker-to-disk back-pressure
/// (build plan §3.2) requires. Making this loop <c>async</c> would hop every message onto the
/// thread pool and throw the dedicated thread away for no benefit.</para>
/// </remarks>
public sealed class KafkaEventSource : IEventSource
{
    private readonly KafkaSourceOptions _options;
    private readonly Func<ConsumerConfig, IConsumer<byte[], byte[]>> _consumerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly string _groupId;
    private IConsumer<byte[], byte[]>? _consumer;

    public KafkaEventSource(
        KafkaSourceOptions options,
        Func<ConsumerConfig, IConsumer<byte[], byte[]>>? consumerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _groupId = $"{options.GroupIdPrefix}-{Guid.NewGuid():N}";

        _consumerFactory = consumerFactory ?? (config => new ConsumerBuilder<byte[], byte[]>(config)
            .SetErrorHandler((_, error) => ErrorOccurred?.Invoke(new SourceError(error.Reason, error.IsFatal)))
            // Where a non-default start position takes effect on the Subscribe path: partitions
            // are not known until the group assigns them, and returning offsets from this handler
            // is how librdkafka is told where to begin. See ResolveStartOffsets' remarks on why it
            // is exposed rather than inlined here.
            .SetPartitionsAssignedHandler((consumer, partitions) => ResolveStartOffsets(consumer, partitions))
            .Build());
    }

    /// <summary>
    /// The starting offsets for a freshly assigned set of partitions.
    ///
    /// <para>
    /// Internal rather than a local lambda because it is otherwise untestable without a broker:
    /// <c>SetPartitionsAssignedHandler</c> lives on <see cref="ConsumerBuilder"/>, and a test that
    /// injects its own consumer factory bypasses the builder entirely, so the handler is never
    /// attached. Widening the factory seam instead would have changed every call site. What this
    /// does *not* prove is that librdkafka honours the returned offsets across a real rebalance —
    /// only an integration test against a broker can show that.
    /// </para>
    /// </summary>
    internal IReadOnlyList<TopicPartitionOffset> ResolveStartOffsets(
        IConsumer<byte[], byte[]> consumer, IReadOnlyList<TopicPartition> partitions) =>
        KafkaStartOffsets.Resolve(
            partitions,
            _options,
            query => consumer.OffsetsForTimes(query, _options.OffsetLookupTimeout));

    public string DisplayName => "Kafka";

    /// <summary>Raised for a non-fatal client error (from the consumer's own error handler,
    /// or a <see cref="ConsumeException"/> whose <see cref="Error.IsFatal"/> is false) — the
    /// loop keeps running afterward. The App wires this into the toolbar's status label.</summary>
    public event Action<SourceError>? ErrorOccurred;

    public SourceCapabilities Capabilities { get; } = new()
    {
        // enable.auto.commit=false means reading never advances anything the broker
        // remembers — differs from FakeEventSource only in flags below, but this one matters
        // most: it is the property that makes this source safe to run at all.
        CanPeekNonDestructively = true,
        SupportsPartitions = true,
        SupportsSubscriptions = false,
        SupportsSessions = false,
        // Kafka has no native dead-letter concept, unlike FakeEventSource's synthetic one.
        SupportsDeadLetterQueue = false,
        // Real: a start position seeks assigned partitions to a beginning, timestamp or explicit
        // offset (KafkaStartOffsets). FakeEventSource cannot replay anything.
        SupportsReplay = true,
        SupportsOffsetCommit = true,
    };

    public Task RunAsync(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => ConsumeLoop(destination, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void ConsumeLoop(ChannelWriter<RawMessage> destination, CancellationToken cancellationToken)
    {
        var config = BuildConsumerConfig();
        var consumer = _consumerFactory(config);
        _consumer = consumer;

        try
        {
            if (_options.Partition is { } partition)
            {
                // Assign, not Subscribe: an explicit partition means the caller wants exactly
                // that partition's messages, not whatever the group-rebalance protocol would
                // hand this throwaway group.
                var assigned = _options.Topics
                    .Select(t => new TopicPartition(t, new Partition(partition)))
                    .ToList();

                if (_options.StartFrom == KafkaStartFrom.Latest)
                {
                    // Unchanged from before start positions existed: no offsets, so
                    // AutoOffsetReset governs — and for a throwaway group with no committed
                    // offset that means tail from now.
                    consumer.Assign(assigned);
                }
                else
                {
                    // Partitions are already known here, so the seek happens inline rather than
                    // through the rebalance handler, which never fires for an explicit assignment.
                    consumer.Assign(ResolveStartOffsets(consumer, assigned));
                }
            }
            else
            {
                consumer.Subscribe(_options.Topics);
            }

            var timeoutMs = (int)_options.ConsumeTimeout.TotalMilliseconds;

            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<byte[], byte[]>? result;
                try
                {
                    result = consumer.Consume(timeoutMs);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    ErrorOccurred?.Invoke(new SourceError(ex.Error.Reason, IsFatal: false, ex));
                    continue;
                }

                if (result is null || result.IsPartitionEOF)
                {
                    continue;
                }

                var message = KafkaMessageMapper.Map(result, _timeProvider);

                // See the class remarks: blocking here is the deliberate back-pressure edge.
                destination.WriteAsync(message, cancellationToken).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        finally
        {
            // Close() (not just Dispose()) lets the group leave cleanly instead of waiting
            // out the broker's session timeout — safe to call here since the consume loop
            // has stopped calling Consume() by this point. Bounded to 2s: measured directly
            // against a bootstrap host that was never reachable, Close() can block far longer
            // than that trying to negotiate a graceful leave that can never succeed, which
            // held the whole app's shutdown hostage. On timeout the close attempt is simply
            // abandoned — DisposeAsync's own consumer.Dispose() still runs the real cleanup,
            // and a stray, already-abandoned Close() finishing later on a background thread is
            // harmless for a handle that's being torn down either way.
            try { Task.Run(() => consumer.Close()).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* best-effort on shutdown */ }
        }
    }

    private ConsumerConfig BuildConsumerConfig()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _groupId,
            EnableAutoCommit = false,
            // Earliest is expressed both here and as a seek. Belt and braces: a partition added
            // mid-run (a repartitioned topic) is assigned without going through this run's seek
            // decision, and the reset is what covers it.
            AutoOffsetReset = _options.StartFrom == KafkaStartFrom.Earliest
                ? Confluent.Kafka.AutoOffsetReset.Earliest
                : _options.AutoOffsetReset,
        };

        if (_options.SecurityProtocol is { } protocol) config.SecurityProtocol = protocol;
        if (_options.SaslMechanism is { } mechanism) config.SaslMechanism = mechanism;
        if (_options.SaslUsername is not null) config.SaslUsername = _options.SaslUsername;
        if (_options.SaslPassword is not null) config.SaslPassword = _options.SaslPassword;
        if (_options.SslCaLocation is not null) config.SslCaLocation = _options.SslCaLocation;

        return config;
    }

    public ValueTask DisposeAsync()
    {
        _consumer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
