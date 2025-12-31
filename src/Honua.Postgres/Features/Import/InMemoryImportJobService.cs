// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// In-memory implementation of import job service for background processing.
/// Uses Channel-based queue and background tasks for processing large files.
/// For production use with persistent storage, consider using a distributed job queue.
/// </summary>
internal sealed partial class InMemoryImportJobService : IImportJobService, IDisposable
{
    private readonly IFileImportService _importService;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<InMemoryImportJobService> _logger;
    private readonly ConcurrentDictionary<string, ImportJobState> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private bool _disposed;

    public InMemoryImportJobService(
        IFileImportService importService,
        IPerformanceMonitor performanceMonitor,
        ILogger<InMemoryImportJobService> logger)
    {
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<string> QueueImportAsync(
        ImportRequest request,
        long fileSize,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var format = _importService.DetectFormat(request.FileName) ?? SupportedFileFormat.GeoJson;
        var formatName = format.ToString();

        var progress = ImportProgress.CreateInitial(jobId, request.TableName, format, fileSize);
        var state = new ImportJobState
        {
            Progress = progress,
            Request = request,
            StartedAt = DateTimeOffset.UtcNow,
            FileSize = fileSize,
            Format = formatName
        };

        _jobs[jobId] = state;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokens[jobId] = cts;

        ImportJobLog.JobQueued(_logger, jobId, request.TableName, formatName, fileSize);
        RecordJobMetrics("queued", formatName, fileSize, null, null, null);

        // Copy stream to a temp file for background processing
        // In production, you'd want to save to durable storage
        if (request.FileStream.CanSeek)
        {
            request.FileStream.Position = 0;
        }

        var tempFilePath = Path.Combine(Path.GetTempPath(), $"honua-import-{jobId}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var tempStream = new FileStream(tempFilePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }))
            {
                await request.FileStream.CopyToAsync(tempStream, cancellationToken);
            }
        }
        catch
        {
            TryDeleteTempFile(tempFilePath);
            throw;
        }

        var backgroundStream = new FileStream(tempFilePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        var backgroundRequest = new ImportRequest
        {
            FileStream = backgroundStream,
            FileName = request.FileName,
            TableName = request.TableName,
            SourceSrid = request.SourceSrid,
            TargetSrid = request.TargetSrid,
            OverwriteExisting = request.OverwriteExisting
        };

        // Start background processing
        _ = ProcessJobAsync(jobId, backgroundRequest, tempFilePath, cts.Token);

        return jobId;
    }

    private async Task ProcessJobAsync(
        string jobId,
        ImportRequest request,
        string tempFilePath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var status = "failed";
        int? featureCount = null;
        int? failedFeatures = null;
        string? errorMessage = null;

        try
        {
            await using var stream = request.FileStream;
            if (_jobs.TryGetValue(jobId, out var state))
            {
                state.Progress = state.Progress with { Status = ImportStatus.Processing };
                ImportJobLog.JobStarted(_logger, jobId, state.Request.TableName, state.Format, state.FileSize);
            }

            var progress = new Progress<ImportProgress>(p =>
            {
                if (_jobs.TryGetValue(jobId, out var s))
                {
                    s.Progress = p;
                }
            });

            var result = await _importService.ImportFileAsync(request, progress, cancellationToken);

            if (_jobs.TryGetValue(jobId, out state))
            {
                status = result.Success ? "completed" : "failed";
                featureCount = result.FeatureCount;
                failedFeatures = state.Progress.FailedFeatures;
                errorMessage = result.ErrorMessage;

                state.Progress = state.Progress with
                {
                    Status = result.Success ? ImportStatus.Completed : ImportStatus.Failed,
                    FeaturesProcessed = result.FeatureCount,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = result.ErrorMessage
                };
                state.Result = result;

                if (result.Success)
                {
                    ImportJobLog.JobCompleted(_logger, jobId, state.Request.TableName, state.Format, state.FileSize,
                        result.FeatureCount, failedFeatures ?? 0, stopwatch.Elapsed.TotalMilliseconds);
                }
                else
                {
                    ImportJobLog.JobFailed(_logger, jobId, state.Request.TableName, state.Format, state.FileSize,
                        errorMessage ?? "Import failed", stopwatch.Elapsed.TotalMilliseconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                status = "cancelled";
                failedFeatures = state.Progress.FailedFeatures;
                state.Progress = state.Progress with
                {
                    Status = ImportStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow
                };
                ImportJobLog.JobCancelled(_logger, jobId, state.Request.TableName, state.Format, state.FileSize,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                status = "failed";
                errorMessage = ex.Message;
                failedFeatures = state.Progress.FailedFeatures;
                state.Progress = state.Progress with
                {
                    Status = ImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message
                };
                ImportJobLog.JobFailed(_logger, jobId, state.Request.TableName, state.Format, state.FileSize,
                    ex.Message, stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            stopwatch.Stop();
            if (_jobs.TryGetValue(jobId, out var state))
            {
                RecordJobMetrics(status, state.Format, state.FileSize, featureCount, failedFeatures, stopwatch.Elapsed);
            }

            TryDeleteTempFile(tempFilePath);
            _cancellationTokens.TryRemove(jobId, out var cts);
            cts?.Dispose();
        }
    }

    /// <inheritdoc/>
    public Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_jobs.TryGetValue(jobId, out var state))
        {
            return Task.FromResult<ImportProgress?>(state.Progress);
        }

        return Task.FromResult<ImportProgress?>(null);
    }

    /// <inheritdoc/>
    public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_cancellationTokens.TryGetValue(jobId, out var cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ImportProgress>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        var activeJobs = _jobs.Values
            .Where(j => j.Progress.Status is ImportStatus.Queued or ImportStatus.Processing)
            .Select(j => j.Progress)
            .ToList();

        return Task.FromResult<IReadOnlyList<ImportProgress>>(activeJobs);
    }

    private void RecordJobMetrics(
        string status,
        string format,
        long fileSize,
        int? featureCount,
        int? failedFeatures,
        TimeSpan? duration)
    {
        var tags = new Dictionary<string, string>
        {
            { "status", status },
            { "format", format },
            { "mode", "background" }
        };

        _performanceMonitor.RecordCounter("honua_import_jobs_total", 1, tags);
        _performanceMonitor.RecordHistogram("honua_import_job_bytes", fileSize, tags);

        if (duration.HasValue)
        {
            _performanceMonitor.RecordHistogram("honua_import_job_duration_ms", duration.Value.TotalMilliseconds, tags);
        }

        if (featureCount.HasValue)
        {
            _performanceMonitor.RecordHistogram("honua_import_job_features", featureCount.Value, tags);
        }

        if (failedFeatures.HasValue)
        {
            _performanceMonitor.RecordHistogram("honua_import_job_failed_features", failedFeatures.Value, tags);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var cts in _cancellationTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _cancellationTokens.Clear();
        _jobs.Clear();

        _disposed = true;
    }

    private static partial class ImportJobLog
    {
        [LoggerMessage(
            EventId = 7410,
            Level = LogLevel.Information,
            Message = "Import job queued {JobId} table={TableName} format={Format} bytes={Bytes}")]
        public static partial void JobQueued(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes);

        [LoggerMessage(
            EventId = 7411,
            Level = LogLevel.Information,
            Message = "Import job started {JobId} table={TableName} format={Format} bytes={Bytes}")]
        public static partial void JobStarted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes);

        [LoggerMessage(
            EventId = 7412,
            Level = LogLevel.Information,
            Message = "Import job completed {JobId} table={TableName} format={Format} bytes={Bytes} imported={Imported} failed={Failed} durationMs={DurationMs:F2}")]
        public static partial void JobCompleted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes,
            int imported,
            int failed,
            double durationMs);

        [LoggerMessage(
            EventId = 7413,
            Level = LogLevel.Warning,
            Message = "Import job cancelled {JobId} table={TableName} format={Format} bytes={Bytes} durationMs={DurationMs:F2}")]
        public static partial void JobCancelled(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes,
            double durationMs);

        [LoggerMessage(
            EventId = 7414,
            Level = LogLevel.Error,
            Message = "Import job failed {JobId} table={TableName} format={Format} bytes={Bytes} error={ErrorMessage} durationMs={DurationMs:F2}")]
        public static partial void JobFailed(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes,
            string errorMessage,
            double durationMs);
    }

    private sealed class ImportJobState
    {
        public required ImportProgress Progress { get; set; }
        public required ImportRequest Request { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required long FileSize { get; init; }
        public required string Format { get; init; }
        public ImportResult? Result { get; set; }
    }

    private static void TryDeleteTempFile(string tempFilePath)
    {
        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore failures
        }
    }
}
