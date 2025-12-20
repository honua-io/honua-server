// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.AspNetCore.Mvc;

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
        var group = app.MapGroup("/api/import")
            .WithTags("Import");

        // Get supported file formats
        group.MapGet("/formats", GetSupportedFormats)
            .WithName("GetSupportedFileFormats")
            .WithSummary("Get supported geospatial file formats")
            .Produces<FileFormatsResponse>();

        // Preview file before import
        group.MapPost("/preview", PreviewFile)
            .WithName("PreviewFile")
            .WithSummary("Preview geospatial file contents")
            .Produces<FilePreview>()
            .Produces<ProblemDetails>(400)
            .DisableAntiforgery(); // For file uploads

        // Import geospatial file
        group.MapPost("/upload", ImportFile)
            .WithName("ImportFile")
            .WithSummary("Import geospatial file to PostgreSQL")
            .Produces<ImportResult>()
            .Produces<ProblemDetails>(400)
            .DisableAntiforgery(); // For file uploads
    }

    /// <summary>
    /// Get supported file formats and extensions
    /// </summary>
    private static IResult GetSupportedFormats(IFileImportService importService)
    {
        var extensions = importService.GetSupportedExtensions();
        var formatDescriptions = new Dictionary<string, string>
        {
            [".geojson"] = "GeoJSON - Web-standard JSON format",
            [".json"] = "JSON - May contain GeoJSON data",
            [".shp"] = "Shapefile - Esri vector format (requires .shx, .dbf)",
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

        return Results.Ok(response);
    }

    /// <summary>
    /// Preview file contents without importing
    /// </summary>
    private static async Task<IResult> PreviewFile(
        IFormFile file,
        IFileImportService importService,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return Results.BadRequest("File is empty");
        }

        // Check file size (10MB limit for preview)
        const long maxPreviewSize = 10 * 1024 * 1024;
        if (file.Length > maxPreviewSize)
        {
            return Results.BadRequest($"File too large for preview. Maximum size: {maxPreviewSize / 1024 / 1024}MB");
        }

        var format = importService.DetectFormat(file.FileName);
        if (format == null)
        {
            return Results.BadRequest($"Unsupported file format: {Path.GetExtension(file.FileName)}");
        }

        try
        {
            using var stream = file.OpenReadStream();
            var preview = await importService.PreviewFileAsync(stream, file.FileName, cancellationToken);
            return Results.Ok(preview);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Failed to preview file: {ex.Message}");
        }
    }

    /// <summary>
    /// Import geospatial file to PostgreSQL
    /// </summary>
    private static async Task<IResult> ImportFile(
        HttpContext context,
        IFileImportService importService,
        CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);

        var file = form.Files["File"];
        if (file?.Length == 0 || file == null)
        {
            return Results.BadRequest("File is required");
        }

        var tableName = form["TableName"].ToString();
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return Results.BadRequest("Table name is required");
        }

        // Validate table name (basic SQL injection prevention)
        if (!IsValidTableName(tableName))
        {
            return Results.BadRequest("Invalid table name. Use only letters, numbers, and underscores.");
        }

        var format = importService.DetectFormat(file.FileName);
        if (format == null)
        {
            return Results.BadRequest($"Unsupported file format: {Path.GetExtension(file.FileName)}");
        }

        try
        {
            using var stream = file.OpenReadStream();

            // Parse optional parameters
            var sourceSrid = int.TryParse(form["SourceSrid"], out var src) ? src : (int?)null;
            var targetSrid = int.TryParse(form["TargetSrid"], out var tgt) ? tgt : 4326;
            var overwriteExisting = bool.TryParse(form["OverwriteExisting"], out var overwrite) && overwrite;

            var importRequest = new ImportRequest
            {
                FileStream = stream,
                FileName = file.FileName,
                TableName = tableName,
                SourceSrid = sourceSrid,
                TargetSrid = targetSrid,
                OverwriteExisting = overwriteExisting
            };

            var result = await importService.ImportFileAsync(importRequest, cancellationToken);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Import failed: {ex.Message}");
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
}

/// <summary>
/// Response containing supported file formats and their descriptions for the import API
/// </summary>
internal sealed record FileFormatsResponse
{
    public required string[] SupportedExtensions { get; init; }
    public required Dictionary<string, string> FormatDescriptions { get; init; }
}

