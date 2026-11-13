// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

// Live fan-out follows the durable append so clients do not miss the gap after replay.
internal sealed partial class FeatureStreamPublisher(
    IFeatureChangeEventStore store,
    FeatureStreamSessionManager sessionManager,
    ILogger<FeatureStreamPublisher> logger,
    FeatureChangeEventSinkBroadcaster? sinkBroadcaster = null) : IFeatureChangeEventPublisher
{
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly FeatureStreamSessionManager _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    private readonly ILogger<FeatureStreamPublisher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly FeatureChangeEventSinkBroadcaster? _sinkBroadcaster = sinkBroadcaster;

    public async Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        var durableRequest = EnsureEventId(request);

        FeatureChangeEvent persisted;
        try
        {
            persisted = await _store.AppendAsync(durableRequest, cancellationToken).ConfigureAwait(false);
        }
        // Best effort runs after the edit committed, so a failed append is logged with the
        // event's identity and dropped; durable delivery is the transactional outbox's job.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogPublishDropped(_logger, durableRequest.EventId!, durableRequest.LayerId, durableRequest.ObjectId, ex);
            return;
        }

        if (await _sessionManager.RefreshRoutabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            BroadcastToSessions(persisted);
        }

        DispatchToSinks(persisted);
    }

    public async Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        // A failed durable append surfaces so the dispatcher keeps the outbox row for retry.
        var durableRequest = EnsureEventId(request);
        var persisted = await _store.AppendAsync(durableRequest, cancellationToken).ConfigureAwait(false);
        if (await _sessionManager.RefreshRoutabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            BroadcastToSessions(persisted);
        }

        DispatchToSinks(persisted);
    }

    private static FeatureChangeEventRequest EnsureEventId(FeatureChangeEventRequest request)
        => string.IsNullOrWhiteSpace(request.EventId)
            ? request with { EventId = Guid.NewGuid().ToString("N") }
            : request;

    private void BroadcastToSessions(FeatureChangeEvent persisted)
    {
        // Fan out to live streaming sessions after durable append.
        // Enrichment data (geometry envelope + properties) is carried on the message
        // for subscription filter evaluation during broadcast — no I/O in the hot path.
        var envelope = ToEnvelope(persisted);
        // Broadcast() returns the local delivery count only; cross-node fan-out is handled separately.
        var delivered = _sessionManager.Broadcast(
            FeatureStreamMessage.Data(envelope, persisted.GeometryEnvelope, persisted.PropertiesJson));

        LogLocalEventBroadcast(_logger, delivered, persisted.Cursor);
    }

    // Broker dispatch is independent from live-stream routability. A transient metadata
    // failure must fail the live filters closed without dropping a durably persisted event
    // from Kafka/NATS (especially on the strict outbox path).
    private void DispatchToSinks(FeatureChangeEvent persisted)
        => _sinkBroadcaster?.Dispatch(persisted);

    internal static FeatureStreamEnvelope ToEnvelope(FeatureChangeEvent e)
    {
        // Defense-in-depth for the geodesy invariant: emit geometry and geometryCrs
        // only when both resolve. The producing service already enforces the pair,
        // so this guard catches stored events whose GeometryJson cannot be parsed
        // (returning null from ParseJsonElement) or any future caller that bypasses
        // the upstream guard.
        var geometry = ParseJsonElement(e.GeometryJson);
        var geometryCrs = (geometry.HasValue && e.GeometrySrid.HasValue)
            ? $"EPSG:{e.GeometrySrid.Value.ToString(CultureInfo.InvariantCulture)}"
            : null;

        return new FeatureStreamEnvelope
        {
            EventId = e.EventId,
            Cursor = e.Cursor,
            Timestamp = e.Timestamp,
            SourceId = e.SourceId,
            ServiceId = e.ServiceId,
            LayerId = e.LayerId,
            FeatureId = e.ObjectId.ToString(CultureInfo.InvariantCulture),
            ObjectId = e.ObjectId,
            Operation = e.Operation,
            Protocol = e.Protocol,
            RequestId = e.RequestId,
            OperationInstanceId = e.OperationInstanceId,
            CorrelationId = e.CorrelationId,
            AuditId = e.AuditId,
            ProposalId = e.ProposalId,
            Geometry = geometry,
            GeometryCrs = geometryCrs,
            Attributes = ParseAttributes(e.PropertiesJson),
            ChangedAttributes = e.ChangedAttributes,
            GeometryChanged = e.GeometryChanged
        };
    }

    internal static JsonElement? ParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Dictionary<string, JsonElement>? ParseAttributes(string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                propertiesJson,
                FeatureStreamJsonContext.Default.DictionaryStringJsonElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Warning,
        Message = "Feature-change event {EventId} for layer {LayerId} object {ObjectId} was dropped because the event store append failed.")]
    private static partial void LogPublishDropped(ILogger logger, string eventId, int layerId, long objectId, Exception exception);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Debug, Message = "Feature stream event delivered locally to {Count} sessions (cursor={Cursor})")]
    private static partial void LogLocalEventBroadcast(ILogger logger, int count, long cursor);
}
