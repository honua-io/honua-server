// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Import job service that uses the unified progress store for progress tracking.
/// Provides background job processing with centralized progress management.
/// </summary>
internal sealed partial class UniversalImportJobService : IImportJobService, IDisposable
{
    // PA-016: this is the DI-registered IImportJobService (see ServiceCollectionExtensions.cs) and
    // its ProcessJobAsync is invoked fire-and-forget (`_ = ProcessJobAsync(...)`), so it needs its
    // own ActivitySource plus the detach-from-caller idiom at the top of ProcessJobAsync. Registered
    // as "Honua.Import.Jobs" in Honua.ServiceDefaults.Extensions._activitySourceNames.
    private static readonly ActivitySource ActivitySource = new("Honua.Import.Jobs", "1.0.0");

    private const string SafeImportFailureMessage = "Import failed.";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUniversalProgressStore _progressStore;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly ILogger<UniversalImportJobService> _logger;
    private readonly ConcurrentDictionary<string, ImportJobState> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private bool _disposed;

    public UniversalImportJobService(
        IServiceScopeFactory scopeFactory,
        IUniversalProgressStore progressStore,
        IPerformanceMonitor performanceMonitor,
        ILogger<UniversalImportJobService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<string> QueueImportAsync(
        ImportRequest request,
        long fileSize,
        CancellationToken cancellationToken = default)
    {
        request.Validate();

        SupportedFileFormat format;
        using (var scope = _scopeFactory.CreateScope())
        {
            var importService = scope.ServiceProvider.GetRequiredService<IFileImportService>();
            format = ImportFormatDispatchGuard.RequireGatedFormat(
                importService.DetectFormat(request.FileName), request.FileName);
        }
        var formatName = format.ToString();

        // A full 128-bit Guid makes collisions unrealistic (the store write is a plain set, not
        // add-if-absent, so a colliding id would silently overwrite another job's progress).
        var jobId = Guid.NewGuid().ToString("N");
        var initialProgress = ImportProgress.CreateInitial(
            jobId,
            request.TableName,
            format,
            fileSize,
            request.FileName,
            request.SourceKind,
            request.SourceUrl,
            request.CloudFileId,
            request.UploadId);

        // Store initial progress in the unified store. The write is retried a bounded number of
        // times for transient store failures; cancellation propagates and an unreachable store
        // fails the queue call instead of spinning (and log-flooding) forever.
        const int MaxQueueAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _progressStore.SetProgressAsync(jobId, initialProgress, TimeSpan.FromDays(1), cancellationToken);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                UniversalImportJobLog.ProgressUpdateFailed(_logger, jobId, ex);
                if (attempt >= MaxQueueAttempts)
                {
                    throw;
                }
            }
        }

        var state = new ImportJobState
        {
            TableName = request.TableName,
            StartedAt = DateTimeOffset.UtcNow,
            FileSize = fileSize,
            Format = formatName
        };

        _jobs[jobId] = state;

        var cts = new CancellationTokenSource();
        _cancellationTokens[jobId] = cts;

        UniversalImportJobLog.JobQueued(_logger, jobId, request.TableName, formatName, fileSize);
        RecordJobMetrics("queued", formatName, fileSize, null, null, null);

        string? tempFilePath = null;
        var backgroundRequest = request;

