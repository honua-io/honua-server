// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Services;

namespace Honua.Postgres.Features.Import;

internal sealed partial class StreamingFileImportService
{
    /// <summary>
    /// Outcome of GeoParquet pre-import validation: either a successful prep with the
    /// buffered scratch and detected SRID, or a populated error message for early failure.
    /// </summary>
    private readonly record struct GeoParquetPrepResult(
        GeoParquetScratch? Scratch,
        Stream? Stream,
        int? DetectedSrid,
        string[] Warnings,
        string? ErrorMessage);

    /// <summary>
    /// Buffers and validates a GeoParquet source: extracts metadata, enforces row-group
    /// caps and WKB-encoding requirements. Returns ErrorMessage when validation fails so
    /// the orchestrator can short-circuit with a uniform failure result.
    /// </summary>
    private async Task<GeoParquetPrepResult> PrepareGeoParquetImportAsync(
        Stream fileStream,
        string jobId,
        string tableName,
        string[] warnings,
        CancellationToken cancellationToken)
    {
        GeoParquetScratch scratch;
        try
        {
            scratch = await PrepareGeoParquetScratchAsync(fileStream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ImportLog.ImportFailedWithException(_logger, ex, jobId, tableName);
            return new GeoParquetPrepResult(
                null, null, null, warnings, "Failed to buffer GeoParquet file for reading.");
        }

        var scratchStream = scratch.Stream;

        GeoParquetReader.GeoParquetFileMetadata parquetMeta;
        try
        {
            parquetMeta = await GeoParquetReader.ExtractMetadataAsync(scratchStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            // Preserve the specific validation message (malformed geo metadata,
            // missing primary column, etc.) instead of collapsing to "Import failed."
            ImportLog.ImportFailedWithException(_logger, ex, jobId, tableName);
            return new GeoParquetPrepResult(scratch, null, null, warnings, ex.Message);
        }

        var updatedWarnings = parquetMeta.Warnings;

        // Hard-reject files with any oversized row group — Parquet.Net materializes
        // an entire row group's columns in memory, so unbounded groups defeat the
        // streaming memory contract. Log and fail fast instead of silently spiking memory.
        if (parquetMeta.MaxRowGroupRowCount > GeoParquetReader.MaxRowsPerRowGroup)
        {
            GeoParquetLog.LargeRowGroupDetected(_logger, parquetMeta.RowGroupCount, parquetMeta.MaxRowGroupRowCount);
            var msg = GeoParquetReader.BuildLargeRowGroupMessage(
                parquetMeta.MaxRowGroupRowCount, parquetMeta.RowGroupCount);
            return new GeoParquetPrepResult(scratch, null, null, updatedWarnings, msg);
        }

        // Hard-reject non-WKB encoding before any further processing
        if (!parquetMeta.IsWkbEncoding)
        {
            return new GeoParquetPrepResult(
                scratch, null, null, updatedWarnings, GeoParquetReader.UnsupportedEncodingMessage);
        }

        return new GeoParquetPrepResult(scratch, scratchStream, parquetMeta.Srid, updatedWarnings, null);
    }
}
