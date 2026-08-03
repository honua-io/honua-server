// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// PostgreSQL-based raster import service using PostGIS ST_FromGDALRaster.
/// Handles file loading, statistics computation, and tile pre-generation.
/// </summary>
internal sealed class PostgresRasterImportService : IRasterImportService
{
    // GeoTIFF magic bytes: "II" (little-endian) or "MM" (big-endian) followed by 42
    private static readonly byte[] _tiffLittleEndian = [0x49, 0x49, 0x2A, 0x00];
    private static readonly byte[] _tiffBigEndian = [0x4D, 0x4D, 0x00, 0x2A];
    // BigTIFF variants
    private static readonly byte[] _bigTiffLittleEndian = [0x49, 0x49, 0x2B, 0x00];
    private static readonly byte[] _bigTiffBigEndian = [0x4D, 0x4D, 0x00, 0x2B];
    // PNG signature: 8-byte header
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    // JPEG SOI marker: 0xFF 0xD8
    private static readonly byte[] _jpegSoiMarker = [0xFF, 0xD8];
    // Stable namespace for PostgreSQL two-key advisory locks used by raster imports.
    private const int RasterImportLayerLockNamespace = 0x0484_5221;

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly ICrsDetectionService _crsDetectionService;
    private readonly ILogger<PostgresRasterImportService> _logger;
    private readonly string _rasterDataTable;
    private readonly string _rasterStatisticsTable;
    private readonly string _rasterTilesTable;
    private readonly string _rasterOverviewsTable;
    private readonly string _rasterFootprintsTable;

