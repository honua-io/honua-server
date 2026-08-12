// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;

namespace Honua.Core.Features.Validation;

/// <summary>
/// Unified resource validation service for consistent service/layer/collection existence checks
/// across all protocols (GeoServices REST, OGC API Features, OData).
/// </summary>
public sealed class ResourceValidator : IResourceValidator
{
    private readonly IMetadataV2GraphProvider _v2Provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceValidator"/> class.
    /// </summary>
    /// <param name="v2Provider">Metadata v2 graph provider for canonical resource lookup.</param>
    public ResourceValidator(IMetadataV2GraphProvider v2Provider)
    {
        _v2Provider = v2Provider ?? throw new ArgumentNullException(nameof(v2Provider));
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<MetadataV2Resource>> ValidateLayerV2Async(
        int layerId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);

        // A single storage layer index can be published more than once (for
        // example a feature dataset and a raster sidecar that share the same
        // integer layer id, as the client-compat seed does for layer 2000).
        // Prefer the publication whose backing resource resolves to a usable
        // storage-layer handle so the integer-collection-id contract returns a
        // storage-backed layer rather than an arbitrary first match (which may
        // be a raster resource with no integer storage handle, leaving callers
        // such as OGC API Maps unable to resolve the layer). Fall back to any
        // matching publication's resource so existing single-publication and
        // non-storage-backed cases keep their previous behaviour.
        MetadataV2Resource? firstResource = null;
        foreach (var candidate in snapshot.Graph.Publications)
        {
            if (candidate.LayerIndex != layerId)
            {
                continue;
            }

            // Admin-disable flips the publication/resource lifecycle to
            // MetadataV2LifecycleStatus.Retired — skip them so disabled layers 404 on
            // every protocol surface that resolves layers by integer id (OGC
            // collections, Maps, OData), mirroring ValidateServiceLayerV2Async.
            var candidateResource = snapshot.ResolveResource(candidate);
            if (!snapshot.IsRoutable(candidate))
            {
                continue;
            }

            firstResource ??= candidateResource!;

            if (snapshot.ResolveStorageLayerId(candidate).HasValue)
            {
                return ResourceValidationResult.Success(candidateResource!);
            }
        }

        if (firstResource is null)
        {
            return ResourceValidationResult.NotFound<MetadataV2Resource>(
                ErrorMessages.NotFound.FormatLayer(layerId));
        }

        return ResourceValidationResult.Success(firstResource);
    }

