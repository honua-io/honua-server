// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
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

internal static partial class OgcTileCacheExportEndpoints
{
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
        // Intentionally generic: ReadFromJsonAsync can throw JsonException, NotSupportedException,
        // or IOException for malformed/unreadable request bodies; map all of them to a 400 response.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.RequestDeserializationFailed(GetLogger(context), ex);
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
        var allowedHostSuffixes = context.RequestServices.GetAllowedMigrationServiceHostSuffixes();
        var validation = await OgcServiceUrlValidation.ValidateAsync(
            request.ServiceUrl,
            allowUnsafeLocalUrls,
            allowedHostSuffixes,
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

        var exportService = context.RequestServices.GetRequiredService<OgcTileCacheExportService>();

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
                    AllowUnsafeLocalUrls = allowUnsafeLocalUrls,
                    AllowedServiceHostSuffixes = allowedHostSuffixes
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
            await AdminResponseWriter.WriteErrorAsync(
                context,
                ImportValidationErrorMessage.FromArgument(ex, "Invalid tile-cache export request."),
                StatusCodes.Status400BadRequest);
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

    private static ILogger<OgcTileCacheExportEndpointsLog> GetLogger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILogger<OgcTileCacheExportEndpointsLog>>();

    internal sealed class OgcTileCacheExportEndpointsLog;

    private static partial class Log
    {
        [LoggerMessage(8120, LogLevel.Warning, "Failed to deserialize tile-cache export request")]
        public static partial void RequestDeserializationFailed(ILogger logger, Exception exception);
    }
}

internal sealed record OgcTileCacheExportApiRequest
{
    public string? ServiceUrl { get; init; }

    public string? Version { get; init; }

    public string[]? LayerIdentifiers { get; init; }

    public string[]? TileMatrixSetIdentifiers { get; init; }

    public string? TileFormat { get; init; }

    public string? StyleIdentifier { get; init; }

    public int? MinZoom { get; init; }

    public int? MaxZoom { get; init; }

    public OgcTileCacheExportWindowApiModel? Window { get; init; }

    public bool? DryRun { get; init; }

    public bool? ApplyMode { get; init; }

    public int? RequestTimeoutSeconds { get; init; }
}

internal sealed record OgcTileCacheExportWindowApiModel
{
    public required int MinColumn { get; init; }

    public required int MaxColumn { get; init; }

    public required int MinRow { get; init; }

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