    public PostgresRasterImportService(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ICrsDetectionService crsDetectionService,
        ILogger<PostgresRasterImportService> logger,
        string? schemaName = null)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _crsDetectionService = crsDetectionService ?? throw new ArgumentNullException(nameof(crsDetectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _rasterDataTable = SchemaSearchPath.QualifyTable("raster_data", schemaName);
        _rasterStatisticsTable = SchemaSearchPath.QualifyTable("raster_statistics", schemaName);
        _rasterTilesTable = SchemaSearchPath.QualifyTable("raster_tiles", schemaName);
        _rasterOverviewsTable = SchemaSearchPath.QualifyTable("raster_overviews", schemaName);
        _rasterFootprintsTable = SchemaSearchPath.QualifyTable("raster_footprints", schemaName);
    }

    /// <inheritdoc />
    public SupportedRasterFormat? DetectFormat(string fileName) =>
        RasterImportFormatDetector.DetectFormat(fileName);

    /// <inheritdoc />
    public string[] GetSupportedExtensions() => [.. RasterImportFormatDetector.SupportedExtensions];

    /// <inheritdoc />
    public async Task<RasterImportResult> ImportAsync(
        RasterImportRequest request,
        IProgress<RasterImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        ReportProgress(progress, operationId, startedAt, RasterImportPhase.Validating, OperationStatus.Processing, "Validating raster file");
        PostgresRasterImportLog.ImportStarted(_logger, request.LayerId, request.FileName, request.Format);

        try
        {
            // Phase 1: Validate the file
            ValidateRequest(request);
            var fileBytes = await ReadFileBytesAsync(request.FilePath, cancellationToken).ConfigureAwait(false);
            ValidateFileContent(fileBytes, request.Format);

            // Phase 2: Ingest into raster_data
            ReportProgress(progress, operationId, startedAt, RasterImportPhase.Ingesting, OperationStatus.Processing, "Loading raster into PostGIS");

            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            long rasterId;
            try
            {
                await AcquireLayerImportLockAsync(
                    connection, transaction, request.LayerId, cancellationToken).ConfigureAwait(false);

                // Guarantee the monolithic raster payload is stored EXTERNAL (out-of-line,
                // uncompressed) before the row is written, so dynamic tile/terrain/statistics
                // reads fetch only the chunks they touch instead of detoasting + decompressing
                // the entire 25-115 MB raster on every request (#1625). SET STORAGE only affects
                // rows written afterwards, so applying it here (immediately before INSERT) ensures
                // every imported raster lands EXTERNAL even on databases that predate migration 055.
                await EnsureExternalRasterStorageAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);

                rasterId = await InsertRasterDataAsync(
                    connection, transaction, request, fileBytes, cancellationToken).ConfigureAwait(false);

                // Read back raster metadata for the result
                var (width, height, bandCount, srid) = await ReadRasterMetadataAsync(
                    connection, transaction, rasterId, cancellationToken).ConfigureAwait(false);

                // Handle world file + SRID for non-GeoTIFF formats
                if (request.Format is not SupportedRasterFormat.GeoTiff and not SupportedRasterFormat.CloudOptimizedGeoTiff)
                {
                    await ApplyWorldFileGeoReferencingAsync(
                        connection, transaction, rasterId, request, warnings, cancellationToken).ConfigureAwait(false);

                    // Re-read metadata after georeferencing update
                    (width, height, bandCount, srid) = await ReadRasterMetadataAsync(
                        connection, transaction, rasterId, cancellationToken).ConfigureAwait(false);
                }

                // Validate SRID was resolved
                if (srid == 0 && request.Srid == null)
                {
                    warnings.Add("No CRS detected from file. SRID defaults to 0 (unknown). Specify 'srid' to set explicitly.");
                }

                PostgresRasterImportLog.RasterIngested(_logger, rasterId, width, height, bandCount, srid);

                // Per-layer SRID and band-count homogeneity guard. ST_Union (used by mosaic
                // export/identify/tile/statistics paths) requires all rasters in a layer to share
                // the same SRID and band count; otherwise PostGIS aborts with an opaque error at
                // query time. Reject the upload before commit so callers see a structured 400.
                // Inner catch block handles the rollback; do not roll back here.
                var homogeneityError = await CheckLayerHomogeneityAsync(
                    connection, transaction, request.LayerId, rasterId, srid, bandCount, cancellationToken).ConfigureAwait(false);
                if (homogeneityError != null)
                {
                    throw new ArgumentException(homogeneityError);
                }

                // Phase 3: Compute band statistics
                ReportProgress(progress, operationId, startedAt, RasterImportPhase.ComputingStatistics, OperationStatus.Processing,
                    "Computing band statistics", rasterId: rasterId, totalBands: bandCount);

                var statsBands = await ComputeAndStoreStatisticsAsync(
                    connection, transaction, rasterId, bandCount, progress, operationId, startedAt, cancellationToken).ConfigureAwait(false);

                PostgresRasterImportLog.StatisticsComputed(_logger, rasterId, statsBands);

                // Phase 4: Pre-generate tiles (requires a known CRS for reprojection to Web Mercator)
                var tilesGenerated = 0;
                if (request.TileZoomLevels.Length > 0 && srid > 0)
                {
                    ReportProgress(progress, operationId, startedAt, RasterImportPhase.GeneratingTiles, OperationStatus.Processing,
                        "Pre-generating tiles", rasterId: rasterId);

                    tilesGenerated = await GenerateAndStoreTilesAsync(
                        connection, transaction, rasterId, request.TileZoomLevels,
                        progress, operationId, startedAt, cancellationToken).ConfigureAwait(false);

                    PostgresRasterImportLog.TilesPreGenerated(_logger, rasterId, tilesGenerated, request.TileZoomLevels.Length);
                }
                else if (request.TileZoomLevels.Length > 0 && srid <= 0)
                {
                    warnings.Add("Tile pre-generation skipped: raster has no known CRS (SRID 0). Set 'srid' explicitly to enable tiling.");
                }

                // Phase 5: Build persisted overview pyramids (reduced-resolution copies in
                // EPSG:3857) so the dynamic tile read path reuses a pyramid level instead of
                // recomputing an ST_Rescale reduction per low-zoom request (#1836). Best-effort:
                // a missing raster_overviews table (schema predates migration 061) is tolerated.
                if (request.OverviewFactors.Length > 0 && srid > 0)
                {
                    await GenerateAndStoreOverviewsAsync(
                        connection, transaction, rasterId, request.OverviewFactors, warnings, cancellationToken).ConfigureAwait(false);
                }
                else if (request.OverviewFactors.Length > 0 && srid <= 0)
                {
                    warnings.Add("Overview pyramid build skipped: raster has no known CRS (SRID 0). Set 'srid' explicitly to enable overviews.");
                }

                // Phase 6: Persist the per-raster footprint (and a default seamline equal to the
                // footprint) so esriMosaicSeamline can clip each raster to its cutline before the
                // union (#1804). Best-effort: a missing raster_footprints table (schema predates
                // migration 062) is tolerated; seamline mosaics then fall back to the footprint.
                if (srid > 0)
                {
                    await GenerateAndStoreFootprintAsync(
                        connection, transaction, rasterId, srid, warnings, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                var completedAt = DateTimeOffset.UtcNow;
                ReportProgress(progress, operationId, startedAt, RasterImportPhase.Completed, OperationStatus.Completed,
                    "Import completed", rasterId: rasterId, percentComplete: 100, completedAt: completedAt,
                    warnings: warnings);

                PostgresRasterImportLog.ImportCompleted(_logger, rasterId, request.LayerId, stopwatch.Elapsed.TotalSeconds);

                return RasterImportResult.CreateSuccess(
                    rasterId, request.LayerId, request.Name, request.Format,
                    srid == 0 ? null : srid, width, height, bandCount,
                    statsBands, tilesGenerated, stopwatch.Elapsed, warnings);
            }
            catch (PostgresException ex) when (ex.SqlState == "23503")
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw new ArgumentException($"Layer {request.LayerId} does not exist.", ex);
            }
            catch (PostgresException ex) when (IsRasterDataError(ex))
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw new InvalidDataException(
                    "PostGIS could not process the raster file.", ex);
            }
            catch
            {
                await TryRollbackAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReportProgress(progress, operationId, startedAt, RasterImportPhase.Failed, OperationStatus.Cancelled,
                "Import cancelled", completedAt: DateTimeOffset.UtcNow);
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException
            or NotSupportedException or FileNotFoundException)
        {
            // Validation / user-input errors â€” return a failure result so the endpoint can map to 400.
            stopwatch.Stop();
            PostgresRasterImportLog.ImportFailed(_logger, ex, request.LayerId, request.FileName);

            ReportProgress(progress, operationId, startedAt, RasterImportPhase.Failed, OperationStatus.Failed,
                "Import failed.", completedAt: DateTimeOffset.UtcNow,
                errorMessage: "Import failed.", warnings: warnings);

            return RasterImportResult.CreateFailure(
                request.LayerId, request.Name, request.Format,
                "Import failed.", stopwatch.Elapsed, warnings);
        }
        catch (Exception ex)
        {
            // Infrastructure / unexpected errors â€” log, update progress, and rethrow so the
            // endpoint returns 500 with a generic message (no internal details leaked).
            stopwatch.Stop();
            PostgresRasterImportLog.ImportFailed(_logger, ex, request.LayerId, request.FileName);

            ReportProgress(progress, operationId, startedAt, RasterImportPhase.Failed, OperationStatus.Failed,
                "Import failed due to an internal error", completedAt: DateTimeOffset.UtcNow,
                errorMessage: "Internal server error", warnings: warnings);

            throw;
        }
    }

    // =============================================================================
    // Validation
    // =============================================================================

    private static void ValidateRequest(RasterImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new ArgumentException("File path is required.");
        }

        if (!File.Exists(request.FilePath))
        {
            throw new FileNotFoundException("Staged raster file not found.", request.FilePath);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Raster name is required.");
        }

        // World-file formats always require a world file for the geotransform.
        // An explicit SRID is supplemental (overrides .prj detection) but cannot
        // replace the world file â€” without it pixels map 1:1 to CRS units which is
        // almost certainly wrong.
        if (request.Format is SupportedRasterFormat.PngWorldFile or SupportedRasterFormat.JpegWorldFile &&
            string.IsNullOrWhiteSpace(request.WorldFileContent))
        {
            throw new ArgumentException(
                $"{request.Format} imports require a world file (.pgw/.jgw/.tfw) for georeferencing. " +
                "An explicit SRID can optionally accompany the world file to override CRS detection.");
        }

        // COG is a valid TIFF â€” same import path as GeoTIFF.
        // PostGIS ST_FromGDALRaster handles COG files correctly (reads base resolution).
        // COG internal overviews are not preserved as separate entities in PostGIS;
        // tile pre-generation at configured zoom levels provides the overview mechanism.
    }

