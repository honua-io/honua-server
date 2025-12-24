// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Import;

/// <summary>
/// File import endpoints for uploading and processing geospatial files
/// </summary>
public static class ImportEndpoints
{
    /// <summary>
    /// Map file import endpoints to the web application
    /// </summary>
    public static void MapImportEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/import")
            .WithTags("Import");

        // Get supported file formats
        _ = group.Map("/formats", HandleGetSupportedFormats)
            .WithName("GetSupportedFileFormats")
            .WithSummary("Get supported geospatial file formats")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
        // .Produces<FileFormatsResponse>();

        // Preview file before import
        _ = group.Map("/preview", HandlePreviewFile)
            .WithName("PreviewFile")
            .WithSummary("Preview geospatial file contents")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            // .Produces<FilePreview>()
            .DisableAntiforgery(); // For file uploads

        // Import geospatial file
        _ = group.Map("/upload", HandleImportFile)
            .WithName("ImportFile")
            .WithSummary("Import geospatial file to PostgreSQL")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            // .Produces<ImportResult>()
            .DisableAntiforgery(); // For file uploads
    }

    /// <summary>
    /// Get supported file formats and extensions
    /// </summary>
    private static async Task HandleGetSupportedFormats(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        string[] extensions = importService.GetSupportedExtensions();
        var formatDescriptions = new Dictionary<string, string>
        {
            [".geojson"] = "GeoJSON - Web-standard JSON format",
            [".json"] = "JSON - May contain GeoJSON data",
            [".shp"] = "Shapefile - vector format (requires .shx, .dbf)",
            [".gpkg"] = "GeoPackage - OGC SQLite-based format",
            [".gpx"] = "GPX - GPS Exchange format",
            [".kml"] = "KML - Keyhole Markup Language (Google Earth)",
            [".kmz"] = "KMZ - Compressed KML format",
            [".gml"] = "GML - Geography Markup Language",
            [".wkt"] = "WKT - Well-Known Text format",
            [".twkb"] = "TinyWKB - Compact binary format"
        };

        var response = new FileFormatsResponse
        {
            SupportedExtensions = extensions,
            FormatDescriptions = formatDescriptions.Where(kv => extensions.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
        };

        IResult result = Results.Json(response, ImportJsonContext.Default.FileFormatsResponse);
        await result.ExecuteAsync(context);
    }

    /// <summary>
    /// Preview file contents without importing
    /// </summary>
    private static async Task HandlePreviewFile(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
        IFormFile? file = GetFormFile(form, "file", "File");

        if (file == null || file.Length == 0)
        {
            await WriteErrorAsync(context, "File is empty", StatusCodes.Status400BadRequest);
            return;
        }

        // Check file size (10MB limit for preview)
        const long maxPreviewSize = 10 * 1024 * 1024;
        if (file.Length > maxPreviewSize)
        {
            await WriteErrorAsync(context,
                $"File too large for preview. Maximum size: {maxPreviewSize / 1024 / 1024}MB",
                StatusCodes.Status400BadRequest);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        SupportedFileFormat? format = importService.DetectFormat(file.FileName);
        if (format == null)
        {
            await WriteErrorAsync(context, $"Unsupported file format: {Path.GetExtension(file.FileName)}",
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            using Stream stream = file.OpenReadStream();
            FilePreview preview = await importService.PreviewFileAsync(stream, file.FileName, cancellationToken);
            IResult result = Results.Json(preview, ImportJsonContext.Default.FilePreview);
            await result.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, $"Failed to preview file: {ex.Message}", StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Import geospatial file to PostgreSQL
    /// </summary>
    private static async Task HandleImportFile(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        CancellationToken cancellationToken = context.RequestAborted;
        IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);

        IFormFile? file = GetFormFile(form, "File", "file");
        if (file == null || file.Length == 0)
        {
            await WriteErrorAsync(context, "File is required", StatusCodes.Status400BadRequest);
            return;
        }

        string tableName = form["TableName"].ToString();
        if (string.IsNullOrWhiteSpace(tableName))
        {
            await WriteErrorAsync(context, "Table name is required", StatusCodes.Status400BadRequest);
            return;
        }

        // Validate table name (basic SQL injection prevention)
        if (!IsValidTableName(tableName))
        {
            await WriteErrorAsync(context, "Invalid table name. Use only letters, numbers, and underscores.",
                StatusCodes.Status400BadRequest);
            return;
        }

        IFileImportService importService = context.RequestServices.GetRequiredService<IFileImportService>();
        SupportedFileFormat? format = importService.DetectFormat(file.FileName);
        if (format == null)
        {
            await WriteErrorAsync(context, $"Unsupported file format: {Path.GetExtension(file.FileName)}",
                StatusCodes.Status400BadRequest);
            return;
        }

        try
        {
            using Stream stream = file.OpenReadStream();

            // Parse optional parameters
            int? sourceSrid = int.TryParse(form["SourceSrid"], out int src) ? src : (int?)null;
            int targetSrid = int.TryParse(form["TargetSrid"], out int tgt) ? tgt : 4326;
            bool overwriteExisting = bool.TryParse(form["OverwriteExisting"], out bool overwrite) && overwrite;

            var importRequest = new ImportRequest
            {
                FileStream = stream,
                FileName = file.FileName,
                TableName = tableName,
                SourceSrid = sourceSrid,
                TargetSrid = targetSrid,
                OverwriteExisting = overwriteExisting
            };

            ImportResult result = await importService.ImportFileAsync(importRequest, cancellationToken);
            IResult response = Results.Json(result, ImportJsonContext.Default.ImportResult);
            await response.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(context, $"Import failed: {ex.Message}", StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// Validate table name to prevent SQL injection
    /// </summary>
    private static bool IsValidTableName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName) || tableName.Length > 63) // PostgreSQL limit
            return false;

        // Allow letters, numbers, underscores; must start with letter or underscore
        return tableName.All(c => char.IsLetterOrDigit(c) || c == '_') &&
               (char.IsLetter(tableName[0]) || tableName[0] == '_');
    }

    private static IFormFile? GetFormFile(IFormCollection form, string primaryName, string fallbackName) => form.Files.GetFile(primaryName) ?? form.Files.GetFile(fallbackName);

    private static Task WriteErrorAsync(HttpContext context, string message, int statusCode)
    {
        var error = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = GeoServicesErrorCodes.FromHttpStatusCode(statusCode),
                Message = message
            }
        };
        IResult result = Results.Json(error, ImportJsonContext.Default.ApiErrorResponse, statusCode: statusCode);
        return result.ExecuteAsync(context);
    }
}

/// <summary>
/// Response containing supported file formats and their descriptions for the import API
/// </summary>
internal sealed record FileFormatsResponse
{
    public required string[] SupportedExtensions { get; init; }
    public required Dictionary<string, string> FormatDescriptions { get; init; }
}
