using System.Text;
using Confluent.Kafka;
using EventScope.Core.Models;

namespace EventScope.Brokers.Kafka;

/// <summary>
/// Maps a Kafka <see cref="ConsumeResult{TKey,TValue}"/> onto <see cref="RawMessage"/>. Kafka
/// has no native message-id or correlation-id concept, so the fallback chains below are the
/// one place this assembly makes an explicit decision instead of a direct field copy.
/// </summary>
internal static class KafkaMessageMapper
{
    private static readonly string[] MessageIdHeaderNames = ["message-id", "messageId", "ce-id"];
    private static readonly string[] CorrelationIdHeaderNames = ["correlation-id", "correlationId", "ce-correlationid"];

    public static RawMessage Map(ConsumeResult<byte[], byte[]> result, TimeProvider timeProvider)
    {
        var headers = result.Message.Headers;

        return new RawMessage
        {
            Body = result.Message.Value ?? [],
            EnqueuedTicks = result.Message.Timestamp.UtcDateTime.Ticks,
            ReceivedTicks = timeProvider.GetUtcNow().UtcTicks,
            MessageId = ResolveMessageId(headers, result.Message.Key, result.Partition.Value, result.Offset.Value),
            CorrelationId = FindHeader(headers, CorrelationIdHeaderNames),
            Subject = result.Topic,
            Partition = result.Partition.Value,
            // Kafka has no native dead-letter concept — matches Capabilities.SupportsDeadLetterQueue = false.
            IsDeadLettered = false,
            ApplicationProperties = ToPropertyDictionary(headers),
            SystemProperties = BuildSystemProperties(result),
        };
    }

    private static string ResolveMessageId(Headers? headers, byte[]? key, int partition, long offset)
    {
        var fromHeader = FindHeader(headers, MessageIdHeaderNames);
        if (fromHeader is not null) return fromHeader;

        if (key is { Length: > 0 })
        {
            return Encoding.UTF8.GetString(key);
        }

        return $"{partition}:{offset}";
    }

    /// <summary>Case-insensitive lookup by header key, trying each candidate name in priority
    /// order before falling through to the next — Kafka producers disagree on header casing
    /// and on which of the synonymous names they use.</summary>
    private static string? FindHeader(Headers? headers, IReadOnlyList<string> candidateNames)
    {
        if (headers is null || headers.Count == 0) return null;

        foreach (var candidate in candidateNames)
        {
            foreach (var header in headers)
            {
                if (string.Equals(header.Key, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
                }
            }
        }

        return null;
    }

    private static Dictionary<string, string>? ToPropertyDictionary(Headers? headers)
    {
        if (headers is null || headers.Count == 0) return null;

        var result = new Dictionary<string, string>(headers.Count);
        foreach (var header in headers)
        {
            result[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
        }

        return result;
    }

    private static Dictionary<string, string> BuildSystemProperties(ConsumeResult<byte[], byte[]> result) => new()
    {
        ["kafka.topic"] = result.Topic,
        ["kafka.partition"] = result.Partition.Value.ToString(),
        ["kafka.offset"] = result.Offset.Value.ToString(),
        ["kafka.timestampType"] = result.Message.Timestamp.Type.ToString(),
    };
}
