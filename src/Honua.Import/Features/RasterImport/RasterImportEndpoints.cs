// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
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

namespace Honua.Import.RasterImport;

/// <summary>
/// Raster file import endpoints for uploading GeoTIFF and world-file rasters into PostGIS.
/// </summary>
internal static partial class RasterImportEndpoints
{
    private const string InvalidRasterImportRequestMessage = "Raster import request is invalid.";
    /// <summary>
    /// Maximum size for sidecar text files (world files, .prj).
    /// World files are ~200 bytes; .prj files are typically &lt; 5 KB.
    /// 64 KB is generous while preventing unbounded memory allocation.
    /// </summary>
    private const int MaxSidecarFileSizeBytes = 64 * 1024;

    /// <summary>Log category marker for raster import endpoint operations.</summary>
    internal sealed class RasterImportEndpointsLog
    {
    }

    /// <summary>
    /// Map raster import endpoints to the web application with formal API versioning.
    /// </summary>
    public static void MapRasterImportEndpoints(this WebApplication app)
    {
        var v1Group = app.MapGroup("/api/v{version:apiVersion}/admin/import/raster")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Raster Import")
            .RequireAdminAuthorization();

        _ = v1Group.MapPost("/", HandleImportRaster)
            .WithName("ImportRasterV1")
            .WithSummary("Import a raster file (GeoTIFF, PNG world-file, JPEG world-file) into PostGIS")
            .DisableAntiforgery();

        _ = v1Group.MapGet("/formats", HandleGetRasterFormats)
            .WithName("GetRasterFormatsV1")
            .WithSummary("Get supported raster file formats");
    }

