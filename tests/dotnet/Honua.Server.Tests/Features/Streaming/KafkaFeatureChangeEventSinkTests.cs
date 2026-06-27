// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for the Kafka feature-change event sink (#357), verifying the
/// serialized payload/key/headers, the dead-letter routing on delivery failure,
/// and the surfaced-failure behaviour when dead-lettering is unavailable. The
/// sink is exercised against a fake <see cref="IKafkaEventProducer"/> so no live
/// broker is required.
/// </summary>
public sealed class KafkaFeatureChangeEventSinkTests
{
    private static FeatureChangeEvent SampleEvent(string eventId = "evt-1") => new()
    {
        EventId = eventId,
        Cursor = 42,
        Timestamp = DateTimeOffset.UnixEpoch,
        ServiceId = "parcels",
        LayerId = 3,
        ObjectId = 99,
        Operation = "update",
        Protocol = "rest",
        RequestId = "req-1"
    };

    private static KafkaFeatureChangeEventSink Create(
        IKafkaEventProducer producer,
        KafkaFeatureChangeEventSinkOptions options)
        => new(
            producer,
            Options.Create(options),
            NullLogger<KafkaFeatureChangeEventSink>.Instance);

    [UnitTest]
    public async Task PublishAsync_OnSuccess_ProducesToTopicWithKeyAndHeaders()
    {
        var producer = new RecordingProducer();
        var sink = Create(producer, new KafkaFeatureChangeEventSinkOptions
        {
            Topic = "feature-changes",
            DeadLetterTopic = "feature-changes.dlq",
        });

        await sink.PublishAsync(SampleEvent());

        var produced = Assert.Single(producer.Produced);
        Assert.Equal("feature-changes", produced.Topic);
        Assert.Equal("parcels/3/99", produced.Key);

        // Payload round-trips to the original event.
        var roundTripped = JsonSerializer.Deserialize(
            produced.Value,
            FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
        Assert.NotNull(roundTripped);
        Assert.Equal("evt-1", roundTripped!.EventId);

        Assert.Equal("evt-1", produced.HeaderValue("x-event-id"));
        Assert.Equal("update", produced.HeaderValue("x-operation"));
        Assert.Equal("application/json", produced.HeaderValue("content-type"));
    }

    [UnitTest]
    public async Task PublishAsync_WhenPrimaryFails_RoutesToDeadLetterTopic()
    {
        var producer = new RecordingProducer { FailTopic = "feature-changes" };
        var sink = Create(producer, new KafkaFeatureChangeEventSinkOptions
        {
            Topic = "feature-changes",
            DeadLetterTopic = "feature-changes.dlq",
        });

        // Must not throw: a routed-to-DLQ event is handled, not a sink failure.
        await sink.PublishAsync(SampleEvent());

        var dlq = Assert.Single(producer.Produced);
        Assert.Equal("feature-changes.dlq", dlq.Topic);
        Assert.Equal("parcels/3/99", dlq.Key);
        Assert.Equal("feature-changes", dlq.HeaderValue("x-dlq-source-topic"));
        Assert.False(string.IsNullOrEmpty(dlq.HeaderValue("x-dlq-error")));
        Assert.False(string.IsNullOrEmpty(dlq.HeaderValue("x-dlq-failed-at")));
    }

    [UnitTest]
    public async Task PublishAsync_WhenPrimaryFails_AndDlqDisabled_SurfacesFailure()
    {
        var producer = new RecordingProducer { FailTopic = "feature-changes" };
        var sink = Create(producer, new KafkaFeatureChangeEventSinkOptions
        {
            Topic = "feature-changes",
            DeadLetterTopic = null,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PublishAsync(SampleEvent()));
    }

    [UnitTest]
    public async Task PublishAsync_WhenPrimaryAndDlqFail_SurfacesOriginalFailure()
    {
        var producer = new RecordingProducer { FailAllTopics = true };
        var sink = Create(producer, new KafkaFeatureChangeEventSinkOptions
        {
            Topic = "feature-changes",
            DeadLetterTopic = "feature-changes.dlq",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PublishAsync(SampleEvent()));
    }

    [UnitTest]
    public void Validate_WhenEnabledWithoutBootstrapServers_Fails()
    {
        var validator = new KafkaFeatureChangeEventSinkOptionsValidator();

        var result = validator.Validate(null, new KafkaFeatureChangeEventSinkOptions
        {
            Enabled = true,
            BootstrapServers = null,
            Topic = "feature-changes",
        });

        Assert.True(result.Failed);
    }

    [UnitTest]
    public void Validate_WhenDeadLetterTopicEqualsTopic_Fails()
    {
        var validator = new KafkaFeatureChangeEventSinkOptionsValidator();

        var result = validator.Validate(null, new KafkaFeatureChangeEventSinkOptions
        {
            Enabled = true,
            BootstrapServers = "localhost:9092",
            Topic = "feature-changes",
            DeadLetterTopic = "feature-changes",
        });

        Assert.True(result.Failed);
    }

    [UnitTest]
    public void Validate_WhenDisabled_Succeeds()
    {
        var validator = new KafkaFeatureChangeEventSinkOptionsValidator();

        var result = validator.Validate(null, new KafkaFeatureChangeEventSinkOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    private sealed record ProducedMessage(
        string Topic,
        string Key,
        byte[] Value,
        IReadOnlyList<KeyValuePair<string, string>> Headers)
    {
        public string? HeaderValue(string key)
        {
            foreach (var header in Headers)
            {
                if (string.Equals(header.Key, key, StringComparison.Ordinal))
                {
                    return header.Value;
                }
            }

            return null;
        }
    }

    private sealed class RecordingProducer : IKafkaEventProducer
    {
        public List<ProducedMessage> Produced { get; } = [];

        public string? FailTopic { get; set; }

        public bool FailAllTopics { get; set; }

        public Task ProduceAsync(
            string topic,
            string key,
            byte[] value,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
        {
            if (FailAllTopics || string.Equals(topic, FailTopic, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"simulated broker failure for topic '{topic}'");
            }

            Produced.Add(new ProducedMessage(topic, key, value, headers));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