    private static void ValidateFileContent(byte[] fileBytes, SupportedRasterFormat format)
    {
        if (fileBytes.Length == 0)
        {
            throw new InvalidDataException("Raster file is empty.");
        }

        switch (format)
        {
            case SupportedRasterFormat.GeoTiff:
            case SupportedRasterFormat.CloudOptimizedGeoTiff:
                if (fileBytes.Length < 4)
                {
                    throw new InvalidDataException("File is too small to be a valid GeoTIFF.");
                }

                var tiffHeader = fileBytes.AsSpan(0, 4);
                if (!tiffHeader.SequenceEqual(_tiffLittleEndian) &&
                    !tiffHeader.SequenceEqual(_tiffBigEndian) &&
                    !tiffHeader.SequenceEqual(_bigTiffLittleEndian) &&
                    !tiffHeader.SequenceEqual(_bigTiffBigEndian))
                {
                    throw new InvalidDataException("File does not have a valid TIFF header. Expected GeoTIFF format.");
                }

                break;

            case SupportedRasterFormat.PngWorldFile:
                if (fileBytes.Length < 8)
                {
                    throw new InvalidDataException("File is too small to be a valid PNG.");
                }

                if (!fileBytes.AsSpan(0, 8).SequenceEqual(_pngSignature))
                {
                    throw new InvalidDataException("File does not have a valid PNG header.");
                }

                break;

            case SupportedRasterFormat.JpegWorldFile:
                if (fileBytes.Length < 2)
                {
                    throw new InvalidDataException("File is too small to be a valid JPEG.");
                }

                if (!fileBytes.AsSpan(0, 2).SequenceEqual(_jpegSoiMarker))
                {
                    throw new InvalidDataException("File does not have a valid JPEG header.");
                }

                break;
        }
    }

    /// <summary>
    /// Detects whether a <see cref="PostgresException"/> originated from PostGIS/GDAL
    /// raster processing (corrupt file, unsupported driver, etc.) rather than a true
    /// infrastructure failure. Matched errors are surfaced as user-input validation
    /// failures (400) instead of internal server errors (500).
    /// </summary>
    private static bool IsRasterDataError(PostgresException ex)
        => ex.SqlState == "XX000" &&
           (ex.MessageText.Contains("rt_raster", StringComparison.OrdinalIgnoreCase) ||
            ex.MessageText.Contains("ST_FromGDALRaster", StringComparison.OrdinalIgnoreCase) ||
            ex.MessageText.Contains("GDAL", StringComparison.OrdinalIgnoreCase));

    // =============================================================================
    // Data operations
    // =============================================================================

