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

        var start = await StartProcessingAsync(new ExportAdmission(job, request, context), cancellationToken).ConfigureAwait(false);
        return start switch
        {
            { Progress: null } => NotRetryable("Export progress is no longer active."),
            _ => await ExportAsync(new ExportRun(request, start.Progress, context), cancellationToken).ConfigureAwait(false)
        };
    }

    private async Task<ExportStart> StartProcessingAsync(ExportAdmission attempt, CancellationToken cancellationToken)
    {
        var (job, request, context) = attempt;
        var existing = await progressStore.GetProgressAsync<ExportProgress>(request.JobId, cancellationToken).ConfigureAwait(false);
        await context.ThrowIfExecutionLeaseLostAsync(cancellationToken).ConfigureAwait(false);
        if (existing?.Status == OperationStatus.Cancelled && job.CancellationRequestedAt.HasValue)
        {
            throw new OperationCanceledException("The export was cancelled before it started.");
        }

        var processing = Fresh(request);
        var admission = await progressStore.TryReplaceProgressAsync(
                new OperationProgressWrite(request.JobId, processing, ExportJobService.Retention),
                existing,
                cancellationToken)
            .ConfigureAwait(false);
        return admission.Outcome == ProgressCompareAndSetOutcome.Updated
            ? new ExportStart(processing)
            : Lost(admission.CurrentProgress as ExportProgress);
    }

    private static ExportStart Lost(ExportProgress? current) => current?.Status switch
    {
        OperationStatus.Cancelled => throw new OperationCanceledException("The export was cancelled before it started."),
        _ => new ExportStart(null)
    };

    private static ExportProgress Fresh(ExportJob request)
        => ExportProgress.CreateInitial(request.JobId, request.Format, request.ServiceName, request.ServiceLayerIndex, request.TotalFeatures)
            with
            { Status = OperationStatus.Processing, CurrentPhase = ExportingPhase };

    private async Task<JobExecutionResult> ExportAsync(ExportRun run, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(run.ScratchDir);
        using var activity = StartActivity(run.Request);
        await using var scope = scopeFactory.CreateAsyncScope();
        var cloudStorage = scope.ServiceProvider.GetRequiredService<ICloudFileStorage>();
        try
        {
            var result = await WriteUploadAndCompleteAsync(run, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(run.Completed ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
            return result;
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
            ExportLog.AsyncExportFailed(logger, run.Request.JobId, ex);
            return NotRetryable(ExportFailureMessage);
        }
        finally
        {
            await CleanUpAsync(run, cloudStorage).ConfigureAwait(false);
        }
    }

    private async Task<JobExecutionResult> WriteUploadAndCompleteAsync(
        ExportRun run,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var request = run.Request;
        var output = await WriteToFileAsync(run, services, cancellationToken)
            .ConfigureAwait(false);
        var downloadUrl = await UploadAsync(new ExportUpload(run, output), services.GetRequiredService<ICloudFileStorage>(), cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (run.UploadedFileId is not null
            && !await run.Context.TryPublishArtifactAsync(run.UploadedFileId, cancellationToken).ConfigureAwait(false))
        {
            throw new OperationCanceledException("The export attempt can no longer publish its result.");
        }

        var done = run.Processing with
        {
            Status = OperationStatus.Completed,
            ProcessedFeatures = output.WrittenCount,
            Warnings = output.Warnings,
            OutputSizeBytes = new FileInfo(output.Path).Length,
            DownloadUrl = downloadUrl,
            CompletedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Export completed"
        };

        // Cancellation and replacement attempts must both win over this attempt's completion.
        var completion = await progressStore.TryReplaceProgressAsync(
                new OperationProgressWrite(request.JobId, done, ExportJobService.Retention),
                run.Processing,
                cancellationToken)
            .ConfigureAwait(false);
        if (completion.Outcome != ProgressCompareAndSetOutcome.Updated)
        {
            return completion.CurrentProgress?.Status == OperationStatus.Cancelled
                ? throw new OperationCanceledException("The export was cancelled while it ran.")
                : NotRetryable("Export progress is no longer active.");
        }

        run.Completed = true;
        Activity.Current?.SetTag("export.output_size_bytes", done.OutputSizeBytes);
        ExportLog.AsyncExportCompleted(logger, request.JobId, request.TotalFeatures, done.OutputSizeBytes);
        return JobExecutionResult.Succeeded(output.Warnings);
    }

    private static async Task<string?> UploadAsync(
        ExportUpload upload,
        ICloudFileStorage cloudStorage,
        CancellationToken cancellationToken)
    {
        var (run, output) = upload;
        await using (var fileStream = File.OpenRead(output.Path))
        {
            var uploadResult = await cloudStorage.UploadAsync(new FileUploadRequest
            {
                Content = fileStream,
                FileName = Path.GetFileName(output.Path),
                ContentType = GetContentType(run.Request.Format),
                Folder = "exports",
                TimeToLive = ExportJobService.Retention
            }, cancellationToken).ConfigureAwait(false);
            run.UploadedFileId = uploadResult.File?.FileId;
        }

        return run.UploadedFileId is null
            ? null
            : await cloudStorage.GetPresignedUrlAsync(run.UploadedFileId, ExportJobService.Retention, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task CleanUpAsync(ExportRun run, ICloudFileStorage cloudStorage)
    {
        if (!run.Completed && run.UploadedFileId is not null)
        {
            await DeleteArtifactBestEffortAsync(cloudStorage, run.Request.JobId, run.UploadedFileId).ConfigureAwait(false);
        }

        try
        {
            Directory.Delete(run.ScratchDir, recursive: true);
        }
        catch (Exception cleanupEx) when (cleanupEx is not OutOfMemoryException)
        {
            // Cleanup failure must not fail the export job itself; log and continue.
            ExportJobServiceLog.ScratchDirCleanupFailed(logger, run.Request.JobId, run.ScratchDir, cleanupEx);
        }
    }

    private static Activity? StartActivity(ExportJob request)
    {
        var activity = HonuaTelemetry.ActivitySource.StartActivity("Export.ProcessQueuedJob");
        activity?.SetTag("export.job_id", request.JobId);
        activity?.SetTag("export.format", request.Format);
        activity?.SetTag("export.service_name", request.ServiceName);
        activity?.SetTag("export.storage_layer_id", request.StorageLayerId);
        activity?.SetTag("export.total_features", request.TotalFeatures);
        return activity;
    }

    private static JobExecutionResult NotRetryable(string message)
        => JobExecutionResult.Failed(message) with
        { IsRetryable = false };

    private async Task<ExportFileResult> WriteToFileAsync(
        ExportRun run,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var job = run.Request;
        var features = services.GetRequiredService<IStreamingFeatureStore>()
            .StreamFeaturesAsync(job.StorageLayerId, job.Query, cancellationToken);
        var crsRegistry = services.GetRequiredService<ICrsRegistry>();
        var crs = await crsRegistry.ResolveBySridAsync(job.OutputSrid, cancellationToken).ConfigureAwait(false);
        var srsName = crs.HasValue ? $"EPSG:{job.OutputSrid}" : null;
        var srsWkt = crs?.Wkt;

        // SanitizeExportFilename strips path separators, drive/colon characters, and
        // traversal segments (FileUploadSecurity.SanitizeFileName), so baseName cannot be
        // rooted and the Path.Join calls below cannot silently drop the scratch directory.
        var basePath = Path.Join(run.ScratchDir, ExportEndpoints.SanitizeExportFilename(job.ServiceName, job.LayerName));

        switch (BuiltInExportFormats.Resolve(job.Format))
        {
            case BuiltInExportFormat.Csv:
                {
                    await using var stream = File.Create($"{basePath}.csv");
                    var writtenCount = await CsvExportWriter.WriteAsync(stream, features, job.Fields, cancellationToken).ConfigureAwait(false);
                    return new ExportFileResult($"{basePath}.csv", writtenCount, []);
                }
            case BuiltInExportFormat.Shapefile:
                {
                    await using var stream = File.Create($"{basePath}.zip");
                    var result = await ShapefileExportWriter.WriteAsync(
                        stream, features, job.Fields, job.GeometryType, srsWkt, logger, cancellationToken).ConfigureAwait(false);
                    return new ExportFileResult($"{basePath}.zip", result.WrittenCount, result.Warnings);
                }
            case BuiltInExportFormat.GeoPackage:
                {
                    var writtenCount = await GeoPackageExportWriter.WriteAsync(
                        $"{basePath}.gpkg", features, job.Fields, job.GeometryType, job.OutputSrid, srsName, srsWkt, cancellationToken).ConfigureAwait(false);
                    return new ExportFileResult($"{basePath}.gpkg", writtenCount, []);
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

    private sealed record ExportAdmission(ExecutionJobRecord Job, ExportJob Request, IJobExecutionContext Context);

    private sealed record ExportStart(ExportProgress? Progress);

    private sealed record ExportFileResult(string Path, int WrittenCount, IReadOnlyList<string> Warnings);

    // One export attempt: the request, its Processing progress, its scratch directory, and the upload
    // it may have to retract.
    private sealed record ExportUpload(ExportRun Run, ExportFileResult Output);

    private sealed class ExportRun(ExportJob request, ExportProgress processing, IJobExecutionContext context)
    {
        public IJobExecutionContext Context { get; } = context;

        public ExportJob Request { get; } = request;

        public ExportProgress Processing { get; } = processing;

        public string ScratchDir { get; } = Path.Join(Path.GetTempPath(), "honua-export", $"{request.JobId}-{Guid.NewGuid():N}");

        public string? UploadedFileId { get; set; }

        public bool Completed { get; set; }
    }
}
