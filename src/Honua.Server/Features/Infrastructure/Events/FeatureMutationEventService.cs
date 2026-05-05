// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Events.Outbox;
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

        var serviceId = await LayerValidationHelpers.ResolvePrimaryServiceNameAsync(
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

        Dictionary<string, Queue<string>>? perOperationQueues = null;
        if (perOperationRequestIds is { Count: > 0 })
        {
            perOperationQueues = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
            foreach (var (operationKind, ids) in perOperationRequestIds)
            {
                if (string.IsNullOrWhiteSpace(operationKind) || ids is null || ids.Count == 0)
                {
                    continue;
                }

                perOperationQueues[operationKind] = new Queue<string>(ids);
            }
        }

        return new FeatureMutationOutboxScopeData
        {
            EntryFactory = (objectId, operation, snapshot) =>
            {
                // Per-row request id when the caller seeded the queue (atomic batch paths);
                // otherwise the resolved scope-wide id flows through to BuildEntry.
                var rowRequestId = perOperationQueues is not null
                    && perOperationQueues.TryGetValue(operation, out var queue)
                    && queue.Count > 0
                        ? queue.Dequeue()
                        : resolvedRequestId;

                return BuildEntry(
                    resolvedServiceId,
                    layerId,
                    objectId,
                    operation,
                    protocol,
                    resolvedSourceId,
                    rowRequestId,
                    snapshot,
                    layerSrid);
            }
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
        int? layerSrid)
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
        // Mirror the heuristic used by the protocol-layer publish path
        // (HonuaFeatureService and OgcFeaturesTransactionHandler): a snapshot with
        // non-empty WKB means geometry was created, replaced, or removed depending on
        // the operation kind. Without this flag, dispatcher-published events always
        // carry GeometryChanged=false, so streaming/webhook subscribers cannot tell
        // attribute-only updates apart from geometry-touching ones.
        var geometryChanged = snapshot?.Geometry is { Length: > 0 };
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
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

}