    // NOTE: ST_FromGDALRaster requires the complete raster byte payload as a single parameter;
    // there is no streaming overload. For the configured max of 500 MB this allocates on the
    // Large Object Heap and roughly doubles peak memory (byte[] + Npgsql parameter copy).
    // The upload staging path streams to disk (CopyStreamToTempFileAsync), so only the DB
    // insertion step carries this cost. If memory pressure becomes an issue, consider lowering
    // MaxRasterFileSizeBytes or switching to raster2pgsql / lo_import in a future pass.
    private static async Task<byte[]> ReadFileBytesAsync(string filePath, CancellationToken cancellationToken)
    {
        return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> InsertRasterDataAsync(
        DbConnection connection,
        DbTransaction transaction,
        RasterImportRequest request,
        byte[] fileBytes,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        // Use ST_FromGDALRaster to load binary raster data directly
        // For GeoTIFF, georeferencing is embedded in the file
        // For PNG/JPEG, we apply georeferencing separately via world file
        if (request.Srid.HasValue && request.Format is SupportedRasterFormat.GeoTiff or SupportedRasterFormat.CloudOptimizedGeoTiff)
        {
            command.CommandText = $"""
                INSERT INTO {_rasterDataTable} (layer_id, name, description, acquisition_date, raster)
                VALUES (@layerId, @name, @description, @acquisitionDate,
                    ST_SetSRID(ST_FromGDALRaster(@rasterData), @srid))
                RETURNING id
                """;
            command.AddParameter("@srid", request.Srid.Value);
        }
        else
        {
            command.CommandText = $"""
                INSERT INTO {_rasterDataTable} (layer_id, name, description, acquisition_date, raster)
                VALUES (@layerId, @name, @description, @acquisitionDate, ST_FromGDALRaster(@rasterData))
                RETURNING id
                """;
        }

        command.AddParameter("@layerId", request.LayerId);
        command.AddParameter("@name", request.Name);
        command.AddParameter("@description", (object?)request.Description ?? DBNull.Value);
        command.AddParameter("@acquisitionDate", (object?)request.AcquisitionDate ?? DBNull.Value);
        command.AddParameter("@rasterData", fileBytes);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (long)result!;
    }

    private static async Task TryRollbackAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Npgsql can dispose the transaction while surfacing the original
            // PostGIS/GDAL exception. Do not mask the user-input failure.
        }
        catch (InvalidOperationException)
        {
            // The provider may already have aborted the transaction after a
            // server-side raster error. Preserve the original failure path.
        }
    }

    // Identifier used for the best-effort-phase savepoints. Constant (only one best-effort
    // phase is in flight at a time) so the SET/RELEASE/ROLLBACK TO names always match.
    private const string BestEffortSavepointName = "honua_raster_best_effort";

    /// <summary>
    /// Runs a best-effort enrichment phase inside a SQL savepoint so that ANY failure (a missing
    /// table on a schema that predates the migration, or a PostGIS/GDAL function quirk on a
    /// particular server build) rolls back to the savepoint and leaves the surrounding import
    /// transaction usable, downgrading to a warning instead of aborting the import.
    /// </summary>
    /// <remarks>
    /// Without the savepoint, a failed statement in one of these phases aborts the whole
    /// transaction; the catch handler swallows the original error but every subsequent
    /// statement then fails with <c>25P02 (in_failed_sql_transaction)</c>, breaking an import
    /// that does not even depend on the enrichment having succeeded (honua-server#1804/#1836).
    /// </remarks>
    private static async Task RunBestEffortPhaseAsync(
        DbConnection connection,
        DbTransaction transaction,
        Func<Task> work,
        Func<PostgresException, string> warningFactory,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await ExecuteSavepointCommandAsync(
            connection, transaction, $"SAVEPOINT {BestEffortSavepointName}", cancellationToken).ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
            await ExecuteSavepointCommandAsync(
                connection, transaction, $"RELEASE SAVEPOINT {BestEffortSavepointName}", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex)
        {
            // Roll the failed statement back to the savepoint so the import transaction is
            // usable again (clearing the 25P02 abort state), then downgrade to a warning.
            await ExecuteSavepointCommandAsync(
                connection, transaction, $"ROLLBACK TO SAVEPOINT {BestEffortSavepointName}", cancellationToken).ConfigureAwait(false);
            await ExecuteSavepointCommandAsync(
                connection, transaction, $"RELEASE SAVEPOINT {BestEffortSavepointName}", cancellationToken).ConfigureAwait(false);
            warnings.Add(warningFactory(ex));
        }
    }

    private static async Task ExecuteSavepointCommandAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches the <c>raster_data.raster</c> column to EXTERNAL TOAST storage so newly inserted
    /// rasters are stored out-of-line and uncompressed. This keeps the logical-raster identity
    /// intact (one row per <c>raster_data.id</c>, unchanged generated columns) while making
    /// ST_Clip / ST_Value / ST_SummaryStats reads fetch only the chunks they touch instead of
    /// inflating the whole monolithic raster (#1625). Idempotent: PostgreSQL no-ops when the
    /// column is already EXTERNAL.
    /// </summary>
    private async Task EnsureExternalRasterStorageAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {_rasterDataTable} ALTER COLUMN raster SET STORAGE EXTERNAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AcquireLayerImportLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        int layerId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(@namespace, @layerId);";
        command.AddParameter("@namespace", RasterImportLayerLockNamespace);
        command.AddParameter("@layerId", layerId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Width, int Height, int BandCount, int Srid)> ReadRasterMetadataAsync(
        DbConnection connection,
        DbTransaction transaction,
        long rasterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT width, height, band_count, srid
            FROM {_rasterDataTable}
            WHERE id = @rasterId
            """;
        command.AddParameter("@rasterId", rasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Raster {rasterId} not found after insert.");
        }

        return (
            reader.GetInt32(reader.GetOrdinal("width")),
            reader.GetInt32(reader.GetOrdinal("height")),
            reader.GetInt32(reader.GetOrdinal("band_count")),
            reader.GetInt32(reader.GetOrdinal("srid")));
    }

    /// <summary>
    /// Verifies the newly inserted raster shares the layer's canonical SRID and band count.
    /// Returns null on success or a client-safe error message describing the mismatch.
    /// First raster in a layer always passes (it sets the canonical values). Layers that
    /// already contain heterogeneous data (e.g. seeded outside the import path) are also
    /// rejected so callers see the real invariant violation rather than an opaque ST_Union
    /// failure at query time.
    /// </summary>
    private async Task<string?> CheckLayerHomogeneityAsync(
        DbConnection connection,
        DbTransaction transaction,
        int layerId,
        long newRasterId,
        int newSrid,
        int newBandCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*) AS existing_count,
                   MIN(srid) AS min_srid,
                   MAX(srid) AS max_srid,
                   MIN(band_count) AS min_bands,
                   MAX(band_count) AS max_bands
            FROM {_rasterDataTable}
            WHERE layer_id = @layerId AND id <> @newRasterId
            """;
        command.AddParameter("@layerId", layerId);
        command.AddParameter("@newRasterId", newRasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var existingCount = reader.GetInt64(0);
        if (existingCount == 0)
        {
            return null;
        }

        var minSrid = reader.GetInt32(1);
        var maxSrid = reader.GetInt32(2);
        var minBands = reader.GetInt32(3);
        var maxBands = reader.GetInt32(4);

        if (minSrid != maxSrid || minBands != maxBands)
        {
            PostgresRasterImportLog.HomogeneityRejected(_logger, layerId, newSrid, newBandCount, minSrid, minBands);
            return $"Layer {layerId} already contains heterogeneous rasters " +
                   $"(SRID range {minSrid}..{maxSrid}, BandCount range {minBands}..{maxBands}). " +
                   "Mosaic compositing requires homogeneity; reload the layer with consistent inputs.";
        }

        if (minSrid == newSrid && minBands == newBandCount)
        {
            return null;
        }

        PostgresRasterImportLog.HomogeneityRejected(_logger, layerId, newSrid, newBandCount, minSrid, minBands);
        return $"Layer {layerId} requires raster homogeneity for mosaic compositing. " +
               $"Expected SRID={minSrid}, BandCount={minBands}; " +
               $"upload has SRID={newSrid}, BandCount={newBandCount}.";
    }

    private async Task ApplyWorldFileGeoReferencingAsync(
        DbConnection connection,
        DbTransaction transaction,
        long rasterId,
        RasterImportRequest request,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.WorldFileContent))
        {
            // Parse world file: 6 lines mapping to GDAL geotransform indices.
            // Variable names follow PostGIS convention (ST_SkewX = GT[2], ST_SkewY = GT[4]).
            // Line 1: pixel size X        (GT[1] / PostGIS scalex)
            // Line 2: rotation about Y    (GT[4] / PostGIS skewy)
            // Line 3: rotation about X    (GT[2] / PostGIS skewx)
            // Line 4: pixel size Y        (GT[5] / PostGIS scaley, typically negative)
            // Line 5: center X of upper-left pixel
            // Line 6: center Y of upper-left pixel
            var lines = request.WorldFileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 6)
            {
                throw new InvalidDataException("World file must contain exactly 6 lines of georeferencing parameters.");
            }

            if (!double.TryParse(lines[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scaleX) ||
                !double.TryParse(lines[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var skewY) ||
                !double.TryParse(lines[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var skewX) ||
                !double.TryParse(lines[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scaleY) ||
                !double.TryParse(lines[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var upperLeftX) ||
                !double.TryParse(lines[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var upperLeftY))
            {
                throw new InvalidDataException("World file contains invalid numeric values.");
            }

            // A zero (or non-finite) pixel scale yields a degenerate geotransform with no spatial
            // extent, so reject it up front rather than persisting an unusable georeference.
            // Compare against a tolerance rather than exact 0.0: a pixel scale parsed from
            // user-supplied world-file text that is merely astronomically small is just as
            // degenerate as an exact zero, and exact float equality would let it slip through.
            const double MinPixelScaleMagnitude = 1e-9;
            if (Math.Abs(scaleX) < MinPixelScaleMagnitude || Math.Abs(scaleY) < MinPixelScaleMagnitude ||
                !double.IsFinite(scaleX) || !double.IsFinite(scaleY))
            {
                throw new InvalidDataException("World file pixel scale (lines 1 and 4) must be non-zero and finite.");
            }

            // Apply georeferencing via ST_SetGeoReference.
            // World files store the CENTER of the upper-left pixel, but the
            // PostGIS 6-parameter overload maps directly to the GDAL
            // geotransform which expects the CORNER (upper-left) of the
            // upper-left pixel.  Convert center â†’ corner before the call.
            // Behavior reference: GDAL GDALReadWorldFile (gdalworldfile.cpp)
            var cornerX = upperLeftX - scaleX / 2.0 - skewX / 2.0;
            var cornerY = upperLeftY - skewY / 2.0 - scaleY / 2.0;

            await using var geoRefCommand = connection.CreateCommand();
            geoRefCommand.Transaction = transaction;
            geoRefCommand.CommandText = $"""
                UPDATE {_rasterDataTable}
                SET raster = ST_SetGeoReference(raster, @cornerX, @cornerY, @scaleX, @scaleY, @skewX, @skewY)
                WHERE id = @rasterId
                """;
            geoRefCommand.AddParameter("@scaleX", scaleX);
            geoRefCommand.AddParameter("@skewX", skewX);
            geoRefCommand.AddParameter("@skewY", skewY);
            geoRefCommand.AddParameter("@scaleY", scaleY);
            geoRefCommand.AddParameter("@cornerX", cornerX);
            geoRefCommand.AddParameter("@cornerY", cornerY);
            geoRefCommand.AddParameter("@rasterId", rasterId);

            await geoRefCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Apply SRID
        var srid = request.Srid;
        if (srid == null && !string.IsNullOrWhiteSpace(request.PrjFileContent))
        {
            // Reuse the shared CRS detection service which handles AUTHORITY tags,
            // well-known names (e.g. ArcGIS GCS_WGS_1984), and WKT fallback.
            srid = await _crsDetectionService.DetectFromPrjAsync(request.PrjFileContent, cancellationToken).ConfigureAwait(false);
            if (srid == null)
            {
                warnings.Add("Could not resolve SRID from .prj file content. Set 'srid' explicitly.");
            }
        }

        if (srid.HasValue)
        {
            await using var sridCommand = connection.CreateCommand();
            sridCommand.Transaction = transaction;
            sridCommand.CommandText = $"""
                UPDATE {_rasterDataTable}
                SET raster = ST_SetSRID(raster, @srid)
                WHERE id = @rasterId
                """;
            sridCommand.AddParameter("@srid", srid.Value);
            sridCommand.AddParameter("@rasterId", rasterId);

            await sridCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> ComputeAndStoreStatisticsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long rasterId,
        int bandCount,
        IProgress<RasterImportProgress>? progress,
        string operationId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        // Compute statistics for all bands via ST_SummaryStats and insert into raster_statistics.
        // nodata_pixel_count = total_pixels - valid_pixel_count (ST_SummaryStats.count is valid only).
        // Cast to bigint to avoid integer overflow for large rasters (width * height can exceed int32).
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {_rasterStatisticsTable} (raster_data_id, band_number, min_value, max_value, mean_value, std_dev, valid_pixel_count, nodata_pixel_count)
            SELECT @rasterId, sub.band_num,
                   (sub.stats).min, (sub.stats).max, (sub.stats).mean, (sub.stats).stddev,
                   (sub.stats).count,
                   (sub.total_pixels - (sub.stats).count)
            FROM (
                SELECT g.band_num, ST_SummaryStats(r.raster, g.band_num) AS stats,
                       (r.width::bigint * r.height::bigint) AS total_pixels
                FROM {_rasterDataTable} r,
                     generate_series(1, @bandCount) AS g(band_num)
                WHERE r.id = @rasterId
            ) sub
            """;
        command.AddParameter("@rasterId", rasterId);
        command.AddParameter("@bandCount", bandCount);

        var rowsInserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        ReportProgress(progress, operationId, startedAt, RasterImportPhase.ComputingStatistics, OperationStatus.Processing,
            $"Computed statistics for {rowsInserted} bands", rasterId: rasterId,
            bandsProcessed: rowsInserted, totalBands: bandCount);

        return rowsInserted;
    }

    private async Task<int> GenerateAndStoreTilesAsync(
        DbConnection connection,
        DbTransaction transaction,
        long rasterId,
        int[] zoomLevels,
        IProgress<RasterImportProgress>? progress,
        string operationId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var totalTiles = 0;

        foreach (var zoom in zoomLevels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;

            if (zoom == 0)
            {
                // Zoom 0 has exactly one tile (0,0) covering the entire world.
                // Handle separately because the general formula uses (1 << (zoom-1))
                // which is undefined for negative shift counts in PostgreSQL.
                // Build a 256Ã—256 reference raster aligned to the z=0 tile envelope so the
                // seeded tile covers exactly the world bbox (not just the raster's own extent
                // stretched to fill the tile, which misregisters partial-world datasets).
                command.CommandText = $"""
                    INSERT INTO {_rasterTilesTable} (raster_data_id, zoom_level, tile_x, tile_y, tile_data, content_type)
                    SELECT @rasterId, 0, 0, 0,
                        ST_AsGDALRaster(
                            ST_Resample(
                                ST_Transform(r.raster, 3857),
                                ST_MakeEmptyRaster(
                                    256, 256,
                                    ST_XMin(ST_TileEnvelope(0, 0, 0)),
                                    ST_YMax(ST_TileEnvelope(0, 0, 0)),
                                    (ST_XMax(ST_TileEnvelope(0, 0, 0)) - ST_XMin(ST_TileEnvelope(0, 0, 0))) / 256.0,
                                    -((ST_YMax(ST_TileEnvelope(0, 0, 0)) - ST_YMin(ST_TileEnvelope(0, 0, 0))) / 256.0),
                                    0.0, 0.0, 3857
                                )
                            ),
                            'PNG'
                        ),
                        'image/png'
                    FROM {_rasterDataTable} r
                    WHERE r.id = @rasterId
                    ON CONFLICT (raster_data_id, zoom_level, tile_x, tile_y) DO UPDATE SET
                        tile_data = EXCLUDED.tile_data,
                        created_at = NOW()
                    """;
                command.AddParameter("@rasterId", rasterId);
            }
            else
            {
                // Generate tiles for this zoom level using ST_TileEnvelope.
                // Compute which tiles intersect the raster extent, then resample each onto a
                // 256Ã—256 reference raster aligned exactly to ST_TileEnvelope(z,x,y) in
                // EPSG:3857. Uncovered pixels become nodata rather than being stretched to fill
                // the frame â€” fixing the spatial misregistration the previous ST_Clip+ST_Resize
                // approach produced for edge tiles and non-3857 source rasters.
                command.CommandText = $"""
                    INSERT INTO {_rasterTilesTable} (raster_data_id, zoom_level, tile_x, tile_y, tile_data, content_type)
                    SELECT @rasterId, @zoom, t.x, t.y,
                        ST_AsGDALRaster(
                            ST_Resample(
                                ST_Transform(r.raster, 3857),
                                ST_MakeEmptyRaster(
                                    256, 256,
                                    ST_XMin(ST_TileEnvelope(@zoom, t.x, t.y)),
                                    ST_YMax(ST_TileEnvelope(@zoom, t.x, t.y)),
                                    (ST_XMax(ST_TileEnvelope(@zoom, t.x, t.y)) - ST_XMin(ST_TileEnvelope(@zoom, t.x, t.y))) / 256.0,
                                    -((ST_YMax(ST_TileEnvelope(@zoom, t.x, t.y)) - ST_YMin(ST_TileEnvelope(@zoom, t.x, t.y))) / 256.0),
                                    0.0, 0.0, 3857
                                )
                            ),
                            'PNG'
                        ),
                        'image/png'
                    FROM {_rasterDataTable} r
                    CROSS JOIN LATERAL (
                        SELECT x, y
                        FROM generate_series(
                            GREATEST(0, floor(ST_XMin(ST_Transform(ST_Envelope(r.raster), 3857)) / (40075016.686 / (1 << @zoom)))::int + (1 << (@zoom - 1))),
                            LEAST((1 << @zoom) - 1, floor(ST_XMax(ST_Transform(ST_Envelope(r.raster), 3857)) / (40075016.686 / (1 << @zoom)))::int + (1 << (@zoom - 1)))
                        ) AS x,
                        generate_series(
                            GREATEST(0, ((1 << (@zoom - 1)) - floor(ST_YMax(ST_Transform(ST_Envelope(r.raster), 3857)) / (40075016.686 / (1 << @zoom)))::int - 1)),
                            LEAST((1 << @zoom) - 1, ((1 << (@zoom - 1)) - floor(ST_YMin(ST_Transform(ST_Envelope(r.raster), 3857)) / (40075016.686 / (1 << @zoom)))::int))
                        ) AS y
                    ) t
                    WHERE r.id = @rasterId
                      AND ST_Intersects(ST_ConvexHull(r.raster), ST_Transform(ST_TileEnvelope(@zoom, t.x, t.y), ST_SRID(r.raster)))
                    ON CONFLICT (raster_data_id, zoom_level, tile_x, tile_y) DO UPDATE SET
                        tile_data = EXCLUDED.tile_data,
                        created_at = NOW()
                    """;
                command.AddParameter("@rasterId", rasterId);
                command.AddParameter("@zoom", zoom);
            }

            var tilesInserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            totalTiles += tilesInserted;

            ReportProgress(progress, operationId, startedAt, RasterImportPhase.GeneratingTiles, OperationStatus.Processing,
                $"Generated {totalTiles} tiles (zoom {zoom}: {tilesInserted})",
                rasterId: rasterId, tilesGenerated: totalTiles);
        }

        return totalTiles;
    }

    // =============================================================================
    // Overview pyramids (#1836)
    // =============================================================================

    /// <summary>
    /// Builds and persists power-of-two reduced-resolution overview rasters (reprojected to
    /// EPSG:3857) into <c>raster_overviews</c>. Only factors that actually coarsen the source
    /// (target ground resolution &gt; the source's 3857 resolution) are stored, so a finer factor
    /// than the source already provides is skipped rather than producing an upsampled copy.
    /// Best-effort: a missing table (schema predates migration 061) downgrades to a warning.
    /// </summary>
    private async Task GenerateAndStoreOverviewsAsync(
        DbConnection connection,
        DbTransaction transaction,
        long rasterId,
        int[] overviewFactors,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Distinct, ascending, power-of-two-ish factors >= 2. Anything < 2 cannot coarsen.
        var factors = overviewFactors.Where(f => f >= 2).Distinct().Order().ToArray();
        if (factors.Length == 0)
        {
            return;
        }

        var built = 0;
        foreach (var factor in factors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Best-effort: a missing raster_overviews table (schema predates migration 061) or any
            // PostGIS/GDAL quirk (ST_Transform/ST_Rescale behaviour varying by server build) must
            // NOT abort the import transaction. Each factor runs inside its own savepoint so a
            // failure rolls back to the savepoint and downgrades to a warning, leaving the
            // transaction usable for the next factor and the remaining import phases.
            await RunBestEffortPhaseAsync(
                connection,
                transaction,
                async () =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandTimeout = OverviewComputeTimeoutSeconds;
                    // Reproject the source to the tile CRS once, then rescale to factor x its 3857
                    // ground resolution. ground_resolution caches the resulting metres/pixel so the
                    // read path can pick a level without detoasting the raster payload. The factor is
                    // skipped when it would not coarsen the source (target res <= source res).
                    command.CommandText = $"""
                        WITH src AS (
                            SELECT ST_Transform(raster, 3857) AS rast
                            FROM {_rasterDataTable}
                            WHERE id = @rasterId
                        ),
                        scaled AS (
                            SELECT
                                GREATEST(abs(ST_ScaleX(rast)) * @factor, abs(ST_ScaleX(rast))) AS target_res,
                                rast
                            FROM src
                        )
                        INSERT INTO {_rasterOverviewsTable} (raster_data_id, overview_factor, raster, ground_resolution)
                        SELECT @rasterId, @factor,
                               ST_Rescale(rast, target_res, -target_res, 'NearestNeighbor'),
                               target_res
                        FROM scaled
                        WHERE target_res > abs(ST_ScaleX(rast))
                        ON CONFLICT (raster_data_id, overview_factor) DO UPDATE SET
                            raster = EXCLUDED.raster,
                            ground_resolution = EXCLUDED.ground_resolution,
                            created_at = NOW()
                        """;
                    command.AddParameter("@rasterId", rasterId);
                    command.AddParameter("@factor", factor);

                    built += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                },
                ex => ex.SqlState == PostgresErrorCodes.UndefinedTable
                    ? "Overview pyramid build skipped: raster_overviews table is not provisioned (apply migration 061)."
                    : $"Overview pyramid build skipped for factor {factor}: {ex.SqlState}.",
                warnings,
                cancellationToken).ConfigureAwait(false);
        }

        PostgresRasterImportLog.OverviewsBuilt(_logger, rasterId, built, factors.Length);
    }

    // Building a pyramid level runs ST_Rescale across the full raster; give it the same generous
    // budget the statistics backfill uses so county-scale imagery finishes within the import.
    private const int OverviewComputeTimeoutSeconds = 300;

    // =============================================================================
    // Footprint / seamline store (#1804)
    // =============================================================================

    /// <summary>
    /// Persists the per-raster footprint (the convex hull of the raster's valid-data envelope)
    /// and a default seamline equal to that footprint into <c>raster_footprints</c>. The default
    /// seamline gives <c>esriMosaicSeamline</c> a deterministic per-raster clip even before a
    /// hand-authored cutline is supplied; a later operation can replace the seamline geometry.
    /// Best-effort: a missing table (schema predates migration 062) downgrades to a warning, and
    /// seamline mosaics then contribute each raster's full footprint.
    /// </summary>
    private async Task GenerateAndStoreFootprintAsync(
        DbConnection connection,
        DbTransaction transaction,
        long rasterId,
        int srid,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        // Best-effort: a missing raster_footprints table (schema predates migration 062) or any
        // PostGIS/GDAL quirk (ST_ConvexHull behaviour varying by server build) must NOT abort the
        // import transaction. The work runs inside a savepoint so a failure rolls back to the
        // savepoint and downgrades to a warning, leaving the transaction usable for the commit.
        await RunBestEffortPhaseAsync(
            connection,
            transaction,
            async () =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO {_rasterFootprintsTable} (raster_data_id, footprint, seamline, srid, updated_at)
                    SELECT @rasterId, hull, hull, @srid, NOW()
                    FROM (
                        SELECT ST_ConvexHull(raster) AS hull
                        FROM {_rasterDataTable}
                        WHERE id = @rasterId
                    ) AS f
                    WHERE hull IS NOT NULL
                    ON CONFLICT (raster_data_id) DO UPDATE SET
                        footprint = EXCLUDED.footprint,
                        seamline = EXCLUDED.seamline,
                        srid = EXCLUDED.srid,
                        updated_at = NOW()
                    """;
                command.AddParameter("@rasterId", rasterId);
                command.AddParameter("@srid", srid);

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            ex => ex.SqlState == PostgresErrorCodes.UndefinedTable
                ? "Footprint/seamline store skipped: raster_footprints table is not provisioned (apply migration 062)."
                : $"Footprint/seamline store skipped: {ex.SqlState}.",
            warnings,
            cancellationToken).ConfigureAwait(false);
    }

    // =============================================================================
    // Progress reporting
    // =============================================================================

    private static void ReportProgress(
        IProgress<RasterImportProgress>? progress,
        string operationId,
        DateTimeOffset startedAt,
        RasterImportPhase phase,
        OperationStatus status,
        string currentPhase,
        long? rasterId = null,
        int bandsProcessed = 0,
        int? totalBands = null,
        int tilesGenerated = 0,
        int? totalTiles = null,
        double? percentComplete = null,
        DateTimeOffset? completedAt = null,
        string? errorMessage = null,
        IReadOnlyList<string>? warnings = null)
    {
        progress?.Report(new RasterImportProgress
        {
            OperationId = operationId,
            Phase = phase,
            Status = status,
            CurrentPhase = currentPhase,
            RasterId = rasterId,
            BandsProcessed = bandsProcessed,
            TotalBands = totalBands,
            TilesGenerated = tilesGenerated,
            TotalTiles = totalTiles,
            PercentComplete = percentComplete,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            ErrorMessage = errorMessage,
            Warnings = warnings ?? []
        });
    }
}
