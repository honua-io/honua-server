// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for the NATS JetStream feature-change event sink (#357), verifying
/// the serialized payload/dedup id/headers, the dead-letter routing on delivery
/// failure, and the surfaced-failure behaviour when dead-lettering is unavailable.
/// The sink is exercised against a fake <see cref="INatsEventProducer"/> so no
/// live JetStream server is required.
/// </summary>
public sealed class NatsFeatureChangeEventSinkTests
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

    private static NatsFeatureChangeEventSink Create(
        INatsEventProducer producer,
        NatsFeatureChangeEventSinkOptions options)
        => new(
            producer,
            Options.Create(options),
            NullLogger<NatsFeatureChangeEventSink>.Instance);

    [UnitTest]
    public async Task PublishAsync_OnSuccess_PublishesToSubjectWithDedupIdAndHeaders()
    {
        var producer = new RecordingProducer();
        var sink = Create(producer, new NatsFeatureChangeEventSinkOptions
        {
            Subject = "feature-changes",
            DeadLetterSubject = "feature-changes.dlq",
        });

        await sink.PublishAsync(SampleEvent());

        var published = Assert.Single(producer.Published);
        Assert.Equal("feature-changes", published.Subject);

        // Dedup id is the event id so JetStream rejects duplicate publishes.
        Assert.Equal("evt-1", published.MessageId);

        // Payload round-trips to the original event.
        var roundTripped = JsonSerializer.Deserialize(
            published.Value,
            FeatureChangeEventsJsonContext.Default.FeatureChangeEvent);
        Assert.NotNull(roundTripped);
        Assert.Equal("evt-1", roundTripped!.EventId);

        Assert.Equal("evt-1", published.HeaderValue("x-event-id"));
        Assert.Equal("update", published.HeaderValue("x-operation"));
        Assert.Equal("99", published.HeaderValue("x-feature-id"));
        Assert.Equal("application/json", published.HeaderValue("content-type"));
    }

    [UnitTest]
    public async Task PublishAsync_WhenDeduplicationDisabled_OmitsMessageId()
    {
        var producer = new RecordingProducer();
        var sink = Create(producer, new NatsFeatureChangeEventSinkOptions
        {
            Subject = "feature-changes",
            EnableDeduplication = false,
        });

        await sink.PublishAsync(SampleEvent());

        var published = Assert.Single(producer.Published);
        Assert.Equal(string.Empty, published.MessageId);
    }

    [UnitTest]
    public async Task PublishAsync_WhenPrimaryFails_RoutesToDeadLetterSubject()
    {
        var producer = new RecordingProducer { FailSubject = "feature-changes" };
        var sink = Create(producer, new NatsFeatureChangeEventSinkOptions
        {
            Subject = "feature-changes",
            DeadLetterSubject = "feature-changes.dlq",
        });

        // Must not throw: a routed-to-DLQ event is handled, not a sink failure.
        await sink.PublishAsync(SampleEvent());

        var dlq = Assert.Single(producer.Published);
        Assert.Equal("feature-changes.dlq", dlq.Subject);
        Assert.Equal("evt-1:dlq", dlq.MessageId);
        Assert.Equal("feature-changes", dlq.HeaderValue("x-dlq-source-subject"));
        Assert.False(string.IsNullOrEmpty(dlq.HeaderValue("x-dlq-error")));
        Assert.False(string.IsNullOrEmpty(dlq.HeaderValue("x-dlq-failed-at")));
    }

    [UnitTest]
    public async Task PublishAsync_WhenPrimaryFails_AndDlqDisabled_SurfacesFailure()
    {
        var producer = new RecordingProducer { FailSubject = "feature-changes" };
        var sink = Create(producer, new NatsFeatureChangeEventSinkOptions
        {
            Subject = "feature-changes",
            DeadLetterSubject = null,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PublishAsync(SampleEvent()));
    }

    [UnitTest]
    public async Task PublishAsync_WhenPrimaryAndDlqFail_SurfacesOriginalFailure()
    {
        var producer = new RecordingProducer { FailAllSubjects = true };
        var sink = Create(producer, new NatsFeatureChangeEventSinkOptions
        {
            Subject = "feature-changes",
            DeadLetterSubject = "feature-changes.dlq",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PublishAsync(SampleEvent()));
    }

    [UnitTest]
    public void Validate_WhenEnabledWithoutUrl_Fails()
    {
        var validator = new NatsFeatureChangeEventSinkOptionsValidator();

        var result = validator.Validate(null, new NatsFeatureChangeEventSinkOptions
        {
            Enabled = true,
            Url = null,
            Subject = "feature-changes",
        });

        Assert.True(result.Failed);
    }

    [UnitTest]
    public void Validate_WhenDeadLetterSubjectEqualsSubject_Fails()
    {
        var validator = new NatsFeatureChangeEventSinkOptionsValidator();

        var result = validator.Validate(null, new NatsFeatureChangeEventSinkOptions
        {
            Enabled = true,
            Url = "nats://localhost:4222",
            Subject = "feature-changes",
            DeadLetterSubject = "feature-changes",
        });

        Assert.True(result.Failed);
    }

    [UnitTest]
    public void Validate_WhenDisabled_Succeeds()
    {
        var validator = new NatsFeatureChangeEventSinkOptionsValidator();

        var result = validator.Validate(null, new NatsFeatureChangeEventSinkOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    private sealed record PublishedMessage(
        string Subject,
        string MessageId,
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

    private sealed class RecordingProducer : INatsEventProducer
    {
        public List<PublishedMessage> Published { get; } = [];

        public string? FailSubject { get; set; }

        public bool FailAllSubjects { get; set; }

        public Task PublishAsync(
            string subject,
            string messageId,
            byte[] value,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken = default)
        {
            if (FailAllSubjects || string.Equals(subject, FailSubject, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"simulated JetStream failure for subject '{subject}'");
            }

            Published.Add(new PublishedMessage(subject, messageId, value, headers));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
