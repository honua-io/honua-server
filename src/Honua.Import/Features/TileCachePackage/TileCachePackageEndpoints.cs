// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.TileCachePackage.Abstractions;
using Honua.Core.Features.TileCachePackage.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Import.TileCachePackage;

/// <summary>
/// Admin import endpoint + public serving binding for Esri tile/vector-tile cache
/// packages (<c>.tpk</c>/<c>.tpkx</c>/<c>.vtpk</c>) (#1269).
///
/// The importer ingests a prebuilt package into Honua's tile catalog; the serving
/// binding lets the imported cache be served through Honua tile endpoints without
/// re-rendering. The package is uploaded as the raw request body (the tileset id and
/// optional zoom range are query parameters) so a single binary file streams straight
/// to a bounded temp file for random-access (ZIP) reading.
/// </summary>
internal static class TileCachePackageEndpoints
{
    // Hard cap on the uploaded package body. Tile packages can be large, but the
    // admin import path streams to a temp file; this bounds disk usage per request.
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

    /// <summary>
    /// Maps the tile-cache package import + serving endpoints.
    /// </summary>
    public static void MapTileCachePackageEndpoints(this WebApplication app)
    {
        var importGroup = app.MapGroup("/api/v{version:apiVersion}/admin/import/tile-package")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("TileCachePackageImport")
            .RequireAdminAuthorization();

        _ = importGroup.MapPost("/", HandleImport)
            .WithName("ImportTileCachePackage")
            .WithSummary("Import an Esri tile/vector-tile cache package (.tpk/.tpkx/.vtpk) into Honua's tile catalog and bind it to a served tileset")
            .DisableAntiforgery();

        // Public serving binding. Imported caches serve through these tile routes.
        var serveGroup = app.MapGroup("/tiles/imported")
            .WithTags("TileCachePackageServe");

        _ = serveGroup.MapGet("/{tileCacheId}", HandleGetTileSet)
            .WithName("GetImportedTileSet")
            .WithSummary("Get TileJSON-style metadata for an imported tile-cache package");

        _ = serveGroup.MapGet("/{tileCacheId}/{z:int}/{x:int}/{y:int}", HandleGetTile)
            .WithName("GetImportedTile")
            .WithSummary("Get a single tile served from an imported tile-cache package");
    }

