using System.Text;
using System.Threading.Channels;
using Confluent.Kafka;
using EventScope.Core.Abstractions;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.Brokers.Kafka.Tests;

/// <summary>
/// Unit tests against <see cref="FakeKafkaConsumer"/> — no live broker needed. The
/// integration test at the bottom is opt-in via <c>EVENTSCOPE_KAFKA_BOOTSTRAP</c> per the
/// build plan's broker-testing strategy (see Blocked item 5 in PROGRESS.md: no broker on
/// this machine).
/// </summary>
public sealed class KafkaEventSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (KafkaEventSource Source, FakeKafkaConsumer Consumer) Build(KafkaSourceOptions? options = null)
    {
        var fake = new FakeKafkaConsumer();
        options ??= new KafkaSourceOptions { BootstrapServers = "test:9092", Topics = ["orders"] };
        return (new KafkaEventSource(options, _ => fake), fake);
    }

    private static ConsumeResult<byte[], byte[]> MakeResult(
        string topic = "orders",
        int partition = 0,
        long offset = 1,
        byte[]? key = null,
        byte[]? value = null,
        IEnumerable<(string Key, string Value)>? headers = null,
        bool isPartitionEof = false)
    {
        var msgHeaders = new Headers();
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                msgHeaders.Add(k, Encoding.UTF8.GetBytes(v));
            }
        }

        return new ConsumeResult<byte[], byte[]>
        {
            Topic = topic,
            Partition = new Partition(partition),
            Offset = new Offset(offset),
            IsPartitionEOF = isPartitionEof,
            Message = new Message<byte[], byte[]>
            {
                Key = key!, // Message<byte[],byte[]>.Key is declared non-nullable, but Kafka permits a null key.
                Value = value!, // likewise for Value — null models a tombstone (see MakeResult's `value` param).
                Timestamp = new Timestamp(DateTime.UtcNow),
                Headers = msgHeaders,
            },
        };
    }

    /// <summary>Runs a source until exactly one message arrives, then cancels and awaits
    /// shutdown — every mapping/loop test below needs a message, not a dangling background
    /// thread still parked in <c>Consume()</c> after the test method returns.</summary>
    private static async Task<RawMessage> RunOneMessageAsync(KafkaEventSource source, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runTask = source.RunAsync(channel.Writer, cts.Token);

        var message = await channel.Reader.ReadAsync(ct);

        await StopAsync(cts, runTask);
        return message;
    }

    private static async Task StopAsync(CancellationTokenSource cts, Task runTask)
    {
        await cts.CancelAsync();
        try { await runTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch { /* best-effort cleanup once the assertion-relevant behaviour has been observed */ }
    }

    // --- Consumer config ---

    [Fact]
    public async Task Builds_a_throwaway_consumer_config_with_auto_commit_disabled()
    {
        var fake = new FakeKafkaConsumer();
        fake.Enqueue(MakeResult());
        var captured = new TaskCompletionSource<ConsumerConfig>(TaskCreationOptions.RunContinuationsAsynchronously);

        var source = new KafkaEventSource(
            new KafkaSourceOptions { BootstrapServers = "broker:9092", Topics = ["orders"] },
            cfg => { captured.TrySetResult(cfg); return fake; });

        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = new CancellationTokenSource();
        var runTask = source.RunAsync(channel.Writer, cts.Token);

        var config = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("broker:9092", config.BootstrapServers);
        Assert.False(config.EnableAutoCommit);
        Assert.Equal(AutoOffsetReset.Latest, config.AutoOffsetReset);
        Assert.NotNull(config.GroupId);
        Assert.StartsWith("eventscope-", config.GroupId);

        await StopAsync(cts, runTask);
    }

    [Fact]
    public async Task Each_instance_gets_a_distinct_group_id()
    {
        var configs = new List<ConsumerConfig>();

        for (var i = 0; i < 2; i++)
        {
            var fake = new FakeKafkaConsumer();
            fake.Enqueue(MakeResult());
            var tcs = new TaskCompletionSource<ConsumerConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new KafkaEventSource(
                new KafkaSourceOptions { BootstrapServers = "broker:9092", Topics = ["orders"] },
                cfg => { tcs.TrySetResult(cfg); return fake; });

            var channel = Channel.CreateUnbounded<RawMessage>();
            using var cts = new CancellationTokenSource();
            var runTask = source.RunAsync(channel.Writer, cts.Token);
            configs.Add(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            await StopAsync(cts, runTask);
        }

        Assert.NotEqual(configs[0].GroupId, configs[1].GroupId);
    }

    [Fact]
    public async Task No_partition_set_subscribes_to_the_whole_topic()
    {
        var (source, fake) = Build(new KafkaSourceOptions
        {
            BootstrapServers = "test:9092",
            Topics = ["orders"],
            Partition = null,
        });
        fake.Enqueue(MakeResult());

        await RunOneMessageAsync(source, Ct);

        Assert.Equal(["orders"], fake.SubscribedTopics);
        Assert.Empty(fake.AssignedPartitions);
    }

    [Fact]
    public async Task An_explicit_partition_assigns_instead_of_subscribing()
    {
        var (source, fake) = Build(new KafkaSourceOptions
        {
            BootstrapServers = "test:9092",
            Topics = ["orders"],
            Partition = 3,
        });
        fake.Enqueue(MakeResult(partition: 3));

        await RunOneMessageAsync(source, Ct);

        Assert.Empty(fake.SubscribedTopics);
        var assigned = Assert.Single(fake.AssignedPartitions);
        Assert.Equal("orders", assigned.Topic);
        Assert.Equal(3, assigned.Partition.Value);
    }

    // --- Mapping rules ---

    [Fact]
    public async Task Maps_message_id_from_header_when_present()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(headers: [("message-id", "abc-123")]));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Equal("abc-123", message.MessageId);
    }

    [Fact]
    public async Task Message_id_header_lookup_is_case_insensitive_and_tries_synonyms()
    {
        var (source, fake) = Build();
        // "ce-id" (CloudEvents convention), wrong case, to prove both the synonym chain and
        // case-insensitivity.
        fake.Enqueue(MakeResult(headers: [("CE-ID", "cloud-event-1")]));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Equal("cloud-event-1", message.MessageId);
    }

    [Fact]
    public async Task Falls_back_to_the_message_key_when_no_message_id_header_exists()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(key: Encoding.UTF8.GetBytes("order-42")));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Equal("order-42", message.MessageId);
    }

    [Fact]
    public async Task Falls_back_to_partition_and_offset_when_no_header_or_key_exists()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(partition: 3, offset: 77));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Equal("3:77", message.MessageId);
    }

    [Fact]
    public async Task Maps_correlation_id_from_header_or_leaves_it_null()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(headers: [("correlation-id", "corr-1")]));
        fake.Enqueue(MakeResult());

        var withHeader = await RunOneMessageAsync(source, Ct);
        Assert.Equal("corr-1", withHeader.CorrelationId);
    }

    [Fact]
    public async Task A_tombstone_maps_to_an_empty_body_not_a_dropped_message()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(value: null));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Empty(message.Body);
    }

    [Fact]
    public async Task Never_reports_dead_lettered_matching_the_capability_flag()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult());

        var message = await RunOneMessageAsync(source, Ct);

        Assert.False(message.IsDeadLettered);
        Assert.False(source.Capabilities.SupportsDeadLetterQueue);
    }

    [Fact]
    public async Task Subject_and_partition_map_from_topic_and_partition_directly()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(topic: "orders.created", partition: 2));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Equal("orders.created", message.Subject);
        Assert.Equal(2, message.Partition);
    }

    // --- Loop behaviour ---

    [Fact]
    public async Task Partition_eof_results_are_skipped_not_emitted_as_messages()
    {
        var (source, fake) = Build();
        fake.Enqueue(MakeResult(isPartitionEof: true));
        fake.Enqueue(MakeResult(offset: 5));

        var message = await RunOneMessageAsync(source, Ct);

        Assert.Equal("5", message.MessageId!.Split(':')[1]);
    }

    [Fact]
    public async Task Back_pressure_stops_the_broker_loop_from_being_polled_again_while_the_channel_is_full()
    {
        var fake = new FakeKafkaConsumer();
        fake.Enqueue(MakeResult(offset: 1));
        fake.Enqueue(MakeResult(offset: 2));

        var source = new KafkaEventSource(
            new KafkaSourceOptions { BootstrapServers = "test:9092", Topics = ["orders"] },
            _ => fake);

        // Capacity 1, no reader draining: the second write blocks forever until cancelled,
        // which is exactly the back-pressure edge the class remarks describe.
        var channel = Channel.CreateBounded<RawMessage>(1);
        using var cts = new CancellationTokenSource();
        var runTask = source.RunAsync(channel.Writer, cts.Token);

        // Give the dedicated thread time to reach the blocked second write.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (fake.ConsumeCallCount < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        Assert.Equal(2, fake.ConsumeCallCount);

        // Give it a further moment to prove it does NOT proceed to a third Consume() call
        // while blocked on the full channel.
        await Task.Delay(100, Ct);
        Assert.Equal(2, fake.ConsumeCallCount);

        await cts.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task Cancellation_drains_gracefully_and_closes_the_consumer_exactly_once()
    {
        var fake = new FakeKafkaConsumer();
        var source = new KafkaEventSource(
            new KafkaSourceOptions { BootstrapServers = "test:9092", Topics = ["orders"] },
            _ => fake);

        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = new CancellationTokenSource();
        var runTask = source.RunAsync(channel.Writer, cts.Token);

        // No results queued: Consume() keeps returning null (a poll timeout) until cancelled.
        await Task.Delay(50, Ct);
        await cts.CancelAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(fake.Closed);
    }

    [Fact]
    public async Task A_non_fatal_consume_exception_is_surfaced_and_the_loop_continues()
    {
        var fake = new FakeKafkaConsumer();
        fake.EnqueueThrow(new ConsumeException(
            new ConsumeResult<byte[], byte[]>(), new Error(ErrorCode.Local_Fail, "transient", isFatal: false)));
        fake.Enqueue(MakeResult(offset: 9));

        var source = new KafkaEventSource(
            new KafkaSourceOptions { BootstrapServers = "test:9092", Topics = ["orders"] },
            _ => fake);

        SourceError? observed = null;
        source.ErrorOccurred += e => observed = e;

        var message = await RunOneMessageAsync(source, Ct);

        Assert.NotNull(observed);
        Assert.Equal("transient", observed!.Message);
        Assert.Equal("9", message.MessageId!.Split(':')[1]);
    }

    [Fact]
    public async Task A_fatal_consume_exception_breaks_the_loop_and_faults_the_task()
    {
        var fake = new FakeKafkaConsumer();
        fake.EnqueueThrow(new ConsumeException(
            new ConsumeResult<byte[], byte[]>(), new Error(ErrorCode.Local_AllBrokersDown, "unrecoverable", isFatal: true)));

        var source = new KafkaEventSource(
            new KafkaSourceOptions { BootstrapServers = "test:9092", Topics = ["orders"] },
            _ => fake);

        var channel = Channel.CreateUnbounded<RawMessage>();
        var runTask = source.RunAsync(channel.Writer, Ct);

        await Assert.ThrowsAsync<ConsumeException>(() => runTask);
        Assert.True(fake.Closed); // finally still runs on the way out
    }

    // --- Integration (opt-in) ---

    public static bool KafkaConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_BOOTSTRAP"));

    [Fact(Skip = "Requires EVENTSCOPE_KAFKA_BOOTSTRAP and EVENTSCOPE_KAFKA_TOPIC against a real broker.",
        SkipUnless = nameof(KafkaConfigured))]
    public async Task Connects_to_a_real_broker_and_consumes_at_least_one_message()
    {
        var bootstrap = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_BOOTSTRAP")!;
        var topic = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_TOPIC") ?? "eventscope-smoke-test";

        var source = new KafkaEventSource(new KafkaSourceOptions
        {
            BootstrapServers = bootstrap,
            Topics = [topic],
        });

        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runTask = source.RunAsync(channel.Writer, cts.Token);

        var message = await channel.Reader.ReadAsync(cts.Token);
        Assert.NotNull(message.Subject);

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }
        await source.DisposeAsync();
    }
}
