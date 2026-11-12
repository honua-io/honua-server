// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Realtime;

namespace Honua.Server.Features.Streaming;

// Streams read changes from the change log, which the edit transaction wrote, on every task. After
// a commit this wakes the local tail, so this task's subscribers do not wait for a notification,
// and hands the change to the Kafka and NATS sinks.
internal sealed partial class FeatureStreamPublisher(
    IRealtimeWakeSignal wake,
    PostgresFeatureChangeLog log,
    ILogger<FeatureStreamPublisher> logger,
    FeatureChangeEventSinkBroadcaster? sinkBroadcaster = null) : IFeatureChangeEventPublisher
{
    public async Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        wake.Wake();
        try
        {
            await DispatchToSinksAsync(request, cancellationToken).ConfigureAwait(false);
        }
        // Best effort runs after the edit committed, so a failed read is logged with the event's
        // identity and the sinks skip it; durable delivery is the transactional outbox's job.
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            LogPublishDropped(logger, request.EventId ?? string.Empty, request.LayerId, request.ObjectId, ex);
        }
    }

    // A failed read surfaces so the outbox dispatcher keeps the row for retry.
    public async Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        wake.Wake();
        await DispatchToSinksAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // Brokers receive the logged change, with its generation as the cursor. A change published
    // without an outbox row is not in the log under its request's event id, so the request itself
    // goes out, with cursor 0.
    private async Task DispatchToSinksAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken)
    {
        if (sinkBroadcaster is not { HasActiveSinks: true })
        {
            return;
        }

        var logged = string.IsNullOrWhiteSpace(request.EventId)
            ? null
            : await log.ReadByEventIdAsync(request.EventId, cancellationToken).ConfigureAwait(false);
        sinkBroadcaster.Dispatch(logged ?? FromRequest(request), CancellationToken.None);
    }

    private static FeatureChangeEvent FromRequest(FeatureChangeEventRequest request)
        => new()
        {
            EventId = string.IsNullOrWhiteSpace(request.EventId) ? Guid.NewGuid().ToString("N") : request.EventId,
            Cursor = 0,
            Timestamp = request.Timestamp ?? DateTimeOffset.UtcNow,
            SourceId = request.SourceId,
            ServiceId = request.ServiceId,
            LayerId = request.LayerId,
            ObjectId = request.ObjectId,
            Operation = request.Operation,
            Protocol = request.Protocol,
            RequestId = request.RequestId,
            OperationInstanceId = request.OperationInstanceId,
            CorrelationId = request.CorrelationId,
            AuditId = request.AuditId,
            ProposalId = request.ProposalId,
            ChangedAttributes = request.ChangedAttributes,
            GeometryChanged = request.GeometryChanged,
            GeometryEnvelope = request.GeometryEnvelope,
            PropertiesJson = request.PropertiesJson,
            GeometryJson = request.GeometryJson,
            GeometrySrid = request.GeometrySrid
        };

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
        Message = "Feature-change event {EventId} for layer {LayerId} object {ObjectId} was not sent to the event sinks because the change log read failed.")]
    private static partial void LogPublishDropped(ILogger logger, string eventId, int layerId, long objectId, Exception exception);
}
