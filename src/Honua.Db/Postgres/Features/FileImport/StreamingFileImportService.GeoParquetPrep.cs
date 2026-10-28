// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Db.Postgres.Features.Migration;
using Honua.Db.Postgres.Features.FileImport;

namespace Honua.Db.Postgres.Features.FileImport;

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

        try
        {
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
                // Preserve specific validation messages after stripping unsafe environment details.
                ImportLog.ImportFailedWithException(_logger, ex, jobId, tableName);
                return new GeoParquetPrepResult(scratch, null, null, warnings, SanitizeImportValidationMessage(ex));
            }

            var updatedWarnings = parquetMeta.Warnings;

            // Refuse unsupported source semantics before database access.
            if (parquetMeta.UnsupportedReason is { } unsupportedReason)
            {
                return new GeoParquetPrepResult(
                    scratch, null, null, updatedWarnings, unsupportedReason);
            }

            // Validate every bounded source row before opening the target write path. A late unsupported
            // nested value or malformed geometry must not leave earlier append/upsert batches committed.
            try
            {
                await foreach (var feature in GeoParquetReader.ReadStreamingAsync(scratchStream, _limits, cancellationToken))
                {
                    _ = feature;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
            {
                ImportLog.ImportFailedWithException(_logger, ex, jobId, tableName);
                return new GeoParquetPrepResult(scratch, null, null, updatedWarnings, SanitizeImportValidationMessage(ex));
            }
            scratchStream.Position = 0;
            return new GeoParquetPrepResult(scratch, scratchStream, parquetMeta.Srid, updatedWarnings, null);
        }
        catch
        {
            // Ownership reaches the caller only when preparation returns, including validation failures.
            CleanupGeoParquetScratch(scratch);
            throw;
        }
    }
}