        if (request.UsesLocalFile)
        {
            tempFilePath = request.LocalFilePath;
        }
        else if (!request.UsesCloudStorage)
        {
            // Copy stream to a temp file for background processing
            if (request.FileStream!.CanSeek)
            {
                request.FileStream.Position = 0;
            }

            // Path.Combine's second argument is a server-generated file name built from a
            // Guid.NewGuid() job id and a fresh Guid.NewGuid() suffix, so it can never be
            // rooted/absolute; Path.Combine cannot silently drop the temp directory here.
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
                // The background job will never start: clean up the bookkeeping created above and
                // best-effort mark the stored Queued progress as failed so it does not linger in
                // GetActiveJobsAsync for the full TTL with no job behind it.
                TryDeleteTempFile(tempFilePath);
                _jobs.TryRemove(jobId, out _);
                _cancellationTokens.TryRemove(jobId, out _);
                cts.Dispose();
                await TryMarkProgressFailedAsync(jobId);
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
        // PA-016: fire-and-forget (`_ = ProcessJobAsync(...)`) off the request handler. The
        // continuation still flows the caller's ExecutionContext, so Activity.Current here would
        // otherwise be the HTTP request's activity — which ends when the response returns, long
        // before this background job finishes. Capture it as a link instead of an implicit
        // parent. Passing default(ActivityContext) alone does NOT force a root span — the
        // ActivitySource falls back to Activity.Current when the context is the all-zero/invalid
        // value — so null out the ambient activity first for a fresh trace.
        var callerContext = Activity.Current?.Context;
        var links = callerContext is { } ctx ? new[] { new ActivityLink(ctx) } : null;
        Activity.Current = null;
        using var activity = ActivitySource.StartActivity(
            "ImportJob.Process", ActivityKind.Internal, default(ActivityContext), links: links);
        activity?.SetTag("honua.import.job_id", jobId);
        activity?.SetTag("honua.import.table_name", request.TableName);

        var stopwatch = Stopwatch.StartNew();
        var status = "failed";
        int? featureCount = null;
        int? failedFeatures = null;
        string? errorMessage = null;
        var stream = request.FileStream;
        SerializedImportProgressWriter? progressWriter = null;

        try
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                activity?.SetTag("honua.import.format", state.Format);
                var currentProgress = await _progressStore.GetProgressAsync<ImportProgress>(jobId, cancellationToken);
                if (currentProgress != null)
                {
                    var updatedProgress = currentProgress with { Status = ImportStatus.Processing };
                    await _progressStore.SetProgressAsync(jobId, updatedProgress, TimeSpan.FromDays(1), cancellationToken);
                }

                UniversalImportJobLog.JobStarted(_logger, jobId, state.TableName, state.Format, state.FileSize);
            }

            progressWriter = new SerializedImportProgressWriter(_progressStore, _logger, jobId);
            var progress = new Progress<ImportProgress>(p => progressWriter.Report(p with { JobId = jobId }));

            using var scope = _scopeFactory.CreateScope();
            var importService = scope.ServiceProvider.GetRequiredService<IFileImportService>();
            var result = await importService.ImportFileAsync(request, progress, cancellationToken);

