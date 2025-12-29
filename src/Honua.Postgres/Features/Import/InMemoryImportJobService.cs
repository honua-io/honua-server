// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// In-memory implementation of import job service for background processing.
/// Uses Channel-based queue and background tasks for processing large files.
/// For production use with persistent storage, consider using a distributed job queue.
/// </summary>
internal sealed class InMemoryImportJobService : IImportJobService, IDisposable
{
    private readonly IFileImportService _importService;
    private readonly ConcurrentDictionary<string, ImportJobState> _jobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private bool _disposed;

    public InMemoryImportJobService(IFileImportService importService)
    {
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
    }

    /// <inheritdoc/>
    public async Task<string> QueueImportAsync(
        ImportRequest request,
        long fileSize,
        CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var format = _importService.DetectFormat(request.FileName) ?? SupportedFileFormat.GeoJson;

        var progress = ImportProgress.CreateInitial(jobId, request.TableName, format, fileSize);
        var state = new ImportJobState
        {
            Progress = progress,
            Request = request,
            StartedAt = DateTimeOffset.UtcNow
        };

        _jobs[jobId] = state;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationTokens[jobId] = cts;

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
        try
        {
            await using var stream = request.FileStream;
            if (_jobs.TryGetValue(jobId, out var state))
            {
                state.Progress = state.Progress with { Status = ImportStatus.Processing };
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
                state.Progress = state.Progress with
                {
                    Status = result.Success ? ImportStatus.Completed : ImportStatus.Failed,
                    FeaturesProcessed = result.FeatureCount,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = result.ErrorMessage
                };
                state.Result = result;
            }
        }
        catch (OperationCanceledException)
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                state.Progress = state.Progress with
                {
                    Status = ImportStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            if (_jobs.TryGetValue(jobId, out var state))
            {
                state.Progress = state.Progress with
                {
                    Status = ImportStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message
                };
            }
        }
        finally
        {
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

    private sealed class ImportJobState
    {
        public required ImportProgress Progress { get; set; }
        public required ImportRequest Request { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
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