    /// <summary>
    /// Get supported raster file formats and extensions.
    /// </summary>
    private static async Task HandleGetRasterFormats(HttpContext context)
    {
        var importService = context.RequestServices.GetRequiredService<IRasterImportService>();
        var extensions = importService.GetSupportedExtensions();
        var formatDescriptions = new Dictionary<string, string>
        {
            [".tif"] = "GeoTIFF - Georeferenced TIFF with embedded CRS and geotransform",
            [".tiff"] = "GeoTIFF - Georeferenced TIFF with embedded CRS and geotransform",
            [".png"] = "PNG with world file (.pgw) - Requires sidecar georeferencing",
            [".jpg"] = "JPEG with world file (.jgw) - Requires sidecar georeferencing",
            [".jpeg"] = "JPEG with world file (.jgw) - Requires sidecar georeferencing"
        };

        var response = new RasterFormatsResponse
        {
            SupportedExtensions = extensions,
            FormatDescriptions = formatDescriptions.Where(kv => extensions.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        var result = Results.Json(response, RasterImportJsonContext.Default.RasterFormatsResponse);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Import a raster file into PostGIS via multipart upload.
    /// </summary>
    private static async Task HandleImportRaster(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var importService = context.RequestServices.GetRequiredService<IRasterImportService>();
        // Raster imports run synchronously (no background job path yet), so enforce the
        // sync size limit.  When background raster jobs are added, switch to MaxImportSize
        // with queue-if-over-threshold logic matching the vector import path.
        var maxFileSizeBytes = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Imports.MaxSyncImportSize;

        // Raster files are validated via TIFF header magic-byte checks and size limits
        // rather than the generic FileUploadSecurity content scanner (which targets vector/text formats).
        var parseResult = await ParseRasterMultipartUploadAsync(
            context, importService, maxFileSizeBytes, cancellationToken);

        if (parseResult.ErrorMessage != null)
        {
            await AdminResponseWriter.WriteErrorAsync(
                context, parseResult.ErrorMessage, parseResult.StatusCode ?? StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            var request = parseResult.Request!;

            // Track progress through universal progress store
            var progressStore = context.RequestServices.GetService<IUniversalProgressStore>();
            IProgress<RasterImportProgress>? progressReporter = null;
            if (progressStore != null)
            {
                var progressLogger = context.RequestServices.GetRequiredService<ILogger<RasterImportEndpointsLog>>();
                progressReporter = new Progress<RasterImportProgress>(p => _ = ReportProgressAsync(p));

                async Task ReportProgressAsync(RasterImportProgress p)
                {
                    try
                    {
                        await progressStore.SetProgressAsync(p.OperationId, p, TimeSpan.FromHours(1), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.ProgressUpdateFailed(progressLogger, p.OperationId, ex);
                    }
                }
            }

            var result = await importService.ImportAsync(request, progressReporter, cancellationToken);

            if (result.Success)
            {
                var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
                if (cacheInvalidator != null)
                {
                    await cacheInvalidator.InvalidateLayerAsync(null, request.LayerId, cancellationToken);
                }

                var response = Results.Json(result, RasterImportJsonContext.Default.RasterImportResult);
                await response.ExecuteAsync(context);
            }
            else
            {
                await AdminResponseWriter.WriteErrorAsync(
                    context, result.ErrorMessage ?? "Raster import failed", StatusCodes.Status400BadRequest);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<RasterImportEndpointsLog>>();
            Log.ImportFailed(logger, parseResult.Request?.FileName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(
                context, InvalidRasterImportRequestMessage, StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<RasterImportEndpointsLog>>();
            Log.ImportFailed(logger, parseResult.Request?.FileName ?? "unknown", ex);
            await AdminResponseWriter.WriteErrorAsync(
                context, "Raster import failed", StatusCodes.Status500InternalServerError);
        }
        finally
        {
            MultipartParsingHelpers.TryDeleteFile(parseResult.Request?.FilePath);
        }
    }

    // =============================================================================
    // Multipart parsing
    // =============================================================================

    private static async Task<RasterImportParseResult> ParseRasterMultipartUploadAsync(
        HttpContext context,
        IRasterImportService importService,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType))
        {
            return RasterImportParseResult.Failure("Invalid multipart content type.", StatusCodes.Status400BadRequest);
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return RasterImportParseResult.Failure("Multipart request is missing a boundary.", StatusCodes.Status400BadRequest);
        }

        var reader = new MultipartReader(boundary, context.Request.Body);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? stagedFilePath = null;
        string? stagedFileName = null;
        string? worldFileContent = null;
        string? prjFileContent = null;

        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) != null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var contentDisposition))
                {
                    continue;
                }

                var fieldName = HeaderUtilities.RemoveQuotes(contentDisposition.Name).Value;

                if (MultipartParsingHelpers.HasFileContentDisposition(contentDisposition))
                {
                    var fileName = MultipartParsingHelpers.ResolveFileName(contentDisposition);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    var safeFileName = FileUploadSecurity.SanitizeFileName(fileName);
                    var extension = Path.GetExtension(safeFileName).ToLowerInvariant();

                    // Route sidecar files to string content (bounded read — these are tiny text files)
                    if (extension is ".pgw" or ".jgw" or ".tfw" or ".wld")
                    {
                        worldFileContent = await MultipartParsingHelpers.ReadBoundedSectionStringAsync(
                            section, MaxSidecarFileSizeBytes, cancellationToken);
                        continue;
                    }

                    if (extension == ".prj")
                    {
                        prjFileContent = await MultipartParsingHelpers.ReadBoundedSectionStringAsync(
                            section, MaxSidecarFileSizeBytes, cancellationToken);
                        continue;
                    }

                    // Primary raster file
                    if (stagedFilePath != null)
                    {
                        MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
                        return RasterImportParseResult.Failure("Only one primary raster file is supported per request.", StatusCodes.Status400BadRequest);
                    }

                    var format = importService.DetectFormat(safeFileName);
                    if (format == null)
                    {
                        return RasterImportParseResult.Failure(
                            $"Unsupported raster format: {extension}. Supported: .tif, .tiff, .png, .jpg, .jpeg",
                            StatusCodes.Status400BadRequest);
                    }

                    stagedFilePath = await StageRasterFileAsync(section.Body, safeFileName, maxFileSizeBytes, cancellationToken);
                    stagedFileName = safeFileName;
                    continue;
                }

                if (MultipartParsingHelpers.HasFormDataContentDisposition(contentDisposition))
                {
                    if (!string.IsNullOrWhiteSpace(fieldName))
                    {
                        fields[fieldName] = await MultipartParsingHelpers.ReadSectionStringAsync(section, cancellationToken);
                    }
                }
            }

            if (stagedFilePath == null || stagedFileName == null)
            {
                return RasterImportParseResult.Failure("Raster file is required.", StatusCodes.Status400BadRequest);
            }

            // Validate required fields
            if (!fields.TryGetValue("layerId", out var layerIdStr) || !int.TryParse(layerIdStr, out var layerId))
            {
                MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
                return RasterImportParseResult.Failure("'layerId' is required and must be a valid integer.", StatusCodes.Status400BadRequest);
            }

            if (!fields.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
                return RasterImportParseResult.Failure("'name' is required.", StatusCodes.Status400BadRequest);
            }

            var detectedFormat = importService.DetectFormat(stagedFileName)!.Value;
            int? srid = fields.TryGetValue("srid", out var sridStr) && int.TryParse(sridStr, out var parsedSrid)
                ? parsedSrid
                : null;
            DateTimeOffset? acquisitionDate = null;
            if (fields.TryGetValue("acquisitionDate", out var acquisitionDateRaw) &&
                !string.IsNullOrWhiteSpace(acquisitionDateRaw))
            {
                if (!DateTimeOffset.TryParse(acquisitionDateRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedAcquisitionDate))
                {
                    MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
                    return RasterImportParseResult.Failure("'acquisitionDate' must be a valid ISO 8601 timestamp.", StatusCodes.Status400BadRequest);
                }

                acquisitionDate = parsedAcquisitionDate;
            }

            int[] tileZoomLevels = [0, 1, 2, 3, 4, 5, 6, 7, 8];
            if (fields.TryGetValue("tileZoomLevels", out var zoomStr) && !string.IsNullOrWhiteSpace(zoomStr))
            {
                try
                {
                    tileZoomLevels = zoomStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(int.Parse)
                        .Distinct()
                        .Order()
                        .ToArray();
                }
                catch (FormatException)
                {
                    MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
                    return RasterImportParseResult.Failure("'tileZoomLevels' must be a comma-separated list of integers (0-24).", StatusCodes.Status400BadRequest);
                }

                if (tileZoomLevels.Any(z => z is < 0 or > 24))
                {
                    MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
                    return RasterImportParseResult.Failure("'tileZoomLevels' values must be between 0 and 24.", StatusCodes.Status400BadRequest);
                }
            }

            var request = new RasterImportRequest
            {
                LayerId = layerId,
                Name = name,
                Description = fields.TryGetValue("description", out var desc) ? desc : null,
                FilePath = stagedFilePath,
                FileName = stagedFileName,
                Format = detectedFormat,
                Srid = srid,
                AcquisitionDate = acquisitionDate,
                WorldFileContent = worldFileContent,
                PrjFileContent = prjFileContent,
                TileZoomLevels = tileZoomLevels
            };

            return RasterImportParseResult.Success(request);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
            return RasterImportParseResult.Failure(InvalidRasterImportRequestMessage, StatusCodes.Status400BadRequest);
        }
        catch
        {
            MultipartParsingHelpers.TryDeleteFile(stagedFilePath);
            throw;
        }
    }

    // =============================================================================
    // File staging helpers
    // =============================================================================

    private static async Task<string> StageRasterFileAsync(
        Stream sourceStream,
        string safeFileName,
        long maxFileSizeBytes,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "honua-raster-staging");
        var tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid()}{Path.GetExtension(safeFileName)}");

