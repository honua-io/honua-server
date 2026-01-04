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
    private const int MaxCompletedJobs = 1000;
    private static readonly TimeSpan _completedJobRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(5);
    private long _lastCleanupTick = Environment.TickCount64;
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
        CleanupCompletedJobsIfNeeded();
        request.Validate();

        var format = _importService.DetectFormat(request.FileName) ?? SupportedFileFormat.GeoJson;
        var formatName = format.ToString();

        ImportJobState state;
        string jobId;
        while (true)
        {
            jobId = Guid.NewGuid().ToString("N")[..8];
            var progress = ImportProgress.CreateInitial(jobId, request.TableName, format, fileSize);
            state = new ImportJobState
            {
                Progress = progress,
                Request = request,
                StartedAt = DateTimeOffset.UtcNow,
                FileSize = fileSize,
                Format = formatName
            };

            if (_jobs.TryAdd(jobId, state))
            {
                break;
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokens[jobId] = cts;

        ImportJobLog.JobQueued(_logger, jobId, request.TableName, formatName, fileSize);
        RecordJobMetrics("queued", formatName, fileSize, null, null, null);

        string? tempFilePath = null;
        var backgroundRequest = request;

        if (!request.UsesCloudStorage)
        {
            // Copy stream to a temp file for background processing
            // In production, you'd want to save to durable storage
            if (request.FileStream!.CanSeek)
            {
                request.FileStream.Position = 0;
            }

            tempFilePath = Path.Combine(Path.GetTempPath(), $"honua-import-{jobId}-{Guid.NewGuid():N}.tmp");
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

            backgroundRequest = request with
            {
                FileStream = backgroundStream,
                CloudFileId = null
            };
        }

        // Start background processing
        _ = ProcessJobAsync(jobId, backgroundRequest, tempFilePath, cts.Token);

        return jobId;
    }

    private async Task ProcessJobAsync(
        string jobId,
        ImportRequest request,
        string? tempFilePath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var status = "failed";
        int? featureCount = null;
        int? failedFeatures = null;
        string? errorMessage = null;
        var stream = request.FileStream;

        try
        {
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
        catch (Exception)
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                status = "failed";
                errorMessage = "Import failed.";
                failedFeatures = state.Progress.FailedFeatures;
                state.Progress = state.Progress with
                {
                    Status = ImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "Import failed."
                };
                ImportJobLog.JobFailed(_logger, jobId, state.Request.TableName, state.Format, state.FileSize,
                    errorMessage, stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        finally
        {
            stopwatch.Stop();
            if (_jobs.TryGetValue(jobId, out var state))
            {
                RecordJobMetrics(status, state.Format, state.FileSize, featureCount, failedFeatures, stopwatch.Elapsed);
            }

            if (stream != null)
            {
                await stream.DisposeAsync();
            }

            TryDeleteTempFile(tempFilePath);
            _cancellationTokens.TryRemove(jobId, out var cts);
            cts?.Dispose();
            CleanupCompletedJobsIfNeeded();
        }
    }

    /// <inheritdoc/>
    public Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        CleanupCompletedJobsIfNeeded();

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
        CleanupCompletedJobsIfNeeded();

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

    private void CleanupCompletedJobsIfNeeded()
    {
        var intervalMs = (long)_cleanupInterval.TotalMilliseconds;
        var nowTick = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastCleanupTick);
        if (nowTick - last < intervalMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastCleanupTick, nowTick, last) != last)
        {
            return;
        }

        CleanupCompletedJobs(DateTimeOffset.UtcNow);
    }

    private void CleanupCompletedJobs(DateTimeOffset now)
    {
        var expirationCutoff = now - _completedJobRetention;
        var completedJobs = _jobs
            .Where(kvp => IsTerminalStatus(kvp.Value.Progress.Status))
            .Select(kvp => new
            {
                kvp.Key,
                CompletedAt = kvp.Value.Progress.CompletedAt ?? kvp.Value.StartedAt
            })
            .ToList();

        foreach (var job in completedJobs)
        {
            if (job.CompletedAt <= expirationCutoff)
            {
                RemoveJob(job.Key);
            }
        }

        var remainingCompleted = _jobs
            .Where(kvp => IsTerminalStatus(kvp.Value.Progress.Status))
            .Select(kvp => new
            {
                kvp.Key,
                CompletedAt = kvp.Value.Progress.CompletedAt ?? kvp.Value.StartedAt
            })
            .OrderBy(kvp => kvp.CompletedAt)
            .ToList();

        var overflow = remainingCompleted.Count - MaxCompletedJobs;
        if (overflow <= 0)
        {
            return;
        }

        for (var i = 0; i < overflow; i++)
        {
            RemoveJob(remainingCompleted[i].Key);
        }
    }

    private void RemoveJob(string jobId)
    {
        _jobs.TryRemove(jobId, out _);
        if (_cancellationTokens.TryRemove(jobId, out var cts))
        {
            cts.Dispose();
        }
    }

    private static bool IsTerminalStatus(ImportStatus status)
        => status is ImportStatus.Completed or ImportStatus.Failed or ImportStatus.Cancelled;

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

    private static void TryDeleteTempFile(string? tempFilePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tempFilePath) && File.Exists(tempFilePath))
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
