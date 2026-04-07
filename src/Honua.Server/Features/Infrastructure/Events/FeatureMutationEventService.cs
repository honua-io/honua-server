// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.Infrastructure.Events;

internal sealed class FeatureMutationEventService(
    IFeatureChangeEventPublisher featureChangeEventPublisher,
    OutputCacheInvalidationService? outputCacheInvalidationService = null)
{
    private readonly IFeatureChangeEventPublisher _featureChangeEventPublisher = featureChangeEventPublisher
        ?? throw new ArgumentNullException(nameof(featureChangeEventPublisher));
    private readonly OutputCacheInvalidationService? _outputCacheInvalidationService = outputCacheInvalidationService;

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
        string? propertiesJson = null)
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

        if (geometryEnvelope is null && propertiesJson is null)
        {
            (geometryEnvelope, propertiesJson) = FeatureChangeEventEnrichment.FromFeature(mutationFeature);
        }

        await _featureChangeEventPublisher.PublishAsync(
            new FeatureChangeEventRequest
            {
                EventId = Guid.NewGuid().ToString("N"),
                ServiceId = resolvedServiceId,
                LayerId = layerId,
                ObjectId = objectId,
                Operation = operation,
                Protocol = protocol,
                RequestId = resolvedRequestId,
                GeometryChanged = geometryChanged ?? false,
                GeometryEnvelope = geometryEnvelope,
                PropertiesJson = propertiesJson
            },
            cancellationToken).ConfigureAwait(false);
    }
}