        try
        {
            var totalBytes = await MultipartParsingHelpers.CopyStreamToTempFileAsync(
                sourceStream, tempFilePath, maxFileSizeBytes, cancellationToken);

            if (totalBytes == 0)
            {
                MultipartParsingHelpers.TryDeleteFile(tempFilePath);
                throw new InvalidDataException("Raster file is empty.");
            }

            return tempFilePath;
        }
        catch
        {
            MultipartParsingHelpers.TryDeleteFile(tempFilePath);
            throw;
        }
    }


    // =============================================================================
    // Structured logging (source-generated)
    // =============================================================================

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8800,
            Level = LogLevel.Error,
            Message = "Raster import failed: file={FileName}")]
        public static partial void ImportFailed(ILogger logger, string fileName, Exception ex);

        [LoggerMessage(
            EventId = 8801,
            Level = LogLevel.Warning,
            Message = "Raster import progress update failed: operationId={OperationId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string operationId, Exception ex);
    }

    // =============================================================================
    // Response/result models
    // =============================================================================

    internal sealed record RasterFormatsResponse
    {
        /// <summary>
        /// Supported file extensions for raster import.
        /// </summary>
        public required string[] SupportedExtensions { get; init; }

        /// <summary>
        /// Format descriptions keyed by extension.
        /// </summary>
        public required Dictionary<string, string> FormatDescriptions { get; init; }
    }

    private sealed record RasterImportParseResult
    {
        public RasterImportRequest? Request { get; init; }
        public string? ErrorMessage { get; init; }
        public int? StatusCode { get; init; }

        public static RasterImportParseResult Success(RasterImportRequest request) => new() { Request = request };

        public static RasterImportParseResult Failure(string errorMessage, int statusCode) =>
            new() { ErrorMessage = errorMessage, StatusCode = statusCode };
    }
}
