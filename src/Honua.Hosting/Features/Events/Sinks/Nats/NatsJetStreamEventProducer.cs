// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Honua.Infrastructure.Events;

/// <summary>
/// NATS.Net-backed <see cref="INatsEventProducer"/> that owns all JetStream
/// connection and deduplication configuration (#357).
/// </summary>
/// <remarks>
/// This is the only type that references the NATS client. It is constructed once
/// as a singleton; the underlying <see cref="NatsConnection"/> is thread-safe and
/// shared across all publishes. Messages are published through JetStream so the
/// server persists and acknowledges them; supplying a <c>Nats-Msg-Id</c> header
/// lets JetStream reject duplicates within the stream's dedup window, giving
/// publish-side exactly-once delivery (no duplicate stored on retry).
/// </remarks>
internal sealed class NatsJetStreamEventProducer : INatsEventProducer
{
    private const string MessageIdHeader = "Nats-Msg-Id";

    private readonly NatsConnection _connection;
    private readonly NatsJSContext _jetStream;
    private readonly TimeSpan _publishTimeout;

    public NatsJetStreamEventProducer(IOptions<NatsFeatureChangeEventSinkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value ?? throw new ArgumentNullException(nameof(options));

        var natsOpts = new NatsOpts
        {
            Url = value.Url ?? "nats://localhost:4222",
            Name = "honua-feature-event-sink",
            // The default registry routes byte[] payloads through the raw
            // serializer, so the already-JSON-serialized event envelope is written
            // to the wire verbatim without an extra serialization layer.
            SerializerRegistry = NatsDefaultSerializerRegistry.Default,
            AuthOpts = BuildAuthOpts(value),
        };

        _connection = new NatsConnection(natsOpts);
        _jetStream = new NatsJSContext(_connection);
        _publishTimeout = TimeSpan.FromMilliseconds(value.PublishTimeoutMs);
    }

    private static NatsAuthOpts BuildAuthOpts(NatsFeatureChangeEventSinkOptions value)
    {
        var auth = NatsAuthOpts.Default;

        if (!string.IsNullOrWhiteSpace(value.CredsFile))
        {
            auth = auth with { CredsFile = value.CredsFile };
        }

        if (!string.IsNullOrWhiteSpace(value.Token))
        {
            auth = auth with { Token = value.Token };
        }

        if (!string.IsNullOrWhiteSpace(value.Username))
        {
            auth = auth with { Username = value.Username };
        }

        if (!string.IsNullOrWhiteSpace(value.Password))
        {
            auth = auth with { Password = value.Password };
        }

        return auth;
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        string subject,
        string messageId,
        byte[] value,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var natsHeaders = new NatsHeaders();
        for (var i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            natsHeaders.Add(header.Key, header.Value ?? string.Empty);
        }

        if (!string.IsNullOrEmpty(messageId))
        {
            natsHeaders[MessageIdHeader] = messageId;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_publishTimeout);

        var ack = await _jetStream
            .PublishAsync(subject, value, headers: natsHeaders, cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);

        if (ack.Error is not null)
        {
            throw new InvalidOperationException(
                $"JetStream rejected publish to subject '{subject}': {ack.Error.Description} (code {ack.Error.Code}).");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
