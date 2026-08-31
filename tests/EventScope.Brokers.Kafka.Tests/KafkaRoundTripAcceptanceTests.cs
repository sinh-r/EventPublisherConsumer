using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.Brokers.Kafka.Tests;

/// <summary>
/// The build plan §5 M3 step 10 round-trip acceptance criterion: "consume → 'Use as publish
/// template' → publish → consume back, assert the same shape." Needs a real broker, so this
/// is opt-in behind <c>EVENTSCOPE_KAFKA_BOOTSTRAP</c> and skips by default, exactly like
/// <see cref="KafkaEventSourceTests"/>'s own integration test — this codebase has no live
/// broker access to prove this against beyond that limitation, which is stated here rather
/// than implied as passing (see <c>Docs/PROGRESS.md</c>).
/// </summary>
public sealed class KafkaRoundTripAcceptanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool KafkaConfigured =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_BOOTSTRAP"));

    [Fact(Skip = "Requires EVENTSCOPE_KAFKA_BOOTSTRAP and EVENTSCOPE_KAFKA_TOPIC against a real broker.",
        SkipUnless = nameof(KafkaConfigured))]
    public async Task A_published_message_consumes_back_with_the_same_body_shape_and_correlation_id()
    {
        var bootstrap = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_BOOTSTRAP")!;
        var topic = Environment.GetEnvironmentVariable("EVENTSCOPE_KAFKA_TOPIC") ?? "eventscope-roundtrip-test";
        var correlationId = $"roundtrip-{Guid.NewGuid():N}";

        // "Consume -> Use as publish template" is simulated here by constructing the
        // OutgoingMessage directly with the shape PublisherTreeModel.ToJson would produce -
        // this test lives in EventScope.Brokers.Kafka.Tests (no EventScope.App reference,
        // matching every other broker test's isolation), so the tree/schema-inference side of
        // that workflow is covered separately by EventScope.App.Tests instead of duplicated
        // here against a real broker.
        var body = JsonNode.Parse("""{"orderId":"abc-123","total":42}""")!;

        await using (var sink = new KafkaEventSink(new KafkaSinkOptions { BootstrapServers = bootstrap, Topic = topic }))
        {
            await sink.PublishAsync(new OutgoingMessage { Body = body, CorrelationId = correlationId }, Ct);
        }

        await using var source = new KafkaEventSource(new KafkaSourceOptions
        {
            BootstrapServers = bootstrap,
            Topics = [topic],
        });

        var channel = Channel.CreateUnbounded<RawMessage>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        var runTask = source.RunAsync(channel.Writer, cts.Token);

        RawMessage received;
        while (true)
        {
            received = await channel.Reader.ReadAsync(cts.Token);
            if (received.CorrelationId == correlationId) break; // the topic may carry other traffic
        }

        await cts.CancelAsync();
        try { await runTask; } catch (OperationCanceledException) { }

        var receivedBody = JsonNode.Parse(Encoding.UTF8.GetString(received.Body));
        Assert.Equal("abc-123", receivedBody!["orderId"]!.GetValue<string>());
        Assert.Equal(42, receivedBody["total"]!.GetValue<int>());
        Assert.Equal(correlationId, received.CorrelationId);
    }
}