            if (_jobs.TryGetValue(jobId, out state))
            {
                status = result.Success ? "completed" : "failed";
                featureCount = result.FeatureCount;
                errorMessage = result.ErrorMessage;

                await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
                var currentProgress = await _progressStore.GetProgressAsync<ImportProgress>(jobId, cancellationToken);
                failedFeatures = currentProgress?.FailedFeatures ?? 0;
                if (currentProgress != null)
                {
                    var clientErrorMessage = result.Success ? result.ErrorMessage : SafeImportFailureMessage;
                    var finalProgress = currentProgress with
                    {
                        Status = result.Success ? ImportStatus.Completed : ImportStatus.Failed,
                        FeaturesProcessed = result.FeatureCount,
                        CompletedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = clientErrorMessage,
                        Warnings = result.Warnings,
                        DetectedSrid = result.DetectedSrid,
                        CurrentPhase = result.Success ? "Import completed" : "Import failed"
                    };
                    await _progressStore.SetProgressAsync(jobId, finalProgress, TimeSpan.FromDays(1), cancellationToken);
                }

                if (result.Success)
                {
                    UniversalImportJobLog.JobCompleted(_logger, jobId, state.TableName, state.Format, state.FileSize,
                        result.FeatureCount, failedFeatures ?? 0, stopwatch.Elapsed.TotalMilliseconds);
                }
                else
                {
                    UniversalImportJobLog.JobFailed(_logger, jobId, state.TableName, state.Format, state.FileSize,
                        errorMessage ?? "Import failed", stopwatch.Elapsed.TotalMilliseconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("honua.import.outcome", "cancelled");
            if (_jobs.TryGetValue(jobId, out var state))
            {
                status = "cancelled";
                if (progressWriter != null)
                {
                    await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
                }

                var currentProgress = await _progressStore.GetProgressAsync<ImportProgress>(jobId, CancellationToken.None);
                failedFeatures = currentProgress?.FailedFeatures;
                if (currentProgress != null)
                {
                    var cancelledProgress = currentProgress with
                    {
                        Status = ImportStatus.Cancelled,
                        CompletedAt = DateTimeOffset.UtcNow
                    };
                    await _progressStore.SetProgressAsync(jobId, cancelledProgress, TimeSpan.FromDays(1), CancellationToken.None);
                }

                UniversalImportJobLog.JobCancelled(_logger, jobId, state.TableName, state.Format, state.FileSize,
                    stopwatch.Elapsed.TotalMilliseconds);
            }
        }
        // Intentional broad catch: ProcessJobAsync runs fire-and-forget with no caller to
        // observe a thrown exception, so any import-provider failure must be caught here,
        // logged, and recorded on the job's progress rather than becoming an unobserved task
        // exception.
        catch (Exception ex)
        {
            activity?.SetTag("honua.import.outcome", "failed");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            if (_jobs.TryGetValue(jobId, out var state))
            {
                status = "failed";
                if (progressWriter != null)
                {
                    await progressWriter.CloseAndDrainAsync().ConfigureAwait(false);
                }

                var currentProgress = await _progressStore.GetProgressAsync<ImportProgress>(jobId, CancellationToken.None);
                failedFeatures = currentProgress?.FailedFeatures;
                if (currentProgress != null)
                {
                    var failedProgress = currentProgress with
                    {
                        Status = ImportStatus.Failed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        ErrorMessage = SafeImportFailureMessage
                    };
                    await _progressStore.SetProgressAsync(jobId, failedProgress, TimeSpan.FromDays(1), CancellationToken.None);
                }

                UniversalImportJobLog.JobException(_logger, jobId, ex);
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
            if (_cancellationTokens.TryRemove(jobId, out var cts))
            {
                using var _ = cts;
            }

            _jobs.TryRemove(jobId, out _);
        }
    }

    /// <inheritdoc/>
    public async Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return await _progressStore.GetProgressAsync<ImportProgress>(jobId, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_cancellationTokens.TryGetValue(jobId, out var cts) && !cts.IsCancellationRequested)
        {
            try
            {
                cts.Cancel();
                return Task.FromResult(true);
            }
            catch (ObjectDisposedException)
            {
                // The job completed and disposed its CTS between the lookup and Cancel();
                // report "nothing to cancel" instead of surfacing the race as a 500.
            }
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ImportProgress>> GetActiveJobsAsync(CancellationToken cancellationToken = default)
    {
        return await _progressStore.GetActiveOperationsAsync<ImportProgress>(OperationType.Import, cancellationToken);
    }

    // Best-effort transition of a stored Queued progress record to Failed when the job can no
    // longer run (e.g. staging the upload to a temp file failed before background processing
    // started). Failures here are logged, never thrown: the caller is already propagating the
    // original error.
    private async Task TryMarkProgressFailedAsync(string jobId)
    {
        try
        {
            var current = await _progressStore.GetProgressAsync<ImportProgress>(jobId, CancellationToken.None);
            if (current is not null)
            {
                var failed = current with
                {
                    Status = ImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = SafeImportFailureMessage
                };
                await _progressStore.SetProgressAsync(jobId, failed, TimeSpan.FromDays(1), CancellationToken.None);
            }
        }
        // Intentional broad catch: this is the best-effort helper described above — failures
        // are logged and swallowed by design, never rethrown to the caller.
        catch (Exception ex)
        {
            UniversalImportJobLog.ProgressUpdateFailed(_logger, jobId, ex);
        }
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

    private static partial class UniversalImportJobLog
    {
        [LoggerMessage(
            EventId = 7420,
            Level = LogLevel.Information,
            Message = "Universal import job queued {JobId} table={TableName} format={Format} bytes={Bytes}")]
        public static partial void JobQueued(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes);

        [LoggerMessage(
            EventId = 7421,
            Level = LogLevel.Information,
            Message = "Universal import job started {JobId} table={TableName} format={Format} bytes={Bytes}")]
        public static partial void JobStarted(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes);

        [LoggerMessage(
            EventId = 7422,
            Level = LogLevel.Information,
            Message = "Universal import job completed {JobId} table={TableName} format={Format} bytes={Bytes} imported={Imported} failed={Failed} durationMs={DurationMs:F2}")]
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
            EventId = 7423,
            Level = LogLevel.Warning,
            Message = "Universal import job cancelled {JobId} table={TableName} format={Format} bytes={Bytes} durationMs={DurationMs:F2}")]
        public static partial void JobCancelled(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes,
            double durationMs);

        [LoggerMessage(
            EventId = 7424,
            Level = LogLevel.Error,
            Message = "Universal import job failed {JobId} table={TableName} format={Format} bytes={Bytes} error={ErrorMessage} durationMs={DurationMs:F2}")]
        public static partial void JobFailed(
            ILogger logger,
            string jobId,
            string tableName,
            string format,
            long bytes,
            string errorMessage,
            double durationMs);

        [LoggerMessage(
            EventId = 7425,
            Level = LogLevel.Error,
            Message = "Universal import job {JobId} threw exception")]
        public static partial void JobException(ILogger logger, string jobId, Exception exception);

        [LoggerMessage(
            EventId = 7426,
            Level = LogLevel.Warning,
            Message = "Failed to update progress for universal import job {JobId}")]
        public static partial void ProgressUpdateFailed(ILogger logger, string jobId, Exception exception);
    }

    private sealed class ImportJobState
    {
        public required string TableName { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public required long FileSize { get; init; }
        public required string Format { get; init; }
    }

    private sealed class SerializedImportProgressWriter
    {
        private readonly IUniversalProgressStore _progressStore;
        private readonly ILogger<UniversalImportJobService> _logger;
        private readonly string _jobId;
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;
        private bool _closed;

        public SerializedImportProgressWriter(
            IUniversalProgressStore progressStore,
            ILogger<UniversalImportJobService> logger,
            string jobId)
        {
            _progressStore = progressStore;
            _logger = logger;
            _jobId = jobId;
        }

        public void Report(ImportProgress progress)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                var previous = _tail;
                _tail = StoreAfterPreviousAsync(previous, progress);
            }
        }

        public Task CloseAndDrainAsync()
        {
            lock (_gate)
            {
                _closed = true;
                return _tail;
            }
        }

        private async Task StoreAfterPreviousAsync(Task previous, ImportProgress progress)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // StoreAsync logs and swallows failures. This guard prevents one
                // unexpected fault from breaking the serialized progress chain.
            }

            await StoreAsync(progress).ConfigureAwait(false);
        }

        private async Task StoreAsync(ImportProgress progress)
        {
            try
            {
                await _progressStore.SetProgressAsync(
                    _jobId,
                    progress,
                    TimeSpan.FromDays(1),
                    CancellationToken.None).ConfigureAwait(false);
            }
            // Intentional broad catch: this backs the serialized progress-writer chain (see
            // StoreAfterPreviousAsync above) — a progress-store fault must be logged and
            // swallowed here so it cannot break the chain or surface as an unobserved task
            // exception on a fire-and-forget progress update.
            catch (Exception ex)
            {
                UniversalImportJobLog.ProgressUpdateFailed(_logger, _jobId, ex);
            }
        }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; ignore failures (e.g. file locked/in use, already removed,
            // or the process lacks delete permission on the temp path).
        }
    }
}
