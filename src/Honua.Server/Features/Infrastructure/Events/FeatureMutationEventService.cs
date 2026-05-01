// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Features.Infrastructure.Events;

internal sealed class FeatureMutationEventService(
    IFeatureChangeEventPublisher featureChangeEventPublisher,
    OutputCacheInvalidationService? outputCacheInvalidationService = null,
    ILogger<FeatureMutationEventService>? logger = null)
{
    private readonly IFeatureChangeEventPublisher _featureChangeEventPublisher = featureChangeEventPublisher
        ?? throw new ArgumentNullException(nameof(featureChangeEventPublisher));
    private readonly OutputCacheInvalidationService? _outputCacheInvalidationService = outputCacheInvalidationService;
    private readonly ILogger<FeatureMutationEventService> _logger = logger ?? NullLogger<FeatureMutationEventService>.Instance;

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

        // Geodesy invariant guard: drop the geometry JSON if a caller supplied
        // coordinates without an accompanying SRID. Mirrors the enrichment-layer
        // guard so streaming subscribers and webhook consumers never observe
        // ambiguous coordinates downstream.
        if (geometryJson is not null && geometrySrid is null)
        {
            geometryJson = null;
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
}