    private static async Task<IResult> HandleImport(
        HttpContext context,
        string tilesetId,
        string? fileName = null,
        int? minZoom = null,
        int? maxZoom = null,
        bool dryRun = false)
    {
        var cancellationToken = context.RequestAborted;

        // Resolve the import service from the request scope rather than as a route-handler
        // parameter. The service is provider-specific (registered only when the active
        // data provider supplies it) so declaring it as a parameter would make the minimal
        // API request-delegate factory infer it as a JSON body parameter on hosts where it
        // is absent, which fails app startup when the source-generated JSON resolver cannot
        // produce metadata for the service interface.
        var importService = context.RequestServices.GetRequiredService<ITileCachePackageImportService>();

        if (string.IsNullOrWhiteSpace(tilesetId))
        {
            return Results.BadRequest("Query parameter 'tilesetId' is required.");
        }

        var resolvedFileName = string.IsNullOrWhiteSpace(fileName)
            ? DeriveFileNameFromContentType(context.Request.ContentType)
            : fileName;

        if (resolvedFileName is null)
        {
            return Results.BadRequest(
                "Unable to determine the package format. Provide a 'fileName' query parameter ending in .tpk, .tpkx, or .vtpk.");
        }

        // Stream the request body to a bounded temp file so the ZIP archive can be
        // read with random access without buffering the whole package in memory.
        // The second segment is a server-generated "honua-tpk-{guid}.tmp" name (not
        // derived from the caller-supplied fileName), so it can never be rooted/absolute.
        var tempPath = Path.Join(Path.GetTempPath(), $"honua-tpk-{Guid.NewGuid():N}.tmp");
        try
        {
            long written;
            await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                written = await CopyBoundedAsync(context.Request.Body, temp, MaxPackageBytes, cancellationToken).ConfigureAwait(false);
            }

            if (written == 0)
            {
                return Results.BadRequest("Request body is empty; upload the package bytes as the request body.");
            }

            if (written >= MaxPackageBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await using var package = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.SequentialScan);

            TileCachePackageImportResult result;
            try
            {
                result = await importService.ImportAsync(
                    new TileCachePackageImportRequest
                    {
                        Package = package,
                        FileName = resolvedFileName,
                        TilesetId = tilesetId,
                        MinZoom = minZoom,
                        MaxZoom = maxZoom,
                        DryRun = dryRun
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest($"The uploaded file is not a readable tile-cache package: {ex.Message}");
            }

            return Results.Json(ToApiResult(result), TileCachePackageJsonContext.Default.TileCachePackageImportApiResult);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task<IResult> HandleGetTileSet(
        string tileCacheId,
        HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var reader = context.RequestServices.GetRequiredService<IImportedTileCacheReader>();
        var info = await reader.GetTileCacheAsync(tileCacheId, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            return Results.NotFound();
        }

        var scheme = context.Request.Scheme;
        var host = context.Request.Host.Value;
        var tileTemplate = $"{scheme}://{host}/tiles/imported/{info.TileCacheId}/{{z}}/{{x}}/{{y}}";

        var json = new TileCachePackageTileSetJson
        {
            TileJson = "3.0.0",
            Name = info.Title ?? info.LayerIdentifier,
            DataType = info.DataType,
            TileMatrixSet = info.TileMatrixSet,
            Format = info.TileFormat,
            MinZoom = info.MinZoom,
            MaxZoom = info.MaxZoom,
            Tiles = [tileTemplate]
        };

        return Results.Json(json, TileCachePackageJsonContext.Default.TileCachePackageTileSetJson);
    }

    private static async Task<IResult> HandleGetTile(
        string tileCacheId,
        int z,
        int x,
        int y,
        HttpContext context)
    {
        var cancellationToken = context.RequestAborted;

        if (z < 0 || x < 0 || y < 0)
        {
            return Results.BadRequest("Tile coordinates must be non-negative.");
        }

        var reader = context.RequestServices.GetRequiredService<IImportedTileCacheReader>();
        var tile = await reader.GetTileAsync(tileCacheId, z, x, y, cancellationToken).ConfigureAwait(false);
        if (tile is null)
        {
            return Results.NotFound();
        }

        return Results.Bytes(tile.Content, tile.ContentType);
    }

    private static string? DeriveFileNameFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        // Vendor content types occasionally carry the format; otherwise we cannot
        // infer the extension and require the caller to pass fileName.
        if (contentType.Contains("vtpk", StringComparison.OrdinalIgnoreCase))
        {
            return "package.vtpk";
        }

        if (contentType.Contains("tpkx", StringComparison.OrdinalIgnoreCase))
        {
            return "package.tpkx";
        }

        if (contentType.Contains("tpk", StringComparison.OrdinalIgnoreCase))
        {
            return "package.tpk";
        }

        return null;
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return limit;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp sweeper reclaims any residue.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    private static TileCachePackageImportApiResult ToApiResult(TileCachePackageImportResult result) => new()
    {
        Success = result.Success,
        TileCacheId = result.TileCacheId,
        StorageFormat = result.StorageFormat,
        DataType = result.DataType,
        ContentType = result.ContentType,
        TileMatrixSet = result.TileMatrixSet,
        MinZoom = result.MinZoom,
        MaxZoom = result.MaxZoom,
        TilesImported = result.TilesImported,
        TilesSkipped = result.TilesSkipped,
        DryRun = result.DryRun,
        ServeUrl = result.TileCacheId is null ? null : $"/tiles/imported/{result.TileCacheId}"
    };
}

/// <summary>
/// API result for the tile-cache package import endpoint.
/// </summary>
internal sealed record TileCachePackageImportApiResult
{
    /// <summary>Whether the import succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Stable tile-cache identifier the tiles were written to.</summary>
    public string? TileCacheId { get; init; }

    /// <summary>Detected storage format.</summary>
    public required string StorageFormat { get; init; }

    /// <summary>Detected payload kind.</summary>
    public required string DataType { get; init; }

    /// <summary>Served tile content type.</summary>
    public required string ContentType { get; init; }

    /// <summary>Resolved Honua tile-matrix-set identifier.</summary>
    public required string TileMatrixSet { get; init; }

    /// <summary>Resolved minimum zoom imported.</summary>
    public required int MinZoom { get; init; }

    /// <summary>Resolved maximum zoom imported.</summary>
    public required int MaxZoom { get; init; }

    /// <summary>Number of tiles inserted.</summary>
    public required int TilesImported { get; init; }

    /// <summary>Number of tiles skipped (already present).</summary>
    public required int TilesSkipped { get; init; }

    /// <summary>Whether this was a dry-run.</summary>
    public required bool DryRun { get; init; }

    /// <summary>Relative URL the imported tileset is served from.</summary>
    public string? ServeUrl { get; init; }
}

/// <summary>
/// TileJSON-style descriptor for an imported served tileset.
/// </summary>
internal sealed record TileCachePackageTileSetJson
{
    /// <summary>TileJSON version.</summary>
    [JsonPropertyName("tilejson")]
    public required string TileJson { get; init; }

    /// <summary>Tileset name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Payload kind (raster/vector).</summary>
    [JsonPropertyName("dataType")]
    public required string DataType { get; init; }

    /// <summary>Honua tile-matrix-set identifier.</summary>
    [JsonPropertyName("tileMatrixSet")]
    public required string TileMatrixSet { get; init; }

    /// <summary>Tile content type.</summary>
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    /// <summary>Minimum zoom.</summary>
    [JsonPropertyName("minzoom")]
    public required int MinZoom { get; init; }

    /// <summary>Maximum zoom.</summary>
    [JsonPropertyName("maxzoom")]
    public required int MaxZoom { get; init; }

    /// <summary>Tile URL templates.</summary>
    [JsonPropertyName("tiles")]
    public required string[] Tiles { get; init; }
}

/// <summary>
/// Source-generated JSON context for the tile-cache package endpoints.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TileCachePackageImportApiResult))]
[JsonSerializable(typeof(TileCachePackageTileSetJson))]
internal sealed partial class TileCachePackageJsonContext : JsonSerializerContext
{
}