    /// <inheritdoc />
    public async Task<ResourceValidationResult<MetadataV2Resource>> ValidateCollectionV2Async(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return ResourceValidationResult.InvalidIdentifier<MetadataV2Resource>(
                ErrorMessages.Validation.CollectionIdRequired);
        }

        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);

        // Match by resource name (case-insensitive) or resource id. Resources retired by
        // admin-disable are treated as missing so disabled layers 404 on the collection
        // routes too (the integer fallback below applies the same lifecycle filtering).
        foreach (var resource in snapshot.Graph.Resources)
        {
            if (resource.Status.Lifecycle == MetadataV2LifecycleStatus.Retired)
            {
                continue;
            }

            if (string.Equals(resource.Metadata.Name, collectionId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resource.Metadata.Id, collectionId, StringComparison.Ordinal))
            {
                return ResourceValidationResult.Success(resource);
            }
        }

        // Fall back to integer layer index, mirroring v1.
        if (int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerIndex))
        {
            var byIndex = await ValidateLayerV2Async(layerIndex, cancellationToken).ConfigureAwait(false);
            if (byIndex.IsValid)
            {
                return byIndex;
            }
        }

        return ResourceValidationResult.NotFound<MetadataV2Resource>(
            ErrorMessages.NotFound.FormatCollection(collectionId));
    }

    /// <inheritdoc />
    public Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
        string serviceId,
        CancellationToken cancellationToken = default)
        => ValidateServiceCoreAsync(serviceId, requiredProtocol: null, cancellationToken);

    /// <inheritdoc />
    public Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceV2Async(
        string serviceId,
        string requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredProtocol);
        return ValidateServiceCoreAsync(serviceId, requiredProtocol, cancellationToken);
    }

    private async Task<ResourceValidationResult<MetadataV2Service>> ValidateServiceCoreAsync(
        string serviceId,
        string? requiredProtocol,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return ResourceValidationResult.InvalidIdentifier<MetadataV2Service>(
                ErrorMessages.Validation.ServiceIdRequired);
        }

        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (requiredProtocol is not null)
        {
            var protocolService = ResolveServiceForProtocol(snapshot, serviceId, requiredProtocol);
            return protocolService is not null
                ? ResourceValidationResult.Success(protocolService)
                : ResourceValidationResult.NotFound<MetadataV2Service>(
                    ErrorMessages.NotFound.FormatService(serviceId));
        }

        if (snapshot.Index.ServicesByName.TryGetValue(serviceId, out var byName) && byName.IsRoutable())
        {
            return ResourceValidationResult.Success(byName);
        }
        if (snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId) && byId.IsRoutable())
        {
            return ResourceValidationResult.Success(byId);
        }
        return ResourceValidationResult.NotFound<MetadataV2Service>(
            ErrorMessages.NotFound.FormatService(serviceId));
    }

    private static MetadataV2Service? ResolveServiceForProtocol(
        MetadataV2GraphSnapshot snapshot,
        string serviceId,
        string requiredProtocol)
    {
        var matchingServices = snapshot.Graph.Services
            .Where(service =>
                service.IsRoutable() &&
                (string.Equals(service.Metadata.Name, serviceId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(service.Metadata.Id, serviceId, StringComparison.Ordinal)))
            .ToArray();

        var exactId = matchingServices.FirstOrDefault(service =>
            string.Equals(service.Metadata.Id, serviceId, StringComparison.Ordinal));
        if (exactId is not null)
        {
            return ServiceProtocols.IsProtocolEnabled(exactId, requiredProtocol) ? exactId : null;
        }

        var candidates = matchingServices
            .Where(service => ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
            .ToArray();

        if (string.Equals(requiredProtocol, ServiceProtocols.GPServer, StringComparison.OrdinalIgnoreCase))
        {
            var geoprocessingServices = candidates
                .Where(IsDedicatedGpService)
                .ToArray();
            if (geoprocessingServices.Length == 1)
            {
                return geoprocessingServices[0];
            }

            if (geoprocessingServices.Length > 1)
            {
                candidates = geoprocessingServices;
            }
        }

        var preferred = candidates
            .Where(service => snapshot.Index.PublicationsByService[service.Metadata.Id]
                .Any(publication =>
                    snapshot.IsRoutable(publication) &&
                    ServiceProtocols.IsPreferredPublicationType(requiredProtocol, publication.PublicationType)))
            .ToArray();
        if (preferred.Length == 1)
        {
            return preferred[0];
        }

        var compatible = candidates
            .Where(service => snapshot.Index.PublicationsByService[service.Metadata.Id]
                .Any(publication =>
                    snapshot.IsRoutable(publication) &&
                    IsPublicationTypeCompatible(requiredProtocol, publication.PublicationType)))
            .ToArray();
        if (compatible.Length == 1)
        {
            return compatible[0];
        }

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsDedicatedGpService(MetadataV2Service service)
        => service.Route?.TrimEnd('/').EndsWith("/GPServer", StringComparison.OrdinalIgnoreCase) == true;

    /// <inheritdoc />
    public Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
        string serviceId,
        int layerId,
        CancellationToken cancellationToken = default)
        => ValidateServiceLayerCoreAsync(serviceId, layerId, requiredProtocol: null, cancellationToken);

    /// <inheritdoc />
    public Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerV2Async(
        string serviceId,
        int layerId,
        string requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredProtocol);
        return ValidateServiceLayerCoreAsync(serviceId, layerId, requiredProtocol, cancellationToken);
    }

    private async Task<ResourceValidationResult<MetadataV2ServiceLayerTriple>> ValidateServiceLayerCoreAsync(
        string serviceId,
        int layerId,
        string? requiredProtocol,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return ResourceValidationResult.InvalidIdentifier<MetadataV2ServiceLayerTriple>(
                ErrorMessages.Validation.ServiceIdRequired);
        }

        var snapshot = await RequireV2SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var service = ResolveServiceForLayer(snapshot, serviceId, layerId, requiredProtocol);
        if (service is null)
        {
            return ResourceValidationResult.NotFound<MetadataV2ServiceLayerTriple>(
                ErrorMessages.NotFound.FormatLayerInService(layerId, serviceId));
        }

        foreach (var candidate in snapshot.Index.PublicationsByService[service.Metadata.Id]
                     .Where(pub => pub.LayerIndex == layerId &&
                                   (requiredProtocol is null ||
                                    IsPublicationTypeCompatible(requiredProtocol, pub.PublicationType)))
                     .OrderByDescending(pub => requiredProtocol is not null &&
                         ServiceProtocols.IsPreferredPublicationType(requiredProtocol, pub.PublicationType))
                     .Select(pub => (Publication: pub, Resource: snapshot.ResolveResource(pub)))
                     .Where(candidate => snapshot.IsRoutable(candidate.Publication)))
        {
            // Disabled (admin-disabled) publications/resources are flipped to
            // MetadataV2LifecycleStatus.Retired — skip them so the protocol routes
            // 404 the layer instead of serving stale metadata for a disabled layer.
            return ResourceValidationResult.Success(
                new MetadataV2ServiceLayerTriple(service, candidate.Publication, candidate.Resource!));
        }
        return ResourceValidationResult.NotFound<MetadataV2ServiceLayerTriple>(
            ErrorMessages.NotFound.FormatLayerInService(layerId, serviceId));
    }

    private static MetadataV2Service? ResolveServiceForLayer(
        MetadataV2GraphSnapshot snapshot,
        string serviceId,
        int layerId,
        string? requiredProtocol)
    {
        if (requiredProtocol is null)
        {
            if (snapshot.Index.ServicesByName.TryGetValue(serviceId, out var byName))
            {
                return byName;
            }

            return snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId) ? byId : null;
        }

        // Several protocol-specific services may intentionally share one public route name.
        // Resolve against the publication surface requested by the route instead of the
        // first-wins ServicesByName index, otherwise a preceding aggregate/OGC service can
        // make a FeatureServer request serve the wrong resource.
        var matchingServices = snapshot.Graph.Services
            .Where(service =>
                string.Equals(service.Metadata.Name, serviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(service.Metadata.Id, serviceId, StringComparison.Ordinal))
            .ToArray();

        // An exact graph identity has the same precedence as the service-root route. Select it
        // before protocol and layer filtering so a disabled protocol or absent layer remains a 404
        // on that service instead of falling through to a different service whose display name
        // collides with the id.
        var exactId = matchingServices.FirstOrDefault(service =>
            string.Equals(service.Metadata.Id, serviceId, StringComparison.Ordinal));
        if (exactId is not null)
        {
            return ServiceProtocols.IsProtocolEnabled(exactId, requiredProtocol) ? exactId : null;
        }

        var protocolServices = matchingServices
            .Where(service => ServiceProtocols.IsProtocolEnabled(service, requiredProtocol))
            .ToArray();

        var candidates = protocolServices
            .Where(service => snapshot.Index.PublicationsByService[service.Metadata.Id]
                .Any(publication =>
                    publication.LayerIndex == layerId &&
                    snapshot.IsRoutable(publication) &&
                    IsPublicationTypeCompatible(requiredProtocol, publication.PublicationType)))
            .ToArray();

        // Prefer a service with the protocol's canonical publication type when one exists.
        // MapServer also serves feature publications created by the standard admin publish path,
        // but that compatibility fallback must not eclipse a dedicated map publication sharing the
        // same public service name.
        var preferred = candidates
            .Where(service => snapshot.Index.PublicationsByService[service.Metadata.Id]
                .Any(publication =>
                    publication.LayerIndex == layerId &&
                    snapshot.IsRoutable(publication) &&
                    ServiceProtocols.IsPreferredPublicationType(requiredProtocol, publication.PublicationType)))
            .ToArray();
        var scopedCandidates = preferred.Length > 0 ? preferred : candidates;
        return scopedCandidates.Length == 1 ? scopedCandidates[0] : null;
    }

    private static bool IsPublicationTypeCompatible(
        string protocol,
        MetadataV2PublicationType publicationType)
        => ServiceProtocols.IsPreferredPublicationType(protocol, publicationType) ||
           (string.Equals(protocol, ServiceProtocols.MapServer, StringComparison.OrdinalIgnoreCase) &&
            publicationType == MetadataV2PublicationType.EsriFeatureLayer);

    private async Task<MetadataV2GraphSnapshot> RequireV2SnapshotAsync(CancellationToken cancellationToken)
    {
        return await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }
}
