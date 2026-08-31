using System.Text;
using System.Text.Json.Nodes;
using EventScope.Core.Models;
using Xunit;

namespace EventScope.Brokers.Kafka.Tests;

public sealed class KafkaEventSinkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (KafkaEventSink Sink, FakeKafkaProducer Producer) CreateSink(string topic = "eventscope")
    {
        var producer = new FakeKafkaProducer();
        var sink = new KafkaEventSink(
            new KafkaSinkOptions { BootstrapServers = "localhost:9092", Topic = topic },
            _ => producer);
        return (sink, producer);
    }

    [Fact]
    public async Task PublishAsync_sends_the_body_as_utf8_json_to_the_configured_topic()
    {
        var (sink, producer) = CreateSink("orders");
        var message = new OutgoingMessage { Body = JsonNode.Parse("""{"id":"abc"}""")! };

        await sink.PublishAsync(message, Ct);

        var (topic, sent) = Assert.Single(producer.Produced);
        Assert.Equal("orders", topic);
        Assert.Equal("""{"id":"abc"}""", Encoding.UTF8.GetString(sent.Value));
    }

    [Fact]
    public async Task PublishAsync_maps_the_partition_key_to_the_kafka_message_key()
    {
        var (sink, producer) = CreateSink();
        var message = new OutgoingMessage { Body = JsonNode.Parse("{}")!, PartitionKey = "region-1" };

        await sink.PublishAsync(message, Ct);

        var sent = producer.Produced.Single().Message;
        Assert.Equal("region-1", Encoding.UTF8.GetString(sent.Key));
    }

    [Fact]
    public async Task PublishAsync_with_no_partition_key_sends_a_null_kafka_key()
    {
        var (sink, producer) = CreateSink();
        var message = new OutgoingMessage { Body = JsonNode.Parse("{}")! };

        await sink.PublishAsync(message, Ct);

        Assert.Null(producer.Produced.Single().Message.Key);
    }

    [Fact]
    public async Task PublishAsync_carries_content_type_and_correlation_id_as_headers()
    {
        var (sink, producer) = CreateSink();
        var message = new OutgoingMessage
        {
            Body = JsonNode.Parse("{}")!,
            ContentType = "application/json",
            CorrelationId = "corr-1",
        };

        await sink.PublishAsync(message, Ct);

        var headers = producer.Produced.Single().Message.Headers;
        Assert.Equal("application/json", Encoding.UTF8.GetString(headers.GetLastBytes("content-type")));
        Assert.Equal("corr-1", Encoding.UTF8.GetString(headers.GetLastBytes("correlation-id")));
    }

    [Fact]
    public async Task PublishAsync_carries_application_properties_as_headers()
    {
        var (sink, producer) = CreateSink();
        var message = new OutgoingMessage
        {
            Body = JsonNode.Parse("{}")!,
            ApplicationProperties = new Dictionary<string, string> { ["env"] = "staging" },
        };

        await sink.PublishAsync(message, Ct);

        var headers = producer.Produced.Single().Message.Headers;
        Assert.Equal("staging", Encoding.UTF8.GetString(headers.GetLastBytes("env")));
    }

    [Fact]
    public async Task PublishAsync_with_no_optional_fields_sends_no_headers_object_at_all()
    {
        var (sink, producer) = CreateSink();
        var message = new OutgoingMessage { Body = JsonNode.Parse("{}")! };

        await sink.PublishAsync(message, Ct);

        Assert.Null(producer.Produced.Single().Message.Headers);
    }

    [Fact]
    public async Task DisposeAsync_flushes_the_producer_before_disposing_it()
    {
        var (sink, producer) = CreateSink();

        await sink.DisposeAsync();

        Assert.True(producer.Flushed);
        Assert.True(producer.Disposed);
    }
}
