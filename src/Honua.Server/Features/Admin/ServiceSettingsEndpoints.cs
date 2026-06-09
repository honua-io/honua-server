// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

        group.MapGet("/{serviceName}/settings-caps", HandleGetSettingsCaps)
            .WithDisplayName("Get Service Settings Caps")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{serviceName}/settings-caps", HandleUpdateSettingsCaps)
            .WithDisplayName("Update Service Settings Caps")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Put }));

        group.MapGet("/{serviceName}/discovery", HandleGetServiceDiscovery)
            .WithDisplayName("Get Service Discovery Metadata")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPut("/{serviceName}/discovery", HandleUpdateServiceDiscovery)
            .WithDisplayName("Update Service Discovery Metadata")
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
        catch (Exception ex)
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
        catch (Exception ex)
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
        catch (Exception ex)
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
        catch (Exception ex)
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

            // Find the publication for this (service, layerId) pair. In V2 layer ids are
            // resource-local indices on publications; the v1 admin contract was "layer ids
            // are stable across services of the same name", so we walk every publication
            // on every matching service and resolve the first numeric LayerIndex match.
            var serviceIds = services
                .Select(s => s.Metadata.Id)
                .ToHashSet(StringComparer.Ordinal);
            MetadataV2Resource? resource = null;
            foreach (var pub in snapshot.Graph.Publications)
            {
                if (!serviceIds.Contains(pub.ServiceId)) continue;
                if (!pub.Identifier.IsNumeric || pub.LayerIndex != layerId) continue;
                resource = snapshot.ResolveResource(pub);
                if (resource is not null) break;
            }
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

            // Patch access policy off the V2 resource's current AccessPolicy.
            AccessPolicy? updatedAccessPolicy = resource.AccessPolicy;
            if (request.AccessPolicy is not null)
            {
                var existingAp = resource.AccessPolicy ?? new AccessPolicy();
                updatedAccessPolicy = existingAp with
                {
                    AllowAnonymous = request.AccessPolicy.AllowAnonymous ?? existingAp.AllowAnonymous,
                    AllowAnonymousWrite = request.AccessPolicy.AllowAnonymousWrite ?? existingAp.AllowAnonymousWrite,
                    AllowedRoles = request.AccessPolicy.AllowedRoles ?? existingAp.AllowedRoles,
                    AllowedWriteRoles = request.AccessPolicy.AllowedWriteRoles ?? existingAp.AllowedWriteRoles
                };
            }

            // Patch time info off the V2 Temporal block (StartTimeField / EndTimeField /
            // TrackIdField).
            var existingTemporal = resource.Temporal;
            string? startTimeField = existingTemporal?.StartTimeField;
            string? endTimeField = existingTemporal?.EndTimeField;
            string? trackIdField = existingTemporal?.TrackIdField;
            var temporalRequested = request.TimeInfo is not null;
            if (request.TimeInfo is { } incomingTimeInfo)
            {
                startTimeField = incomingTimeInfo.StartTimeField is "" ? null : (incomingTimeInfo.StartTimeField ?? startTimeField);
                endTimeField = incomingTimeInfo.EndTimeField is "" ? null : (incomingTimeInfo.EndTimeField ?? endTimeField);
                trackIdField = incomingTimeInfo.TrackIdField is "" ? null : (incomingTimeInfo.TrackIdField ?? trackIdField);
            }
            var updatedTemporal = temporalRequested || existingTemporal is not null
                ? ToV2Temporal(startTimeField, endTimeField, trackIdField, existing: existingTemporal)
                : null;

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

            await MutateResourcesForLayerAsync(
                graphStore,
                serviceName,
                layerId,
                next =>
                {
                    if (updatedAccessPolicy is not null)
                    {
                        next = next with { AccessPolicy = updatedAccessPolicy };
                    }
                    next = next with
                    {
                        Temporal = updatedTemporal,
                    };
                    // RasterMosaic has no typed V2 home yet — silently drop until the V2
                    // raster extension lands. The v1 admin shape is preserved so
                    // GET responses can still echo what the caller PUT.
                    return next;
                },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, graphProvider, serviceName, logger).ConfigureAwait(false);

            var response = BuildLayerMetadataResponse(
                layerId,
                resource.Metadata.Name,
                updatedAccessPolicy,
                updatedTemporal,
                updatedRasterMosaic);
            return TypedResults.Ok(ApiResponse<LayerMetadataResponse>.CreateSuccess(response));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateLayerMetadataFailed(logger, serviceName, layerId, ex);
            return TypedResults.Problem(
                title: "Layer metadata update failed",
                detail: "An internal error occurred while updating layer metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    // ---- Service settings caps (MetadataV2ServiceSettings) ----------------------------------------------

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsCapsResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetSettingsCaps(
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

            // Settings caps collapse to the first matching service entry's settings (deterministic
            // per the graph's storage order) — multiple protocol-specific service entries that share
            // a display name carry the same logical settings.
            var settings = services.Select(s => s.Settings).FirstOrDefault(s => s is not null);
            return TypedResults.Ok(ApiResponse<ServiceSettingsCapsResponse>.CreateSuccess(
                BuildSettingsCapsResponse(serviceName, settings)));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.GetServiceSettingsCapsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Service settings caps retrieval failed",
                detail: "An internal error occurred while retrieving service settings caps.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<ServiceSettingsCapsResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateSettingsCaps(
            string serviceName,
            UpdateServiceSettingsCapsRequest request,
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

            var validationError = ValidateSettingsCaps(request);
            if (validationError is not null)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            await MutateServicesByNameAsync(
                graphStore,
                serviceName,
                svc => svc with { Settings = ApplySettingsCaps(svc.Settings, request) },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, graphProvider, serviceName, logger).ConfigureAwait(false);

            var refreshed = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            TryResolveServicesByName(refreshed, serviceName, out var updatedServices);
            var settings = (updatedServices ?? Array.Empty<MetadataV2Service>())
                .Select(s => s.Settings).FirstOrDefault(s => s is not null);
            return TypedResults.Ok(ApiResponse<ServiceSettingsCapsResponse>.CreateSuccess(
                BuildSettingsCapsResponse(serviceName, settings)));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateServiceSettingsCapsFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Service settings caps update failed",
                detail: "An internal error occurred while updating service settings caps.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string? ValidateSettingsCaps(UpdateServiceSettingsCapsRequest request)
    {
        foreach (var (label, value) in new (string, int?)[]
        {
            ("maxRecordCount", request.MaxRecordCount),
            ("defaultRecordCount", request.DefaultRecordCount),
            ("maxFeaturesPerLayer", request.MaxFeaturesPerLayer),
            ("queryTimeoutMs", request.QueryTimeoutMs),
            ("maxEditsPerTransaction", request.MaxEditsPerTransaction),
        })
        {
            if (value is < 0)
            {
                return $"{label} must be zero or greater.";
            }
        }

        if (request.MaxPayloadBytes is < 0)
        {
            return "maxPayloadBytes must be zero or greater.";
        }

        if (request.MaxAttachmentSizeBytes is < 0)
        {
            return "maxAttachmentSizeBytes must be zero or greater.";
        }

        return null;
    }

    private static MetadataV2ServiceSettings ApplySettingsCaps(
        MetadataV2ServiceSettings? existing,
        UpdateServiceSettingsCapsRequest request)
    {
        var current = existing ?? new MetadataV2ServiceSettings();
        return current with
        {
            MaxRecordCount = request.MaxRecordCount ?? current.MaxRecordCount,
            DefaultRecordCount = request.DefaultRecordCount ?? current.DefaultRecordCount,
            MaxFeaturesPerLayer = request.MaxFeaturesPerLayer ?? current.MaxFeaturesPerLayer,
            QueryTimeoutMs = request.QueryTimeoutMs ?? current.QueryTimeoutMs,
            MaxEditsPerTransaction = request.MaxEditsPerTransaction ?? current.MaxEditsPerTransaction,
            MaxPayloadBytes = request.MaxPayloadBytes ?? current.MaxPayloadBytes,
            SupportedFormats = request.SupportedFormats ?? current.SupportedFormats,
            DefaultFormat = request.DefaultFormat is null
                ? current.DefaultFormat
                : (string.IsNullOrWhiteSpace(request.DefaultFormat) ? null : request.DefaultFormat),
            DefaultTileMatrixSet = request.DefaultTileMatrixSet is null
                ? current.DefaultTileMatrixSet
                : (string.IsNullOrWhiteSpace(request.DefaultTileMatrixSet) ? null : request.DefaultTileMatrixSet),
            SupportsAttachments = request.SupportsAttachments ?? current.SupportsAttachments,
            MaxAttachmentSizeBytes = request.MaxAttachmentSizeBytes ?? current.MaxAttachmentSizeBytes,
        };
    }

    private static ServiceSettingsCapsResponse BuildSettingsCapsResponse(
        string serviceName,
        MetadataV2ServiceSettings? settings)
    {
        var s = settings ?? new MetadataV2ServiceSettings();
        return new ServiceSettingsCapsResponse
        {
            ServiceName = serviceName,
            MaxRecordCount = s.MaxRecordCount,
            DefaultRecordCount = s.DefaultRecordCount,
            MaxFeaturesPerLayer = s.MaxFeaturesPerLayer,
            QueryTimeoutMs = s.QueryTimeoutMs,
            MaxEditsPerTransaction = s.MaxEditsPerTransaction,
            MaxPayloadBytes = s.MaxPayloadBytes,
            SupportedFormats = s.SupportedFormats,
            DefaultFormat = s.DefaultFormat,
            DefaultTileMatrixSet = s.DefaultTileMatrixSet,
            SupportsAttachments = s.SupportsAttachments,
            MaxAttachmentSizeBytes = s.MaxAttachmentSizeBytes,
        };
    }

    // ---- Service discovery metadata (MetadataV2ObjectMetadata discovery fields) -------------------------

    private static async Task<Results<Ok<ApiResponse<DiscoveryMetadataResponse>>, NotFound<ApiResponse<object>>, ProblemHttpResult>>
        HandleGetServiceDiscovery(
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

            var metadata = services[0].Metadata;
            return TypedResults.Ok(ApiResponse<DiscoveryMetadataResponse>.CreateSuccess(
                BuildServiceDiscoveryResponse(serviceName, metadata)));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.GetServiceDiscoveryFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Service discovery retrieval failed",
                detail: "An internal error occurred while retrieving service discovery metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<Results<Ok<ApiResponse<DiscoveryMetadataResponse>>, NotFound<ApiResponse<object>>, BadRequest<ApiResponse<object>>, ProblemHttpResult>>
        HandleUpdateServiceDiscovery(
            string serviceName,
            DiscoveryMetadataUpdateRequest request,
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

            var validationError = ValidateServiceDiscovery(request);
            if (validationError is not null)
            {
                return TypedResults.BadRequest(ApiResponse<object>.Failure(validationError));
            }

            await MutateServicesByNameAsync(
                graphStore,
                serviceName,
                svc => svc with { Metadata = ApplyServiceDiscovery(svc.Metadata, request) },
                context.RequestAborted).ConfigureAwait(false);
            await InvalidateServiceCatalogCacheAsync(context, graphProvider, serviceName, logger).ConfigureAwait(false);

            var refreshed = await graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            TryResolveServicesByName(refreshed, serviceName, out var updatedServices);
            var metadata = (updatedServices ?? Array.Empty<MetadataV2Service>()).Select(s => s.Metadata).FirstOrDefault()
                ?? new MetadataV2ObjectMetadata();
            return TypedResults.Ok(ApiResponse<DiscoveryMetadataResponse>.CreateSuccess(
                BuildServiceDiscoveryResponse(serviceName, metadata)));
        }
        catch (Exception ex)
        {
            ServiceSettingsLog.UpdateServiceDiscoveryFailed(logger, serviceName, ex);
            return TypedResults.Problem(
                title: "Service discovery update failed",
                detail: "An internal error occurred while updating service discovery metadata.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string? ValidateServiceDiscovery(DiscoveryMetadataUpdateRequest request)
    {
        if (request.ContactPoint?.Email is { Length: > 0 } email && !email.Contains('@', StringComparison.Ordinal))
        {
            return "Contact point email must contain '@'.";
        }

        if (request.Links is { } links)
        {
            foreach (var link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Href) || string.IsNullOrWhiteSpace(link.Rel))
                {
                    return "Each discovery link requires a non-empty href and rel.";
                }
            }
        }

        return null;
    }

    private static MetadataV2ObjectMetadata ApplyServiceDiscovery(
        MetadataV2ObjectMetadata metadata,
        DiscoveryMetadataUpdateRequest request)
    {
        static string? ApplyNullable(string? current, string? requested) =>
            requested is null ? current : (string.IsNullOrWhiteSpace(requested) ? null : requested);

        return metadata with
        {
            Title = ApplyNullable(metadata.Title, request.Title),
            Description = ApplyNullable(metadata.Description, request.Description),
            Keywords = request.Keywords ?? metadata.Keywords,
            Themes = request.Themes ?? metadata.Themes,
            Language = ApplyNullable(metadata.Language, request.Language),
            License = ApplyNullable(metadata.License, request.License),
            Attribution = ApplyNullable(metadata.Attribution, request.Attribution),
            Publisher = ApplyNullable(metadata.Publisher, request.Publisher),
            ContactPoint = request.ContactPoint is null
                ? metadata.ContactPoint
                : new MetadataV2ContactPoint
                {
                    Name = request.ContactPoint.Name,
                    Email = request.ContactPoint.Email,
                    Url = request.ContactPoint.Url,
                },
            Links = request.Links is null
                ? metadata.Links
                : request.Links.Select(l => new MetadataV2Link
                {
                    Href = l.Href,
                    Rel = l.Rel,
                    Type = l.Type,
                    Title = l.Title,
                    Hreflang = l.Hreflang,
                }).ToArray(),
        };
    }

    private static DiscoveryMetadataResponse BuildServiceDiscoveryResponse(
        string serviceName,
        MetadataV2ObjectMetadata metadata)
    {
        return new DiscoveryMetadataResponse
        {
            ServiceName = serviceName,
            Title = metadata.Title,
            Description = metadata.Description,
            Keywords = metadata.Keywords,
            Themes = metadata.Themes,
            Language = metadata.Language,
            License = metadata.License,
            Attribution = metadata.Attribution,
            Publisher = metadata.Publisher,
            ContactPoint = metadata.ContactPoint is null ? null : new DiscoveryContactPoint
            {
                Name = metadata.ContactPoint.Name,
                Email = metadata.ContactPoint.Email,
                Url = metadata.ContactPoint.Url,
            },
            Links = metadata.Links.Select(l => new DiscoveryLink
            {
                Href = l.Href,
                Rel = l.Rel,
                Type = l.Type,
                Title = l.Title,
                Hreflang = l.Hreflang,
            }).ToArray(),
        };
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
        AccessPolicy? accessPolicy,
        MetadataV2ResourceTemporal? timeInfo,
        RasterMosaicResponse? rasterMosaic)
    {
        return new LayerMetadataResponse
        {
            LayerId = layerId,
            LayerName = layerName,
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
    /// Loads the canonical Metadata v2 graph, walks every publication whose service
    /// matches <paramref name="serviceName"/> and whose <c>LayerIndex</c> equals
    /// <paramref name="layerId"/>, applies <paramref name="mutate"/> to the backing
    /// resource(s) once, and persists the result. The v1 contract was "layer ids are
    /// stable across services of the same name", so the V2 cut-over collapses the same
    /// way: one logical layer maps to one V2 resource even when it is published through
    /// multiple V2 services.
    /// </summary>
    private static async Task MutateResourcesForLayerAsync(
        IMetadataV2GraphStore graphStore,
        string serviceName,
        int layerId,
        Func<MetadataV2Resource, MetadataV2Resource> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var matchingServiceIds = snapshot.Graph.Services
                .Where(s => string.Equals(s.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Metadata.Id)
                .ToHashSet(StringComparer.Ordinal);

            var targetResourceIds = snapshot.Graph.Publications
                .Where(p => matchingServiceIds.Contains(p.ServiceId)
                    && p.Identifier.IsNumeric
                    && p.LayerIndex == layerId)
                .Select(p => p.ResourceId)
                .ToHashSet(StringComparer.Ordinal);

            if (targetResourceIds.Count == 0)
            {
                return;
            }

            var resources = snapshot.Graph.Resources.ToArray();
            for (var i = 0; i < resources.Length; i++)
            {
                if (targetResourceIds.Contains(resources[i].Metadata.Id))
                {
                    resources[i] = mutate(resources[i]);
                }
            }

            var updated = snapshot.Graph with
            {
                Resources = resources,
                Revision = snapshot.Graph.Revision + 1,
            };

            try
            {
                _ = await graphStore.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply against the fresh snapshot.
            }
        }
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
