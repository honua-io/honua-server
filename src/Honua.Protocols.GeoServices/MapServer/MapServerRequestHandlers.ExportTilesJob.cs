// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Tiles;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.MapServer.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.GeoServices.MapServer;

// Asynchronous durable exportTiles (Compact Cache V2 / TPKX) job lifecycle for MapServer (#2706).
// The durable path is selected only when the request explicitly negotiates Compact Cache V2 and
// the shared tile-export lifecycle service is registered; every other request keeps the existing
// synchronous flat-ZIP / exploded-TPK behavior byte-for-byte.
internal static partial class MapServerEndpoints
{
    private const string CompactV2StorageMode = "esriMapCacheStorageModeCompactV2";

    /// <summary>
    /// Detects an explicit Compact Cache V2 negotiation via the official
    /// <c>storageFormatType=esriMapCacheStorageModeCompactV2</c> parameter or the documented
    /// compatible TPKX aliases on <c>storageFormat</c>/<c>exportBy</c>.
    /// </summary>
    private static bool IsCompactV2Requested(Dictionary<string, StringValues> values)
    {
        var storageFormatType = GetValue(values, "storageFormatType");
        if (!string.IsNullOrWhiteSpace(storageFormatType)
            && string.Equals(storageFormatType.Trim(), CompactV2StorageMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var storageFormat = GetValue(values, "storageFormat") ?? GetValue(values, "exportBy");
        return storageFormat?.Trim().ToLowerInvariant() is "tpkx" or "compact" or "compactv2";
    }

    /// <summary>
    /// Builds and submits a durable tile-export job for a Compact Cache V2 request, returning the
    /// ArcGIS <c>{ jobId, jobStatus: "esriJobSubmitted" }</c> envelope. Validation, ownership,
    /// admission, and store-availability failures surface through the shared sanitized mapping.
    /// </summary>
    private static async Task<IResult> SubmitDurableExportAsync(
        HttpContext context,
        Dictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        var (plan, error) = await TryBuildDurableMapExportPlanAsync(context, values, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var jobService = context.RequestServices.GetRequiredService<ITileExportJobService>();
        try
        {
            var job = await jobService.SubmitAsync(
                plan!,
                idempotencyKey: GetValue(values, "idempotencyKey"),
                correlationId: context.TraceIdentifier,
                context.User,
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ExportTilesJobSubmitResponse { JobId = job.OperationId },
                MapServerJsonContext.Default.ExportTilesJobSubmitResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Projects a durable tile-export job's status onto the ArcGIS Map Service status envelope.</summary>
    private static async Task<IResult> HandleExportTilesJobStatus(HttpContext context, string serviceId, string jobId)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var jobService = context.RequestServices.GetService<ITileExportJobService>();
        if (jobService is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Asynchronous tile-export jobs are not available.");
        }

        try
        {
            var job = await jobService
                .GetStatusAsync(jobId, ScopeFor(serviceId), context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ExportTilesJobStatusResponse
                {
                    JobId = job.OperationId,
                    JobStatus = GPServerStatusMapping.ToEsriJobStatus(job.Status),
                    PercentComplete = job.PercentComplete,
                    Messages = BuildExportTilesJobMessages(job),
                },
                MapServerJsonContext.Default.ExportTilesJobStatusResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Cancels a durable tile-export job scoped to the submitting principal and this map service.</summary>
    private static async Task<IResult> HandleExportTilesJobCancel(HttpContext context, string serviceId, string jobId)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var jobService = context.RequestServices.GetService<ITileExportJobService>();
        if (jobService is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Asynchronous tile-export jobs are not available.");
        }

        try
        {
            await jobService.CancelAsync(jobId, ScopeFor(serviceId), context.User, cancellationToken).ConfigureAwait(false);
            var job = await jobService.GetStatusAsync(jobId, ScopeFor(serviceId), context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ExportTilesJobStatusResponse
                {
                    JobId = job.OperationId,
                    JobStatus = GPServerStatusMapping.ToEsriJobStatus(job.Status),
                    PercentComplete = job.PercentComplete,
                    Messages = BuildExportTilesJobMessages(job),
                },
                MapServerJsonContext.Default.ExportTilesJobStatusResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    /// <summary>Returns the ArcGIS <c>results/out_service_url</c> for a completed durable tile-export job.</summary>
    private static async Task<IResult> HandleExportTilesJobResult(HttpContext context, string serviceId, string jobId)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var jobService = context.RequestServices.GetService<ITileExportJobService>();
        if (jobService is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Asynchronous tile-export jobs are not available.");
        }

        try
        {
            var result = await jobService
                .GetResultAsync(jobId, ScopeFor(serviceId), context.User, cancellationToken).ConfigureAwait(false);
            return TypedResults.Json(
                new ExportTilesJobResultResponse
                {
                    JobId = jobId,
                    Results = new ExportTilesJobResults
                    {
                        OutServiceUrl = new ExportTilesJobResultValue
                        {
                            Value = result.DownloadUrl,
                            ExpiresAt = result.ExpiresAt,
                        },
                    },
                },
                MapServerJsonContext.Default.ExportTilesJobResultResponse);
        }
        catch (Exception exception) when (TileExportAdapterResults.TryMap(context, exception) is { } mapped)
        {
            return mapped;
        }
    }

    private static TileExportJobScope ScopeFor(string serviceId)
        => new(TileExportSourceKind.Map, serviceId);

    private static IReadOnlyList<ExportTilesJobMessage> BuildExportTilesJobMessages(
        Honua.Core.Features.ControlPlane.Domain.ExecutionJobRecord job)
        => job.Status == Honua.Core.Features.ControlPlane.Domain.ExecutionJobStatus.Failed
            && !string.IsNullOrWhiteSpace(job.ErrorMessage)
                ? [new ExportTilesJobMessage { Type = "esriJobMessageTypeError", Description = job.ErrorMessage }]
                : [];

    /// <summary>
    /// Mirrors the synchronous <c>TryBuildExportTilesPlanAsync</c> parsing (service validation,
    /// access gate, levels/maxTiles/extent) but produces a durable <see cref="TileExportJobPlan"/>
    /// bound to the geometry-bearing published layers by their public layer index.
    /// </summary>
    private static async Task<(TileExportJobPlan? Plan, IResult? Error)> TryBuildDurableMapExportPlanAsync(
        HttpContext context,
        Dictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return (null, serviceError);
        }

        var outputFormat = GetValue(values, "f") ?? "json";
        if (!string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputFormat, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                $"Output format '{outputFormat}' is not supported."));
        }

        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        if (!TryParseExportTileLevels(
                GetValue(values, "levels"),
                GetValue(values, "minZoom"),
                GetValue(values, "maxZoom"),
                limits,
                out var requestedZooms,
                out _,
                out _,
                out var levelsError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, levelsError ?? "Invalid levels parameter."));
        }

        if (requestedZooms.Length < 2)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context, "Compact Cache V2 tile export requires at least two zoom levels."));
        }

        if (!TryParseExportTilesMaxTiles(GetValue(values, "maxTiles"), limits, out var maxTiles, out var maxTilesError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, maxTilesError ?? "Invalid maxTiles parameter."));
        }

        if (!TryParseExportTilesExtent(
                GetValue(values, "exportExtent") ?? GetValue(values, "bbox"),
                GetValue(values, "exportExtentSR") ?? GetValue(values, "bboxSR"),
                out var sourceExtent,
                out var sourceSrid,
                out var extentError))
        {
            return (null, StandardErrorHelpers.CreateBadRequest(context, extentError ?? "Invalid exportExtent parameter."));
        }

        var extentTransform = await TryTransformExtentAsync(
            context,
            sourceExtent,
            sourceSrid,
            SpatialReference.WGS84.Wkid,
            cancellationToken).ConfigureAwait(false);
        if (!extentTransform.IsSuccess)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context,
                extentTransform.Error ?? "Invalid exportExtent spatial reference."));
        }

        var bounds = NormalizeExportTilesBounds(extentTransform.Extent);

        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            ServiceProtocols.MapServer,
            context,
            cancellationToken: cancellationToken);
        if (!serviceResult.IsValid)
        {
            return (null, serviceResult.ErrorResult!);
        }

        var service = serviceResult.Service!;
        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);

        var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
            context,
            publishedLayers.Select(static layer => layer.Resource),
            service);
        if (accessError is not null)
        {
            return (null, accessError);
        }

        // The producer resolves LayerId as the PUBLIC layer index, so the descriptor must carry
        // PublicLayerId (not StorageLayerId). Only geometry-bearing published layers render, so the
        // selection uses the same predicate as the synchronous exportTiles render path.
        var layers = publishedLayers
            .Where(static layer => HasMapServerGeometry(layer.Resource))
            .Select(static layer => new TileExportMapLayerSelection(
                layer.PublicLayerId.ToString(CultureInfo.InvariantCulture),
                "default",
                0))
            .ToImmutableArray();
        if (layers.Length == 0)
        {
            return (null, StandardErrorHelpers.CreateBadRequest(
                context, "exportTiles requires at least one geometry layer."));
        }

        var descriptor = new TileExportMapSourceDescriptor(
            snapshot.Revision,
            layers,
            DataWatermark: string.Create(CultureInfo.InvariantCulture, $"metadata-{snapshot.Revision}"),
            SubmissionReuseScope: null);

        var plan = new TileExportJobPlan
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = serviceId,
            Source = descriptor,
            ZoomLevels = [.. requestedZooms],
            West = bounds[0],
            South = bounds[1],
            East = bounds[2],
            North = bounds[3],
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = maxTiles,
            MaxArtifactBytes = 1024L * 1024 * 1024,
            RetentionSeconds = ResolveRetentionSeconds(context),
        };

        return (plan, null);
    }

    private static int ResolveRetentionSeconds(HttpContext context)
    {
        var ttl = context.RequestServices.GetService<IOptions<CloudStorageOptions>>()?.Value.DefaultTimeToLive;
        var seconds = ttl is { } value && value > TimeSpan.Zero ? (long)value.TotalSeconds : 86_400L;
        return (int)Math.Clamp(seconds, 60L, 7L * 24 * 60 * 60);
    }
}
