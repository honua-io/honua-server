// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for managing per-service protocol toggles and MapServer settings.
/// </summary>
internal static class ServiceSettingsEndpoints
{
    private const int DefaultMapServerMaxImageWidth = 4096;
    private const int DefaultMapServerMaxImageHeight = 4096;
    private const int DefaultMapServerImageWidth = 400;
    private const int DefaultMapServerImageHeight = 400;
    private const int DefaultMapServerDpi = 96;
    private const string DefaultMapServerFormat = "png";
    private const int DefaultMapServerMaxFeaturesPerLayer = 10_000;

    /// <summary>
    /// Log category for service settings endpoints.
    /// </summary>
    internal sealed class ServiceSettingsEndpointsLog;

    public static void MapServiceSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/services")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Services")
            .RequireAdminAuthorization();

        group.MapGet("/", HandleListServices)
            .WithDisplayName("List Services")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{serviceName}/settings", HandleGetSettings)
            .WithDisplayName("Get Service Settings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{serviceName}/protocols", HandleUpdateProtocols)
            .WithDisplayName("Update Service Protocols")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/mapserver", HandleUpdateMapServerSettings)
            .WithDisplayName("Update MapServer Settings")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/access-policy", HandleUpdateAccessPolicy)
            .WithDisplayName("Update Access Policy")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/timeinfo", HandleUpdateTimeInfo)
            .WithDisplayName("Update Time Info")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapPut("/{serviceName}/layers/{layerId:int}/metadata", HandleUpdateLayerMetadata)
            .WithDisplayName("Update Layer Metadata")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSummary[]>>, ProblemHttpResult>>
        HandleListServices(
            [FromServices] IMetadataV2GraphProvider graphProvider,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            // V2 cutover: list services from the canonical graph. Multiple V2 service entries can share a display
            // name when the same logical service is exposed under different protocols
            // (FeatureServer / MapServer / Stac all named "test" for the same dataset);
            // the v1 admin response collapsed those into one row keyed by name, so we
            // preserve that contract here by grouping on Metadata.Name and unioning
            // protocols + layer counts.
            var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            var summaries = snapshot.Graph.Services
                .GroupBy(s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var representative = group.First();
                    var enabled = group
                        .SelectMany(s => s.Protocols)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var layerCount = group
                        .SelectMany(s => snapshot.Index.PublicationsByService[s.Metadata.Id])
                        .Select(p => p.ResourceId)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    return new ServiceSummary
                    {
                        ServiceName = representative.Metadata.Name,
                        Description = representative.Metadata.Description ?? string.Empty,
                        LayerCount = layerCount,
                        EnabledProtocols = enabled.Length == 0 ? MetadataV2ServiceProtocols.All : enabled,
                    };
                })
                .ToArray();

            return TypedResults.Ok(ApiResponse<ServiceSummary[]>.CreateSuccess(summaries));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.ListServicesFailed(logger, ex);
            return TypedResults.Problem(
                title: "Service listing failed",
                detail: "An internal error occurred while listing services.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetSettings(
            string serviceName,
            [FromServices] IMetadataV2GraphProvider graphProvider,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            if (!TryResolveServicesByName(snapshot, serviceName, out var services))
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            var response = BuildSettingsResponse(serviceName, services);
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.GetServiceSettingsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Service settings retrieval failed",
                detail: "An internal error occurred while retrieving service settings.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateProtocols(
            string serviceName,
            UpdateProtocolsRequest request,
            [FromServices] IMetadataV2GraphProvider graphProvider,
            [FromServices] IMetadataV2GraphStore graphStore,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            if (request.EnabledProtocols is null)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("Enabled protocols payload is required."));
            }

            var normalizedProtocols = request.EnabledProtocols
                .Where(static protocol => !string.IsNullOrWhiteSpace(protocol))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (normalizedProtocols.Length == 0)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure("At least one protocol must be enabled."));
            }

            // Validate protocol names
            var invalid = normalizedProtocols.Except(MetadataV2ServiceProtocols.All, StringComparer.Ordinal).ToArray();
            if (invalid.Length > 0)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"Invalid protocol(s): {string.Join(", ", invalid)}. Valid values: {string.Join(", ", MetadataV2ServiceProtocols.All)}"));
            }

            var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            if (!TryResolveServicesByName(snapshot, serviceName, out _))
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            await MutateServicesByNameAsync(
                graphStore,
                serviceName,
                svc => svc with { Protocols = normalizedProtocols },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, graphProvider, serviceName, logger).ConfigureAwait(false);

            // Re-read to return updated state
            var refreshed = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            TryResolveServicesByName(refreshed, serviceName, out var updatedServices);
            var response = BuildSettingsResponse(serviceName, updatedServices ?? Array.Empty<MetadataV2Service>());
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.UpdateProtocolsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Protocol update failed",
                detail: "An internal error occurred while updating service protocols.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateMapServerSettings(
            string serviceName,
            UpdateMapServerSettingsRequest request,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        // GAP (#1035 cutover 72/N): the legacy MapServer settings block (max image
        // width/height, default DPI, transparent flag, max features per layer) does not
        // have a typed V2 home for the full shape yet. Until a V2 MapServer extension
        // lands the endpoint refuses the operation honestly rather than silently
        // dropping the payload.
        _ = request;
        ServiceSettingsLog.UpdateMapServerSettingsFailed(
            logger,
            serviceName,
            new NotSupportedException(
                "MapServer settings have no Metadata v2 representation yet (#1035 cutover gap)."));
        return Task.FromResult<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>(
            TypedResults.Problem(
                title: "MapServer settings update not supported",
                detail:
                    "MapServer settings are not yet representable in the Metadata v2 graph. " +
                    "Track this gap on the metadata cutover epic before resuming the admin path.",
                statusCode: StatusCodes.Status501NotImplemented));
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateAccessPolicy(
            string serviceName,
            UpdateAccessPolicyRequest request,
            [FromServices] IMetadataV2GraphProvider graphProvider,
            [FromServices] IMetadataV2GraphStore graphStore,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            if (!TryResolveServicesByName(snapshot, serviceName, out _))
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            await MutateServicesByNameAsync(
                graphStore,
                serviceName,
                svc =>
                {
                    var existing = svc.AccessPolicy ?? new AccessPolicy();
                    return svc with
                    {
                        AccessPolicy = existing with
                        {
                            AllowAnonymous = request.AllowAnonymous ?? existing.AllowAnonymous,
                            AllowAnonymousWrite = request.AllowAnonymousWrite ?? existing.AllowAnonymousWrite,
                            AllowedRoles = request.AllowedRoles ?? existing.AllowedRoles,
                            AllowedWriteRoles = request.AllowedWriteRoles ?? existing.AllowedWriteRoles,
                        },
                    };
                },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, graphProvider, serviceName, logger).ConfigureAwait(false);

            var refreshed = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            TryResolveServicesByName(refreshed, serviceName, out var updatedServices);
            var response = BuildSettingsResponse(serviceName, updatedServices ?? Array.Empty<MetadataV2Service>());
            return TypedResults.Ok(ApiResponse<ServiceSettingsResponse>.CreateSuccess(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.UpdateAccessPolicyFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Access policy update failed",
                detail: "An internal error occurred while updating access policy.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateTimeInfo(
            string serviceName,
            UpdateTimeInfoRequest request,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        // GAP (#1035 cutover 72/N): the v1 admin path exposed a service-scope time-info
        // editor that was actually applied to every layer on the service. In the V2
        // graph temporal metadata lives on MetadataV2Resource.Temporal — there is no
        // service-level slot. Rather than silently fan-out across resources (which
        // would mask field-typed validation gaps) we refuse the operation and direct
        // callers to the per-layer endpoint until a service-level temporal extension
        // is designed.
        _ = request;
        ServiceSettingsLog.UpdateTimeInfoFailed(
            logger,
            serviceName,
            new NotSupportedException(
                "Service-level TimeInfo has no Metadata v2 representation. Use the per-layer endpoint."));
        return Task.FromResult<Results<Ok<ApiResponse<ServiceSettingsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>(
            TypedResults.Problem(
                title: "Service-level TimeInfo update not supported",
                detail:
                    "Service-scope TimeInfo is not representable in the Metadata v2 graph. " +
                    "Use PUT /admin/services/{serviceName}/layers/{layerId}/metadata instead.",
                statusCode: StatusCodes.Status501NotImplemented));
    }

    private static async Task<Results<Ok<ApiResponse<LayerMetadataResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateLayerMetadata(
            string serviceName,
            int layerId,
            UpdateLayerMetadataRequest request,
            [FromServices] IMetadataV2GraphProvider graphProvider,
            [FromServices] IMetadataV2GraphStore graphStore,
            ILogger<ServiceSettingsEndpointsLog> logger,
            HttpContext context)
    {
        try
        {
            var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            if (!TryResolveServicesByName(snapshot, serviceName, out var services))
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Service '{serviceName}' not found."));
            }

            var featureServices = services
                .Where(service => ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer))
                .Where(service => IsFeatureServerService(snapshot.Graph, service))
                .ToArray();
            if (featureServices.Length == 0)
            {
                return TypedResults.Problem(
                    title: "FeatureServer service not found",
                    detail: $"Service '{serviceName}' does not resolve to a FeatureServer service.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var featureServiceIds = featureServices
                .Select(service => service.Metadata.Id)
                .ToHashSet(StringComparer.Ordinal);
            var targetResourceIds = snapshot.Graph.Publications
                .Where(publication =>
                    featureServiceIds.Contains(publication.ServiceId) &&
                    publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
                    publication.Identifier.IsNumeric &&
                    publication.LayerIndex == layerId)
                .Select(publication => publication.ResourceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (targetResourceIds.Length > 1)
            {
                return TypedResults.Problem(
                    title: "Ambiguous FeatureServer layer",
                    detail: $"Layer {layerId} in service '{serviceName}' resolves to multiple resources.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var resource = targetResourceIds.Length == 1
                ? snapshot.Graph.Resources.FirstOrDefault(candidate =>
                    string.Equals(candidate.Metadata.Id, targetResourceIds[0], StringComparison.Ordinal))
                : null;
            if (resource is null)
            {
                return TypedResults.NotFound(ApiResponse<object>.Failure($"Layer {layerId} not found in service '{serviceName}'."));
            }

            if (request.RasterMosaic?.MergeStrategy is { Length: > 0 } mergeStrategyValue
                && !TryNormalizeMergeStrategy(mergeStrategyValue, out _))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(
                    $"Invalid mergeStrategy '{mergeStrategyValue}'. Allowed values: newest, oldest, average, max, min."));
            }

            var governanceRequested = request.License is not null ||
                request.Attribution is not null ||
                request.Publisher is not null ||
                request.LicenseUrl is not null ||
                request.SourceUrl is not null;
            if (!LayerSourceGovernance.TryCreate(
                    request.License,
                    request.Attribution,
                    request.Publisher,
                    request.LicenseUrl,
                    request.SourceUrl,
                    out var sourceGovernance,
                    out var governanceError))
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure($"Invalid source governance: {governanceError}"));
            }

            // RasterMosaic has no typed V2 home yet — we keep echoing the requested merge
            // strategy in the response (so PUT semantics are preserved for callers), but
            // it does not round-trip through the graph store. Documented in the mutator
            // below.
            RasterMosaicResponse? updatedRasterMosaic = null;
            if (request.RasterMosaic is not null)
            {
                string? mergeStrategy;
                if (request.RasterMosaic.MergeStrategy is null)
                {
                    mergeStrategy = null;
                }
                else if (request.RasterMosaic.MergeStrategy.Length == 0)
                {
                    mergeStrategy = null;
                }
                else
                {
                    TryNormalizeMergeStrategy(request.RasterMosaic.MergeStrategy, out var canonical);
                    mergeStrategy = canonical;
                }
                updatedRasterMosaic = new RasterMosaicResponse { MergeStrategy = mergeStrategy };
            }

            var mutation = await MutateResourceByIdAsync(
                graphStore,
                serviceName,
                layerId,
                resource.Metadata.Id,
                next =>
                {
                    if (request.AccessPolicy is { } incomingAccessPolicy)
                    {
                        var existingAccessPolicy = next.AccessPolicy ?? new AccessPolicy();
                        next = next with
                        {
                            AccessPolicy = existingAccessPolicy with
                            {
                                AllowAnonymous = incomingAccessPolicy.AllowAnonymous ?? existingAccessPolicy.AllowAnonymous,
                                AllowAnonymousWrite = incomingAccessPolicy.AllowAnonymousWrite ?? existingAccessPolicy.AllowAnonymousWrite,
                                AllowedRoles = incomingAccessPolicy.AllowedRoles ?? existingAccessPolicy.AllowedRoles,
                                AllowedWriteRoles = incomingAccessPolicy.AllowedWriteRoles ?? existingAccessPolicy.AllowedWriteRoles
                            }
                        };
                    }

                    if (request.TimeInfo is { } incomingTimeInfo)
                    {
                        var existingTemporal = next.Temporal;
                        var startTimeField = incomingTimeInfo.StartTimeField is ""
                            ? null
                            : incomingTimeInfo.StartTimeField ?? existingTemporal?.StartTimeField;
                        var endTimeField = incomingTimeInfo.EndTimeField is ""
                            ? null
                            : incomingTimeInfo.EndTimeField ?? existingTemporal?.EndTimeField;
                        var trackIdField = incomingTimeInfo.TrackIdField is ""
                            ? null
                            : incomingTimeInfo.TrackIdField ?? existingTemporal?.TrackIdField;
                        next = next with
                        {
                            Temporal = ToV2Temporal(
                                startTimeField,
                                endTimeField,
                                trackIdField,
                                existing: existingTemporal)
                        };
                    }

                    next = next with
                    {
                        Metadata = governanceRequested
                            ? ApplySourceGovernancePatch(
                                next.Metadata,
                                request,
                                sourceGovernance,
                                DateTimeOffset.UtcNow)
                            : next.Metadata,
                    };
                    // RasterMosaic has no typed V2 home yet — silently drop until the V2
                    // raster extension lands. The v1 admin shape is preserved so
                    // GET responses can still echo what the caller PUT.
                    return next;
                },
                context.RequestAborted).ConfigureAwait(false);
            if (mutation.BindingConflict)
            {
                return TypedResults.Problem(
                    title: "FeatureServer layer changed during update",
                    detail: $"Layer {layerId} in service '{serviceName}' was relinked while its metadata was being updated.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var persistedResource = mutation.Resource;
            if (persistedResource is null)
            {
                return TypedResults.NotFound(
                    ApiResponse<object>.Failure($"Layer {layerId} not found in service '{serviceName}'."));
            }

            await InvalidateServiceCatalogCacheAsync(context, graphProvider, serviceName, logger).ConfigureAwait(false);

            var response = BuildLayerMetadataResponse(
                layerId,
                persistedResource.Metadata.Name,
                persistedResource.Metadata,
                persistedResource.AccessPolicy,
                persistedResource.Temporal,
                updatedRasterMosaic);
            return TypedResults.Ok(ApiResponse<LayerMetadataResponse>.CreateSuccess(response));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.UpdateLayerMetadataFailed(logger, serviceName, layerId, ex);
            return TypedResults.Problem(
                title: "Layer metadata update failed",
                detail: "An internal error occurred while updating layer metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Looks up every V2 service whose <c>Metadata.Name</c> matches
    /// <paramref name="serviceName"/> (case-insensitive). Multiple V2 services may share
    /// a display name when the same logical service is exposed under multiple protocols
    /// (FeatureServer / MapServer / Stac all named "test"); the v1 admin contract was
    /// service-name-keyed, so we collapse those entries the same way at response time.
    /// </summary>
    private static bool TryResolveServicesByName(
        MetadataV2GraphSnapshot snapshot,
        string serviceName,
        out IReadOnlyList<MetadataV2Service> services)
    {
        var matches = snapshot.Graph.Services
            .Where(s => string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        services = matches;
        return matches.Length > 0;
    }

    private static bool IsFeatureServerService(MetadataV2Graph graph, MetadataV2Service service)
        => service.ServiceType == MetadataV2ServiceType.EsriFeatureService ||
           service.Protocols.Contains(ServiceProtocols.FeatureServer, StringComparer.OrdinalIgnoreCase) ||
           graph.Publications.Any(publication =>
               string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal) &&
               publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);

    private static ServiceSettingsResponse BuildSettingsResponse(
        string serviceName,
        IReadOnlyList<MetadataV2Service> services)
    {
        // Union enabled protocols across every V2 service entry that shares this display
        // name. Service-level access policy collapses to the first matching service's
        // policy (deterministic per the graph's storage order). MapServer + TimeInfo
        // have no V2 home yet — see the matching gaps on the update handlers — so the
        // response carries default MapServer config and a null TimeInfo.
        var enabledProtocols = services
            .SelectMany(s => s.Protocols)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (enabledProtocols.Length == 0)
        {
            enabledProtocols = MetadataV2ServiceProtocols.All;
        }
        var accessPolicy = services.Select(s => s.AccessPolicy).FirstOrDefault(p => p is not null);

        return new ServiceSettingsResponse
        {
            ServiceName = serviceName,
            EnabledProtocols = enabledProtocols,
            AvailableProtocols = MetadataV2ServiceProtocols.All,
            AccessPolicy = accessPolicy is not null ? new AccessPolicyResponse
            {
                AllowAnonymous = accessPolicy.AllowAnonymous,
                AllowAnonymousWrite = accessPolicy.AllowAnonymousWrite,
                AllowedRoles = accessPolicy.AllowedRoles,
                AllowedWriteRoles = accessPolicy.AllowedWriteRoles
            } : null,
            // GAP (#1035 cutover 72/N): V2 has no service-scope TimeInfo; the v1 path
            // surfaced a service-level TimeInfo block here, and the matching update
            // handler now refuses the write. Mirror that here by always returning null.
            TimeInfo = null,
            MapServer = new MapServerSettingsResponse
            {
                MaxImageWidth = DefaultMapServerMaxImageWidth,
                MaxImageHeight = DefaultMapServerMaxImageHeight,
                DefaultImageWidth = DefaultMapServerImageWidth,
                DefaultImageHeight = DefaultMapServerImageHeight,
                DefaultDpi = DefaultMapServerDpi,
                DefaultFormat = DefaultMapServerFormat,
                DefaultTransparent = true,
                MaxFeaturesPerLayer = DefaultMapServerMaxFeaturesPerLayer
            }
        };
    }

    private static LayerMetadataResponse BuildLayerMetadataResponse(
        int layerId,
        string layerName,
        MetadataV2ObjectMetadata metadata,
        AccessPolicy? accessPolicy,
        MetadataV2ResourceTemporal? timeInfo,
        RasterMosaicResponse? rasterMosaic)
    {
        return new LayerMetadataResponse
        {
            LayerId = layerId,
            LayerName = layerName,
            License = metadata.License,
            Attribution = metadata.Attribution,
            Publisher = metadata.Publisher,
            LicenseUrl = FindGovernanceLink(metadata, "license"),
            SourceUrl = FindGovernanceLink(metadata, "describedby"),
            AccessPolicy = accessPolicy is not null ? new AccessPolicyResponse
            {
                AllowAnonymous = accessPolicy.AllowAnonymous,
                AllowAnonymousWrite = accessPolicy.AllowAnonymousWrite,
                AllowedRoles = accessPolicy.AllowedRoles,
                AllowedWriteRoles = accessPolicy.AllowedWriteRoles
            } : null,
            TimeInfo = timeInfo is not null ? new TimeInfoResponse
            {
                StartTimeField = timeInfo.StartTimeField,
                EndTimeField = timeInfo.EndTimeField,
                TrackIdField = timeInfo.TrackIdField
            } : null,
            RasterMosaic = rasterMosaic
        };
    }

    private static MetadataV2ObjectMetadata ApplySourceGovernancePatch(
        MetadataV2ObjectMetadata current,
        UpdateLayerMetadataRequest request,
        LayerSourceGovernance? normalized,
        DateTimeOffset updatedAt)
    {
        var effectiveLicense = request.License is null ? current.License : normalized?.License;
        var currentLicenseUrl = FindGovernanceLink(current, "license");
        var requestedLicenseUrl = request.LicenseUrl;
        var normalizedLicenseUrl = normalized?.LicenseUrl;
        if (request.License is not null && request.LicenseUrl is null)
        {
            var previousDerivedLicenseUrl = LayerSourceGovernance.GetSpdxLicenseUrl(current.License);
            if (currentLicenseUrl is null ||
                string.Equals(currentLicenseUrl, previousDerivedLicenseUrl, StringComparison.Ordinal))
            {
                normalizedLicenseUrl = normalized?.EffectiveLicenseUrl;
                requestedLicenseUrl = normalizedLicenseUrl ?? string.Empty;
            }
        }

        var links = current.Links.ToList();
        PatchGovernanceLink(
            links,
            requestedLicenseUrl,
            normalizedLicenseUrl,
            "license",
            effectiveLicense,
            refreshTitle: request.License is not null);
        PatchGovernanceLink(
            links,
            request.SourceUrl,
            normalized?.SourceUrl,
            "describedby",
            "Source documentation");

        return current with
        {
            License = effectiveLicense,
            Attribution = request.Attribution is null ? current.Attribution : normalized?.Attribution,
            Publisher = request.Publisher is null ? current.Publisher : normalized?.Publisher,
            Links = links,
            UpdatedAt = current.UpdatedAt is { } currentUpdatedAt && currentUpdatedAt > updatedAt
                ? currentUpdatedAt
                : updatedAt
        };
    }

    private static void PatchGovernanceLink(
        List<MetadataV2Link> links,
        string? requestedValue,
        string? normalizedValue,
        string relation,
        string? title,
        bool refreshTitle = false)
    {
        var currentIndex = FindGovernanceLinkIndex(links, relation);

        if (requestedValue is null)
        {
            if (refreshTitle && currentIndex >= 0)
            {
                links[currentIndex] = links[currentIndex] with { Title = title };
            }

            return;
        }

        MetadataV2Link? previous = null;
        if (currentIndex >= 0)
        {
            previous = links[currentIndex];
            links.RemoveAt(currentIndex);
        }

        if (normalizedValue is not null)
        {
            var replacement = previous is null
                ? new MetadataV2Link
                {
                    Href = normalizedValue,
                    Rel = relation,
                    Title = title,
                    ManagedBy = LayerSourceGovernance.LinkManager
                }
                : previous with
                {
                    Href = normalizedValue,
                    Rel = relation,
                    Title = title,
                    ManagedBy = LayerSourceGovernance.LinkManager
                };
            if (currentIndex >= 0)
            {
                links.Insert(currentIndex, replacement);
            }
            else
            {
                links.Add(replacement);
            }
        }
    }

    private static string? FindGovernanceLink(MetadataV2ObjectMetadata metadata, string relation)
    {
        var relationLinks = metadata.Links
            .Where(link => string.Equals(link.Rel, relation, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return relationLinks.FirstOrDefault(link =>
                   string.Equals(
                       link.ManagedBy,
                       LayerSourceGovernance.LinkManager,
                       StringComparison.Ordinal))?.Href ??
               relationLinks.FirstOrDefault()?.Href;
    }

    private static int FindGovernanceLinkIndex(List<MetadataV2Link> links, string relation)
    {
        var relationIndex = -1;
        for (var i = 0; i < links.Count; i++)
        {
            if (!string.Equals(links[i].Rel, relation, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            relationIndex = relationIndex < 0 ? i : relationIndex;
            if (string.Equals(
                    links[i].ManagedBy,
                    LayerSourceGovernance.LinkManager,
                    StringComparison.Ordinal))
            {
                return i;
            }
        }

        return relationIndex;
    }

    /// <summary>
    /// Validates a raster mosaic merge strategy against the canonical set and normalizes to
    /// the lowercase canonical token. Allowed: newest, oldest, average, max, min.
    /// </summary>
    private static bool TryNormalizeMergeStrategy(string value, out string canonical)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "newest":
                canonical = "newest";
                return true;
            case "oldest":
                canonical = "oldest";
                return true;
            case "average":
                canonical = "average";
                return true;
            case "max":
                canonical = "max";
                return true;
            case "min":
                canonical = "min";
                return true;
            default:
                canonical = string.Empty;
                return false;
        }
    }

    private static async Task InvalidateServiceCatalogCacheAsync(
        HttpContext context,
        IMetadataV2GraphProvider graphProvider,
        string serviceName,
        ILogger<ServiceSettingsEndpointsLog> logger)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator == null)
        {
            return;
        }

        try
        {
            // V2 cutover: resolve the service's layer ids from numeric publications
            // in the canonical graph. Layer ids come from the publication
            // LayerIndex for every numeric publication on a matching service.
            var snapshot = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            var serviceIds = snapshot.Graph.Services
                .Where(s => string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Metadata.Id)
                .ToHashSet(StringComparer.Ordinal);
            var layerIds = snapshot.Graph.Publications
                .Where(p => serviceIds.Contains(p.ServiceId) && p.Identifier.IsNumeric)
                .Select(p => p.LayerIndex)
                .Where(layerIndex => layerIndex.HasValue)
                .Select(layerIndex => layerIndex!.Value)
                .Distinct()
                .ToArray();

            await cacheInvalidator.InvalidateServiceCatalogAsync(
                serviceName,
                layerIds,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceSettingsLog.InvalidateServiceCatalogCacheFailed(logger, serviceName, ex);
        }
    }

    // The Metadata v2 graph store uses optimistic concurrency: SaveAsync rejects a stale expected-etag with an
    // "etag mismatch" InvalidOperationException. Background writers (catalog cache, CRS warmup) can bump the
    // etag between our read and write, so the read-modify-write is retried a few times before surfacing.
    private const int MetadataMutationMaxAttempts = 5;

    private static bool IsEtagMismatch(Exception exception) =>
        exception is InvalidOperationException
        && exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the canonical Metadata v2 graph, applies <paramref name="mutate"/> to every
    /// service whose <c>Metadata.Name</c> matches <paramref name="serviceName"/>
    /// (case-insensitively), and persists the result. Multiple service entries can share
    /// a name when the same logical service is exposed under different protocols (e.g.
    /// FeatureServer / MapServer / Stac all named "test"), and the v1 admin endpoint
    /// updated all of them in one row because protocol toggles lived on a single
    /// per-service settings record. Retries on optimistic-concurrency etag mismatch.
    /// </summary>
    private static async Task MutateServicesByNameAsync(
        IMetadataV2GraphStore graphStore,
        string serviceName,
        Func<MetadataV2Service, MetadataV2Service> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var services = snapshot.Graph.Services.ToArray();
            var mutatedAny = false;
            for (var i = 0; i < services.Length; i++)
            {
                if (string.Equals(services[i].Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
                {
                    services[i] = mutate(services[i]);
                    mutatedAny = true;
                }
            }

            if (!mutatedAny)
            {
                return;
            }

            var updated = snapshot.Graph with
            {
                Services = services,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply the mutation against the fresh snapshot.
            }
        }
    }

    /// <summary>
    /// Loads the canonical Metadata v2 graph, applies <paramref name="mutate"/> to the
    /// uniquely resolved canonical resource, and persists the result. The immutable
    /// resource id is carried across optimistic-concurrency retries, and the current
    /// FeatureServer publication is revalidated before each attempt so a relink cannot
    /// redirect or orphan the patch.
    /// </summary>
    private static async Task<(MetadataV2Resource? Resource, bool BindingConflict)> MutateResourceByIdAsync(
        IMetadataV2GraphStore graphStore,
        string serviceName,
        int layerId,
        string resourceId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var bindingState = ResolveFeatureLayerBindingState(
                snapshot.Graph,
                serviceName,
                layerId,
                resourceId);
            if (bindingState == FeatureLayerBindingState.Conflict)
            {
                return (null, true);
            }
            if (bindingState == FeatureLayerBindingState.NotFound)
            {
                return (null, false);
            }

            var resources = snapshot.Graph.Resources.ToArray();
            var resourceIndex = Array.FindIndex(resources, resource =>
                string.Equals(resource.Metadata.Id, resourceId, StringComparison.Ordinal));
            if (resourceIndex < 0)
            {
                return (null, false);
            }

            var mutatedResource = mutate(resources[resourceIndex]);
            resources[resourceIndex] = mutatedResource;

            var updated = snapshot.Graph with
            {
                Resources = resources,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return (mutatedResource, false);
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply against the fresh snapshot.
            }
        }
    }

    private static FeatureLayerBindingState ResolveFeatureLayerBindingState(
        MetadataV2Graph graph,
        string serviceName,
        int layerId,
        string expectedResourceId)
    {
        var matchingServices = graph.Services
            .Where(service => string.Equals(
                service.Metadata.Name,
                serviceName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingServices.Length == 0)
        {
            return FeatureLayerBindingState.NotFound;
        }

        var featureServiceIds = matchingServices
            .Where(service => ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer))
            .Where(service => IsFeatureServerService(graph, service))
            .Select(service => service.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (featureServiceIds.Count == 0)
        {
            return FeatureLayerBindingState.Conflict;
        }

        var currentResourceIds = graph.Publications
            .Where(publication =>
                featureServiceIds.Contains(publication.ServiceId) &&
                publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
                publication.Identifier.IsNumeric &&
                publication.LayerIndex == layerId)
            .Select(publication => publication.ResourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (currentResourceIds.Length == 0)
        {
            return FeatureLayerBindingState.NotFound;
        }
        if (currentResourceIds.Length != 1 ||
            !string.Equals(currentResourceIds[0], expectedResourceId, StringComparison.Ordinal))
        {
            return FeatureLayerBindingState.Conflict;
        }

        return graph.Resources.Any(resource =>
            string.Equals(resource.Metadata.Id, expectedResourceId, StringComparison.Ordinal))
            ? FeatureLayerBindingState.Bound
            : FeatureLayerBindingState.NotFound;
    }

    private enum FeatureLayerBindingState
    {
        Bound,
        NotFound,
        Conflict,
    }

    /// <summary>
    /// Creates the V2 <see cref="MetadataV2ResourceTemporal"/> block. Treats an entirely-empty time info as
    /// "clear temporal" — returns null so the resource turns non-temporal — to preserve
    /// clear-on-empty-PUT semantics. Preserves an existing declared extent when
    /// only the field names are changing.
    /// </summary>
    private static MetadataV2ResourceTemporal? ToV2Temporal(
        string? startTimeField,
        string? endTimeField,
        string? trackIdField,
        MetadataV2ResourceTemporal? existing)
    {
        if (string.IsNullOrEmpty(startTimeField)
            && string.IsNullOrEmpty(endTimeField)
            && string.IsNullOrEmpty(trackIdField))
        {
            return null;
        }

        return new MetadataV2ResourceTemporal
        {
            StartTimeField = startTimeField,
            EndTimeField = endTimeField,
            TrackIdField = trackIdField,
            Extent = existing?.Extent,
        };
    }
}
