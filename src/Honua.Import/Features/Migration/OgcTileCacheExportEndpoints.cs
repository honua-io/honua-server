// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Migration;

/// <summary>
/// Admin endpoints for OGC WMTS tile-cache export (#1016 slice 4).
/// </summary>
internal static class OgcTileCacheExportEndpoints
{
    /// <summary>
    /// Maps tile-cache export endpoints.
    /// </summary>
    public static void MapOgcTileCacheExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v{version:apiVersion}/admin/import/ogc-tiles")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("OgcTileCacheExport")
            .RequireAdminAuthorization();

        _ = group.MapPost("/export", HandleExport)
            .WithName("ExportOgcTileCache")
            .WithSummary("Export XYZ/TMS tile caches from an automated WMTS migration plan into Honua's tile catalog");
    }

    private static async Task HandleExport(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        OgcTileCacheExportApiRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                OgcTileCacheExportJsonContext.Default.OgcTileCacheExportApiRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Invalid request body", StatusCodes.Status400BadRequest);
            return;
        }

        if (request == null)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "Request body is required", StatusCodes.Status400BadRequest);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            await AdminResponseWriter.WriteErrorAsync(context, "serviceUrl is required.", StatusCodes.Status400BadRequest);
            return;
        }

        var isDryRun = request.DryRun ?? false;
        var isApplyMode = request.ApplyMode ?? false;
        if (!isDryRun && !isApplyMode)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Non-dry-run tile-cache exports require applyMode=true to acknowledge that the reviewed migration plan will write tiles into the Honua tile catalog.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var minZoom = request.MinZoom ?? OgcTileCacheExportSafetyLimits.DefaultMinZoom;
        var maxZoom = request.MaxZoom ?? OgcTileCacheExportSafetyLimits.DefaultMaxZoom;
        if (minZoom < 0 || maxZoom < 0 || maxZoom < minZoom)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "minZoom and maxZoom must be non-negative and minZoom must not exceed maxZoom.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var timeoutSeconds = request.RequestTimeoutSeconds ?? 30;
        if (timeoutSeconds <= 0)
        {
            await AdminResponseWriter.WriteErrorAsync(context, "requestTimeoutSeconds must be greater than zero.", StatusCodes.Status400BadRequest);
            return;
        }

        var allowUnsafeLocalUrls = GeoServerImportExecutionSettings.ShouldAllowUnsafeLocalUrls(context.RequestServices);
        var validation = await OgcServiceUrlValidation.ValidateAsync(
            request.ServiceUrl,
            allowUnsafeLocalUrls,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            await AdminResponseWriter.WriteErrorAsync(context, validation.ErrorMessage!, StatusCodes.Status400BadRequest);
            return;
        }

        OgcTileCacheTileWindow? window = null;
        if (request.Window != null)
        {
            window = new OgcTileCacheTileWindow
            {
                MinColumn = request.Window.MinColumn,
                MaxColumn = request.Window.MaxColumn,
                MinRow = request.Window.MinRow,
                MaxRow = request.Window.MaxRow
            };
        }

        var exportService = context.RequestServices.GetRequiredService<IOgcTileCacheExportService>();

        OgcTileCacheExportResult result;
        try
        {
            result = await exportService.ExportAsync(
                new OgcTileCacheExportRequest
                {
                    ServiceUrl = request.ServiceUrl,
                    Version = request.Version,
                    LayerIdentifiers = request.LayerIdentifiers,
                    TileMatrixSetIdentifiers = request.TileMatrixSetIdentifiers,
                    TileFormat = request.TileFormat,
                    StyleIdentifier = request.StyleIdentifier,
                    MinZoom = minZoom,
                    MaxZoom = maxZoom,
                    Window = window,
                    DryRun = isDryRun,
                    ApplyMode = isApplyMode,
                    RequestTimeoutSeconds = timeoutSeconds,
                    AllowUnsafeLocalUrls = allowUnsafeLocalUrls
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context,
                "Tile-cache export was canceled by the operator.",
                StatusCodes.Status499ClientClosedRequest);
            return;
        }
        catch (ArgumentException ex)
        {
            await AdminResponseWriter.WriteErrorAsync(context, ex.Message, StatusCodes.Status400BadRequest);
            return;
        }

        if (!result.Success)
        {
            await Results.Json(result, OgcTileCacheExportJsonContext.Default.OgcTileCacheExportResult,
                    statusCode: StatusCodes.Status502BadGateway)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await Results.Json(result, OgcTileCacheExportJsonContext.Default.OgcTileCacheExportResult).ExecuteAsync(context).ConfigureAwait(false);
    }
}

/// <summary>
/// API request model for the WMTS tile-cache export endpoint.
/// </summary>
internal sealed record OgcTileCacheExportApiRequest
{
    /// <summary>WMTS service URL (root or GetCapabilities URL).</summary>
    public string? ServiceUrl { get; init; }

    /// <summary>Optional WMTS version.</summary>
    public string? Version { get; init; }

    /// <summary>Specific WMTS layer identifiers to export (omit to export every automated tile-set).</summary>
    public string[]? LayerIdentifiers { get; init; }

    /// <summary>Specific WMTS tile-matrix-set identifiers to export.</summary>
    public string[]? TileMatrixSetIdentifiers { get; init; }

    /// <summary>Output tile format (defaults to image/png).</summary>
    public string? TileFormat { get; init; }

    /// <summary>WMTS style identifier (defaults to "default").</summary>
    public string? StyleIdentifier { get; init; }

    /// <summary>Minimum zoom level (inclusive).</summary>
    public int? MinZoom { get; init; }

    /// <summary>Maximum zoom level (inclusive).</summary>
    public int? MaxZoom { get; init; }

    /// <summary>Optional per-zoom tile window.</summary>
    public OgcTileCacheExportWindowApiModel? Window { get; init; }

    /// <summary>Preview mode: resolve plan + URLs without fetching tile bytes.</summary>
    public bool? DryRun { get; init; }

    /// <summary>Operator opt-in safety gate for non-dry-run exports.</summary>
    public bool? ApplyMode { get; init; }

    /// <summary>Per-tile HTTP timeout in seconds.</summary>
    public int? RequestTimeoutSeconds { get; init; }
}

/// <summary>
/// API tile-window model.
/// </summary>
internal sealed record OgcTileCacheExportWindowApiModel
{
    /// <summary>Inclusive minimum tile column.</summary>
    public required int MinColumn { get; init; }

    /// <summary>Inclusive maximum tile column.</summary>
    public required int MaxColumn { get; init; }

    /// <summary>Inclusive minimum tile row.</summary>
    public required int MinRow { get; init; }

    /// <summary>Inclusive maximum tile row.</summary>
    public required int MaxRow { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OgcTileCacheExportApiRequest))]
[JsonSerializable(typeof(OgcTileCacheExportWindowApiModel))]
[JsonSerializable(typeof(OgcTileCacheExportResult))]
[JsonSerializable(typeof(OgcTileCacheExportedTileSet))]
[JsonSerializable(typeof(OgcTileCacheExportedTileSet[]))]
[JsonSerializable(typeof(IReadOnlyList<OgcTileCacheExportedTileSet>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class OgcTileCacheExportJsonContext : JsonSerializerContext
{
}
