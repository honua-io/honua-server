// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Io.Export.Writers;
using Honua.ServiceDefaults;

namespace Honua.Io.Export;

// Runs a claimed Export job. The worker has already entered the submitter's security scope and owns
// the terminal state; ExportJobTerminalCallback closes the progress when the job fails or is cancelled.
internal sealed class ExportJobExecutor(
    IUniversalProgressStore progressStore,
    IServiceScopeFactory scopeFactory,
    ILogger<ExportJobExecutor> logger) : IJobExecutor
{
    internal const string ExportFailureMessage = "Export failed.";
    private const string ExportingPhase = "Exporting features";

    public ExecutionJobKind Kind => ExecutionJobKind.Export;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!ExportJobService.TryReadRequest(job, out var request))
        {
            return NotRetryable("Export request is missing or unreadable.");
        }

        if (job.Audit.SubmitterSecurityContext is null)
        {
            return NotRetryable("Export request has no captured submitter identity.");
        }

        var processing = await StartProcessingAsync(request, cancellationToken).ConfigureAwait(false);
        if (processing is null)
        {
            return NotRetryable("Export progress is no longer active.");
        }

        // "honua-export" is a compile-time relative literal and the job id is always the
        // server-generated Guid.NewGuid().ToString("N") minted in ExportEndpoints (never
        // caller-supplied), so this combine cannot silently drop Path.GetTempPath().
        var scratchDir = Path.Join(Path.GetTempPath(), "honua-export", request.JobId);
        Directory.CreateDirectory(scratchDir);

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Export.ProcessQueuedJob");
        activity?.SetTag("export.job_id", request.JobId);
        activity?.SetTag("export.format", request.Format);
        activity?.SetTag("export.service_name", request.ServiceName);
        activity?.SetTag("export.layer_id", request.LayerId);
        activity?.SetTag("export.total_features", request.TotalFeatures);

        await using var scope = scopeFactory.CreateAsyncScope();
        var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();
        string? uploadedFileId = null;
        var completed = false;
        try
        {
            var streamingStore = scope.ServiceProvider.GetRequiredService<IStreamingFeatureStore>();
            var crsRegistry = scope.ServiceProvider.GetRequiredService<ICrsRegistry>();

            var features = streamingStore.StreamFeaturesAsync(request.LayerId, request.Query, cancellationToken);
            var output = await WriteToFileAsync(request, features, scratchDir, crsRegistry, logger, cancellationToken).ConfigureAwait(false);
            var fileInfo = new FileInfo(output.Path);

            await using (var fileStream = File.OpenRead(output.Path))
            {
                var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
                {
                    Content = fileStream,
                    FileName = Path.GetFileName(output.Path),
                    ContentType = GetContentType(request.Format),
                    Folder = "exports",
                    TimeToLive = ExportJobService.Retention
                }, cancellationToken).ConfigureAwait(false);
                uploadedFileId = uploadResult.File?.FileId;
            }

            var downloadUrl = uploadedFileId is not null
                ? await cloudStorage.GetPresignedUrlAsync(uploadedFileId, ExportJobService.Retention, cancellationToken).ConfigureAwait(false)
                : null;

            cancellationToken.ThrowIfCancellationRequested();
            var done = processing with
            {
                Status = OperationStatus.Completed,
                ProcessedFeatures = output.WrittenCount,
                Warnings = output.Warnings,
                OutputSizeBytes = fileInfo.Length,
                DownloadUrl = downloadUrl,
                CompletedAt = DateTimeOffset.UtcNow,
                CurrentPhase = "Export completed"
            };

            // A cancellation accepted while the file was written wins over the completion.
            var completion = await progressStore.TrySetProgressAsync(
                    new OperationProgressWrite(request.JobId, done, ExportJobService.Retention),
                    OperationStatus.Processing,
                    cancellationToken)
                .ConfigureAwait(false);
            if (completion.Outcome != ProgressCompareAndSetOutcome.Updated)
            {
                if (completion.CurrentProgress?.Status == OperationStatus.Cancelled)
                {
                    throw new OperationCanceledException("The export was cancelled while it ran.");
                }

                return NotRetryable("Export progress is no longer active.");
            }

            completed = true;
            activity?.SetTag("export.output_size_bytes", fileInfo.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);
            ExportLog.AsyncExportCompleted(logger, request.JobId, request.TotalFeatures, fileInfo.Length);
            return JobExecutionResult.Succeeded(output.Warnings);
        }
        // A provider can surface cancellation as its own exception; the worker decides cancellation from OCE.
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException
            && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The export was cancelled while it ran.", ex, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            ExportLog.AsyncExportFailed(logger, request.JobId, ex);
            return NotRetryable(ExportFailureMessage);
        }
        finally
        {
            if (!completed && uploadedFileId is not null)
            {
                await DeleteArtifactBestEffortAsync(cloudStorage, request.JobId, uploadedFileId).ConfigureAwait(false);
            }

            try
            {
                Directory.Delete(scratchDir, recursive: true);
            }
            catch (Exception cleanupEx) when (cleanupEx is not OutOfMemoryException)
            {
                // Cleanup failure must not fail the export job itself; log and continue.
                ExportJobServiceLog.ScratchDirCleanupFailed(logger, request.JobId, scratchDir, cleanupEx);
            }
        }
    }

    // A retry after a lost node finds the progress already Processing and takes it over.
    private async Task<ExportProgress?> StartProcessingAsync(ExportJob request, CancellationToken cancellationToken)
    {
        var existing = await progressStore.GetProgressAsync<ExportProgress>(request.JobId, cancellationToken).ConfigureAwait(false);
        if (existing?.Status == OperationStatus.Cancelled)
        {
            throw new OperationCanceledException("The export was cancelled before it started.");
        }

        var processing = (existing ?? ExportProgress.CreateInitial(
                request.JobId, request.Format, request.ServiceName, request.LayerId, request.TotalFeatures)) with
        {
            Status = OperationStatus.Processing,
            CurrentPhase = ExportingPhase
        };
        if (existing is null)
        {
            await progressStore.SetProgressAsync(request.JobId, processing, ExportJobService.Retention, cancellationToken).ConfigureAwait(false);
            return processing;
        }

        var admission = await progressStore.TrySetProgressAsync(
                new OperationProgressWrite(request.JobId, processing, ExportJobService.Retention),
                existing.Status,
                cancellationToken)
            .ConfigureAwait(false);
        if (admission.Outcome == ProgressCompareAndSetOutcome.Updated)
        {
            return processing;
        }

        if (admission.CurrentProgress?.Status == OperationStatus.Cancelled)
        {
            throw new OperationCanceledException("The export was cancelled before it started.");
        }

        return null;
    }

    private static JobExecutionResult NotRetryable(string message)
        => JobExecutionResult.Failed(message) with { IsRetryable = false };

    private sealed record ExportFileResult(string Path, int WrittenCount, IReadOnlyList<string> Warnings);

    private static async Task<ExportFileResult> WriteToFileAsync(
        ExportJob job,
        IAsyncEnumerable<Feature> features,
        string scratchDir,
        ICrsRegistry crsRegistry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? srsName = null;
        string? srsWkt = null;
        var crs = await crsRegistry.ResolveBySridAsync(job.OutputSrid, cancellationToken).ConfigureAwait(false);
        if (crs.HasValue)
        {
            srsName = $"EPSG:{job.OutputSrid}";
            srsWkt = crs.Value.Wkt;
        }

        // SanitizeExportFilename strips path separators, drive/colon characters, and
        // traversal segments (FileUploadSecurity.SanitizeFileName), so baseName cannot be
        // rooted and the Path.Combine calls below cannot silently drop scratchDir.
        var baseName = ExportEndpoints.SanitizeExportFilename(job.ServiceName, job.LayerName);

        switch (BuiltInExportFormats.Resolve(job.Format))
        {
            case BuiltInExportFormat.Csv:
                {
                    var csvPath = Path.Join(scratchDir, $"{baseName}.csv");
                    await using var stream = File.Create(csvPath);
                    var writtenCount = await CsvExportWriter.WriteAsync(stream, features, job.Fields, cancellationToken).ConfigureAwait(false);
                    return new ExportFileResult(csvPath, writtenCount, []);
                }
            case BuiltInExportFormat.Shapefile:
                {
                    var zipPath = Path.Join(scratchDir, $"{baseName}.zip");
                    await using var stream = File.Create(zipPath);
                    var result = await ShapefileExportWriter.WriteAsync(
                        stream, features, job.Fields, job.GeometryType, srsWkt, logger, cancellationToken).ConfigureAwait(false);
                    return new ExportFileResult(zipPath, result.WrittenCount, result.Warnings);
                }
            case BuiltInExportFormat.GeoPackage:
                {
                    var gpkgPath = Path.Join(scratchDir, $"{baseName}.gpkg");
                    var writtenCount = await GeoPackageExportWriter.WriteAsync(
                        gpkgPath, features, job.Fields, job.GeometryType, job.OutputSrid, srsName, srsWkt, cancellationToken).ConfigureAwait(false);
                    return new ExportFileResult(gpkgPath, writtenCount, []);
                }
            default:
                throw new InvalidOperationException($"Unsupported export format: {job.Format}");
        }
    }

    private async Task DeleteArtifactBestEffortAsync(ICloudFileStorage storage, string jobId, string fileId)
    {
        try
        {
            await storage.DeleteAsync(fileId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cloud cleanup must not replace the outcome the worker is about to record.
            ExportJobServiceLog.ArtifactCleanupFailed(logger, jobId, fileId, ex);
        }
    }

    private static string GetContentType(string format) => format.ToLowerInvariant() switch
    {
        "csv" => "text/csv",
        "shapefile" => "application/zip",
        "gpkg" => "application/geopackage+sqlite3",
        _ => "application/octet-stream"
    };
}
