// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Features.Infrastructure.Events;

internal sealed class FeatureMutationEventService(
    IFeatureChangeEventPublisher featureChangeEventPublisher,
    OutputCacheInvalidationService? outputCacheInvalidationService = null,
    ILogger<FeatureMutationEventService>? logger = null,
    IOutboxCapabilityProvider? outboxCapabilityProvider = null)
{
    private readonly IFeatureChangeEventPublisher _featureChangeEventPublisher = featureChangeEventPublisher
        ?? throw new ArgumentNullException(nameof(featureChangeEventPublisher));
    private readonly OutputCacheInvalidationService? _outputCacheInvalidationService = outputCacheInvalidationService;
    private readonly ILogger<FeatureMutationEventService> _logger = logger ?? NullLogger<FeatureMutationEventService>.Instance;
    private readonly IOutboxCapabilityProvider? _outboxCapabilityProvider = outboxCapabilityProvider;

    /// <summary>
    /// True when the active provider records feature-change events through the durable
    /// transactional outbox. Protocol handlers use this to skip the redundant post-commit
    /// publish and let the dispatcher own delivery.
    /// </summary>
    public bool OutboxEnabled => _outboxCapabilityProvider?.SupportsTransactionalOutbox == true;

    public Task InvalidateLayerAsync(string? serviceId, int layerId, CancellationToken cancellationToken)
        => _outputCacheInvalidationService?.InvalidateLayerAsync(serviceId, layerId, cancellationToken)
           ?? Task.CompletedTask;

    public static async Task<string> ResolveServiceIdAsync(
        HttpContext context,
        int layerId,
        string serviceProtocol,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var serviceId = await LayerValidationHelpers.ResolvePrimaryServiceNameByProtocolV2Async(
            context,
            layerId,
            serviceProtocol,
            cancellationToken).ConfigureAwait(false);

        return serviceId ?? layerId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Resolve a transactional outbox scope for the next feature-mutation call. Returns
    /// <c>null</c> when the active provider does not support a transactional outbox so the
    /// caller's <c>using</c> wrapper becomes a no-op. The caller activates the scope
    /// synchronously via <see cref="FeatureMutationOutboxScope.BeginIfNotNull"/> so the
    /// <see cref="AsyncLocal{T}"/> mutation lands in the caller's ExecutionContext (mutations
    /// inside this <c>async</c> method are not observed by the caller after the awaiter
    /// completes — the canonical .NET behavior for AsyncLocal in async callees).
    /// When <c>perOperationRequestIds</c> is provided, the entry factory dequeues a
    /// per-row request id from the kind-keyed queue ("create"/"update"/"delete") on each
    /// invocation so batch protocol handlers (OData $batch atomic groups, OGC Features
    /// transactions) preserve subrequest correlation; otherwise the resolved
    /// <c>requestId</c> (or the parent trace identifier) flows through.
    /// <c>geometryChanged</c> and <c>perOperationGeometryChanged</c> let the protocol
    /// layer signal whether the originating request actually intended to mutate geometry;
    /// otherwise <c>GeometryChanged</c> defaults to <c>false</c> (matching the inline
    /// publish path) so attribute-only PATCH/merge updates are not over-reported as
    /// geometry changes by inferring from the post-mutation snapshot, which still carries
    /// the prior geometry on partial updates.
    /// </summary>
    public async Task<FeatureMutationOutboxScopeData?> ResolveOutboxScopeAsync(
        HttpContext context,
        int layerId,
        string protocol,
        string? sourceId = null,
        string? serviceId = null,
        string? serviceProtocol = null,
        string? requestId = null,
        int? layerSrid = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? perOperationRequestIds = null,
        bool? geometryChanged = null,
        IReadOnlyDictionary<string, IReadOnlyList<bool>>? perOperationGeometryChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);

        if (!OutboxEnabled)
        {
            return null;
        }

        var resolvedServiceId = !string.IsNullOrWhiteSpace(serviceId)
            ? serviceId
            : !string.IsNullOrWhiteSpace(serviceProtocol)
                ? await ResolveServiceIdAsync(context, layerId, serviceProtocol, cancellationToken).ConfigureAwait(false)
                : layerId.ToString(CultureInfo.InvariantCulture);

        var resolvedRequestId = !string.IsNullOrWhiteSpace(requestId)
            ? requestId
            : context.TraceIdentifier;

        var resolvedSourceId = !string.IsNullOrWhiteSpace(sourceId) ? sourceId : protocol;
        var resolvedGeometryChanged = geometryChanged ?? false;

        Dictionary<string, Queue<string>>? perOperationRequestIdQueues = null;
        if (perOperationRequestIds is { Count: > 0 })
        {
            perOperationRequestIdQueues = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
            foreach (var (operationKind, ids) in perOperationRequestIds)
            {
                if (string.IsNullOrWhiteSpace(operationKind) || ids is null || ids.Count == 0)
                {
                    continue;
                }

                perOperationRequestIdQueues[operationKind] = new Queue<string>(ids);
            }
        }

        Dictionary<string, Queue<bool>>? perOperationGeometryChangedQueues = null;
        if (perOperationGeometryChanged is { Count: > 0 })
        {
            perOperationGeometryChangedQueues = new Dictionary<string, Queue<bool>>(StringComparer.Ordinal);
            foreach (var (operationKind, flags) in perOperationGeometryChanged)
            {
                if (string.IsNullOrWhiteSpace(operationKind) || flags is null || flags.Count == 0)
                {
                    continue;
                }

                perOperationGeometryChangedQueues[operationKind] = new Queue<bool>(flags);
            }
        }

        // Currently bound per-row metadata. Mutated by BeginRowAttempt at the start of
        // each attempted row mutation so a failed row consumes its queued metadata even
        // when the mutation never reaches EntryFactory; otherwise non-rollback batches
        // (RollbackOnFailure=false in WFS / GeoServices applyEdits) would shift the queued
        // GeometryChanged / request-id onto the next successful row of the same kind.
        var boundRequestId = resolvedRequestId;
        var boundGeometryChanged = resolvedGeometryChanged;

        return new FeatureMutationOutboxScopeData
        {
            BeginRowAttempt = operation =>
            {
                // Advance per-kind request-id queue (no-op when the caller did not seed one).
                boundRequestId = perOperationRequestIdQueues is not null
                    && perOperationRequestIdQueues.TryGetValue(operation, out var requestIdQueue)
                    && requestIdQueue.Count > 0
                        ? requestIdQueue.Dequeue()
                        : resolvedRequestId;

                // Advance per-kind geometryChanged queue. The protocol layer is the source
                // of truth for whether the originating request intended to mutate geometry;
                // inferring from the post-mutation snapshot would over-report PATCH/merge
                // updates that preserve the prior geometry untouched.
                boundGeometryChanged = perOperationGeometryChangedQueues is not null
                    && perOperationGeometryChangedQueues.TryGetValue(operation, out var geometryChangedQueue)
                    && geometryChangedQueue.Count > 0
                        ? geometryChangedQueue.Dequeue()
                        : resolvedGeometryChanged;
            },
            EntryFactory = (objectId, operation, snapshot) => BuildEntry(
                resolvedServiceId,
                layerId,
                objectId,
                operation,
                protocol,
                resolvedSourceId,
                boundRequestId,
                snapshot,
                layerSrid,
                boundGeometryChanged)
        };
    }

    public async Task PublishAsync(
        HttpContext context,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        CancellationToken cancellationToken,
        Feature? mutationFeature = null,
        string? serviceId = null,
        string? serviceProtocol = null,
        string? requestId = null,
        bool? geometryChanged = null,
        double[]? geometryEnvelope = null,
        string? propertiesJson = null,
        string? geometryJson = null,
        int? geometrySrid = null,
        int? layerSrid = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);

        // When the active provider supports a transactional outbox, the outbox row was
        // committed inside the mutation transaction and the dispatcher will publish it.
        // Skipping this fire-and-forget publish prevents a duplicate event from racing the
        // dispatcher and avoids charging the request hot path for an extra Redis/append round trip.
        if (OutboxEnabled)
        {
            return;
        }

        var resolvedServiceId = !string.IsNullOrWhiteSpace(serviceId)
            ? serviceId
            : !string.IsNullOrWhiteSpace(serviceProtocol)
                ? await ResolveServiceIdAsync(context, layerId, serviceProtocol, cancellationToken).ConfigureAwait(false)
                : layerId.ToString(CultureInfo.InvariantCulture);

        var resolvedRequestId = !string.IsNullOrWhiteSpace(requestId)
            ? requestId
            : context.TraceIdentifier;

        if ((geometryEnvelope is null || propertiesJson is null || geometryJson is null || geometrySrid is null) &&
            mutationFeature is not null)
        {
            // Pass layerSrid as the enrichment fallback so mutation paths whose default
            // WKBWriter does not embed SRID (gRPC ApplyEdits, WFS Transaction) still
            // emit geometry/geometryCrs to streaming subscribers.
            var enrichment = FeatureChangeEventEnrichment.FromFeatureSnapshot(mutationFeature, layerSrid);
            geometryEnvelope ??= enrichment.GeometryEnvelope;
            propertiesJson ??= enrichment.PropertiesJson;
            geometryJson ??= enrichment.GeometryJson;
            geometrySrid ??= enrichment.GeometrySrid;
        }
        else if (geometrySrid is null && layerSrid is > 0)
        {
            // Caller pre-supplied geometryJson but no SRID; honor the layer SRID
            // fallback so the paired-contract guard below does not strip a known
            // GeoJSON when the originating protocol knows the layer CRS.
            geometrySrid = layerSrid;
        }

        // Geodesy invariant guard: emit geometry and geometryCrs as a pair, or
        // omit both. Mirrors the enrichment-layer guard so streaming subscribers
        // and webhook consumers never observe ambiguous coordinates (geometry
        // without CRS) or orphaned CRS metadata (CRS without coordinates — for
        // example a delete event that supplied layerSrid as a fallback but had
        // no before-image to enrich into geometryJson).
        if (geometryJson is null || geometrySrid is null)
        {
            geometryJson = null;
            geometrySrid = null;
        }

        var requestPayload = new FeatureChangeEventRequest
        {
            EventId = Guid.NewGuid().ToString("N"),
            SourceId = protocol,
            ServiceId = resolvedServiceId,
            LayerId = layerId,
            ObjectId = objectId,
            Operation = operation,
            Protocol = protocol,
            RequestId = resolvedRequestId,
            GeometryChanged = geometryChanged ?? false,
            GeometryEnvelope = geometryEnvelope,
            PropertiesJson = propertiesJson,
            GeometryJson = geometryJson,
            GeometrySrid = geometrySrid
        };

        try
        {
            await _featureChangeEventPublisher.PublishAsync(
                requestPayload,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FeatureMutationEventLog.PublishAfterCommitFailed(
                _logger,
                requestPayload.EventId,
                requestPayload.ServiceId,
                requestPayload.LayerId,
                requestPayload.ObjectId,
                requestPayload.Operation,
                requestPayload.Protocol,
                ex);
        }
    }

    private static FeatureChangeOutboxEntry BuildEntry(
        string serviceId,
        int layerId,
        long objectId,
        string operation,
        string protocol,
        string sourceId,
        string requestId,
        Feature? snapshot,
        int? layerSrid,
        bool geometryChanged)
    {
        var enrichment = FeatureChangeEventEnrichment.FromFeatureSnapshot(snapshot, layerSrid);
        var geometryJson = enrichment.GeometryJson;
        var geometrySrid = enrichment.GeometrySrid;
        if (geometryJson is null || geometrySrid is null)
        {
            // Geodesy invariant — strip both halves of the pair when either is missing.
            geometryJson = null;
            geometrySrid = null;
        }

        var eventId = Guid.NewGuid().ToString("N");
        // Capture mutation time once so the serialized payload and the outbox row's
        // CreatedAt agree, and so a delayed/retried dispatcher append does not re-stamp
        // the event with the dispatch time. Replay from/to filtering uses
        // FeatureChangeEvent.Timestamp, so the persisted timestamp must reflect when the
        // mutation happened — not when the dispatcher got around to publishing it.
        var mutationTimestamp = DateTimeOffset.UtcNow;
        var request = new FeatureChangeEventRequest
        {
            EventId = eventId,
            SourceId = sourceId,
            ServiceId = serviceId,
            LayerId = layerId,
            ObjectId = objectId,
            Operation = operation,
            Protocol = protocol,
            RequestId = requestId,
            Timestamp = mutationTimestamp,
            GeometryChanged = geometryChanged,
            GeometryEnvelope = enrichment.GeometryEnvelope,
            PropertiesJson = enrichment.PropertiesJson,
            GeometryJson = geometryJson,
            GeometrySrid = geometrySrid,
        };

        var payload = JsonSerializer.Serialize(
            request,
            FeatureChangeEventsJsonContext.Default.FeatureChangeEventRequest);

        return new FeatureChangeOutboxEntry
        {
            OutboxId = Guid.NewGuid(),
            ServiceId = serviceId,
            LayerId = layerId,
            ObjectId = objectId,
            Operation = operation,
            Protocol = protocol,
            SourceId = sourceId,
            RequestId = requestId,
            EventId = eventId,
            EventPayload = payload,
            Status = OutboxStatuses.Pending,
            RetryCount = 0,
            CreatedAt = mutationTimestamp,
        };
    }

}
