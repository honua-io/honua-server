// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Bim;
using Honua.Core.Features.Scene.Domain;
using Honua.Core.Features.Scene.PointCloud;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Scene;
using Honua.Scene;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Admin.Scene;

internal sealed partial class SceneJobCoordinator : IDisposable
{
    private const int MaxConcurrentJobs = 4;
    private const int MaxAdmittedJobs = 8;
    private const long MaxAdmittedUploadBytes = 512L * 1024 * 1024;
    private const int MaxRetainedJobs = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SceneJobCoordinator> _logger;
    private readonly SemaphoreSlim _concurrencyGate = new(MaxConcurrentJobs, MaxConcurrentJobs);
    private readonly ConcurrentDictionary<string, SceneJobEntry> _jobs = new(StringComparer.Ordinal);

    private readonly object _admissionLock = new();
    private int _admittedJobs;
    private long _admittedUploadBytes;

    public SceneJobCoordinator(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<SceneJobCoordinator> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Admission? TryReserve(long uploadBytes = 0)
    {
        lock (_admissionLock)
        {
            if (_admittedJobs >= MaxAdmittedJobs ||
                uploadBytes > MaxAdmittedUploadBytes - _admittedUploadBytes)
            {
                return null;
            }

            _admittedJobs++;
            _admittedUploadBytes += uploadBytes;
            return new Admission(this, uploadBytes);
        }
    }

    private SceneJobSnapshot Submit(
        long uploadBytes,
        string sourceKind,
        string? requestedSceneId,
        string? displayName,
        string baseUrl,
        Func<IServiceProvider, CancellationToken, Task<SceneJobExecutionResult>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKind);
        ArgumentNullException.ThrowIfNull(work);

        var jobId = Guid.NewGuid().ToString("N");
        var entry = new SceneJobEntry(
            jobId,
            sourceKind,
            requestedSceneId,
            displayName,
            baseUrl.TrimEnd('/'),
            DateTimeOffset.UtcNow);

        _jobs[jobId] = entry;
        TrimOldTerminalJobs();

        SceneJobCoordinatorLog.JobQueued(_logger, jobId, sourceKind, requestedSceneId ?? "<auto>");

        var snapshot = entry.ToSnapshot();
        _ = Task.Run(() => ExecuteJobAsync(entry, work, uploadBytes), CancellationToken.None);
        return snapshot;
    }

    private void ReleaseAdmission(long uploadBytes)
    {
        lock (_admissionLock)
        {
            _admittedJobs--;
            _admittedUploadBytes -= uploadBytes;
        }
    }

    internal sealed class Admission(SceneJobCoordinator coordinator, long uploadBytes) : IDisposable
    {
        private SceneJobCoordinator? _owner = coordinator;

        internal long UploadBytes { get; private set; } = uploadBytes;

        public bool TryUpdateUploadBytes(long bytes)
        {
            var owner = _owner
                ?? throw new InvalidOperationException("Scene job admission has already been used.");
            lock (owner._admissionLock)
            {
                var increase = bytes - UploadBytes;
                if (increase > MaxAdmittedUploadBytes - owner._admittedUploadBytes)
                {
                    return false;
                }

                owner._admittedUploadBytes += increase;
                UploadBytes = bytes;
                return true;
            }
        }

        public SceneJobSnapshot Submit(
            string sourceKind,
            string? requestedSceneId,
            string? displayName,
            string baseUrl,
            Func<IServiceProvider, CancellationToken, Task<SceneJobExecutionResult>> work)
        {
            var owner = Interlocked.Exchange(ref _owner, null)
                ?? throw new InvalidOperationException("Scene job admission has already been used.");
            try
            {
                return owner.Submit(UploadBytes, sourceKind, requestedSceneId, displayName, baseUrl, work);
            }
            catch
            {
                owner.ReleaseAdmission(UploadBytes);
                throw;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseAdmission(UploadBytes);
    }

    public SceneJobSnapshot? GetJob(string jobId)
        => !string.IsNullOrWhiteSpace(jobId) && _jobs.TryGetValue(jobId, out var entry)
            ? entry.ToSnapshot()
            : null;

    public IReadOnlyList<SceneJobSnapshot> ListJobs(string? sceneId = null, string? status = null)
    {
        var results = new List<SceneJobSnapshot>();
        foreach (var entry in _jobs.Values)
        {
            var snapshot = entry.ToSnapshot();
            if (!string.IsNullOrWhiteSpace(sceneId)
                && !string.Equals(snapshot.SceneId, sceneId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(status)
                && !string.Equals(snapshot.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(snapshot);
        }

        results.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return results;
    }

    public SceneJobLifecycleSummary? GetLatestJobForScene(string sceneId)
    {
        SceneJobSnapshot? latest = null;
        foreach (var entry in _jobs.Values)
        {
            var snapshot = entry.ToSnapshot();
            if (!string.Equals(snapshot.SceneId, sceneId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (latest is null || snapshot.CreatedAt > latest.CreatedAt)
            {
                latest = snapshot;
            }
        }

        return latest is null
            ? null
            : new SceneJobLifecycleSummary(
                latest.JobId,
                latest.SourceKind,
                latest.Status,
                latest.SceneId,
                latest.DisplayName,
                latest.ByteSize,
                latest.FileCount,
                latest.Warnings,
                latest.CreatedAt,
                latest.CompletedAt);
    }

    public IReadOnlyList<SceneJobLifecycleSummary> GetLatestJobsByScene()
    {
        var byScene = new Dictionary<string, SceneJobSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _jobs.Values)
        {
            var snapshot = entry.ToSnapshot();
            if (string.IsNullOrWhiteSpace(snapshot.SceneId))
            {
                continue;
            }

            if (!byScene.TryGetValue(snapshot.SceneId, out var existing) || snapshot.CreatedAt > existing.CreatedAt)
            {
                byScene[snapshot.SceneId] = snapshot;
            }
        }

        return byScene.Values
            .Select(s => new SceneJobLifecycleSummary(
                s.JobId,
                s.SourceKind,
                s.Status,
                s.SceneId,
                s.DisplayName,
                s.ByteSize,
                s.FileCount,
                s.Warnings,
                s.CreatedAt,
                s.CompletedAt))
            .ToArray();
    }

    private async Task ExecuteJobAsync(
        SceneJobEntry entry,
        Func<IServiceProvider, CancellationToken, Task<SceneJobExecutionResult>> work,
        long uploadBytes)
    {
        var token = _lifetime.ApplicationStopping;
        var acquired = false;
        try
        {
            await _concurrencyGate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            entry.MarkRunning();
            SceneJobCoordinatorLog.JobStarted(_logger, entry.JobId, entry.SourceKind);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var result = await work(scope.ServiceProvider, token).ConfigureAwait(false);

            var invalidator = scope.ServiceProvider.GetService<OutputCacheInvalidationService>();
            if (invalidator is not null)
            {
                try
                {
                    await invalidator.InvalidateSceneAsync(result.SceneId, token).ConfigureAwait(false);
                }
                // Best-effort cache invalidation must not mark a completed publish job as failed.
                catch (Exception cacheEx) when (cacheEx is not OutOfMemoryException)
                {
                    SceneJobCoordinatorLog.CacheInvalidationFailed(_logger, result.SceneId, cacheEx);
                }
            }

            entry.MarkSucceeded(result);
            SceneJobCoordinatorLog.JobCompleted(_logger, entry.JobId, entry.SourceKind, result.SceneId);
        }
        catch (OperationCanceledException)
        {
            entry.MarkCancelled();
            SceneJobCoordinatorLog.JobCancelled(_logger, entry.JobId, entry.SourceKind);
        }
        catch (ValidationException vex)
        {
            var code = ExtractErrorCode(vex.Message, SceneGenerationErrorCodes.OptionsInvalid);
            entry.MarkFailed(code, vex.Message);
            SceneJobCoordinatorLog.JobRejected(_logger, entry.JobId, entry.SourceKind, code, vex.Message);
        }
        catch (CityGmlFormatException fex)
        {
            entry.MarkFailed(fex.Code, $"{fex.Code}: {fex.Message}");
            SceneJobCoordinatorLog.JobRejected(_logger, entry.JobId, entry.SourceKind, fex.Code, fex.Message);
        }
        catch (LasFormatException lex)
        {
            entry.MarkFailed(lex.Code, $"{lex.Code}: {lex.Message}");
            SceneJobCoordinatorLog.JobRejected(_logger, entry.JobId, entry.SourceKind, lex.Code, lex.Message);
        }
        catch (SceneOutputUnwritableException ex)
        {
            entry.MarkFailed(ex.Code, ex.Message);
            SceneJobCoordinatorLog.JobFailed(_logger, entry.JobId, entry.SourceKind, ex);
        }
        // Top-level background job boundary: log and record a sanitized failure message without leaking internals.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.MarkFailed("SCENE_JOB_FAILED", "Scene job failed unexpectedly; see server logs for diagnostic detail.");
            SceneJobCoordinatorLog.JobFailed(_logger, entry.JobId, entry.SourceKind, ex);
        }
        finally
        {
            try
            {
                if (acquired)
                {
                    _concurrencyGate.Release();
                }
            }
            finally
            {
                ReleaseAdmission(uploadBytes);
            }
        }
    }

    private void TrimOldTerminalJobs()
    {
        if (_jobs.Count <= MaxRetainedJobs)
        {
            return;
        }

        var terminal = _jobs.Values
            .Select(e => e.ToSnapshot())
            .Where(s => s.Status is SceneJobStatusNames.Succeeded or SceneJobStatusNames.Failed or SceneJobStatusNames.Cancelled)
            .OrderBy(s => s.CreatedAt)
            .Take(_jobs.Count - MaxRetainedJobs)
            .ToArray();

        foreach (var item in terminal)
        {
            _jobs.TryRemove(item.JobId, out _);
        }
    }

    private static string ExtractErrorCode(string message, string fallback)
    {
        var colon = message.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon <= 48)
        {
            var candidate = message[..colon].Trim();
            if (candidate.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                return candidate;
            }
        }

        return fallback;
    }

    public void Dispose() => _concurrencyGate.Dispose();

    private sealed class SceneJobEntry
    {
        private readonly object _gate = new();
        private string _status = SceneJobStatusNames.Queued;
        private string? _sceneId;
        private DateTimeOffset? _startedAt;
        private DateTimeOffset? _completedAt;
        private int? _tileCount;
        private int? _fileCount;
        private long? _byteSize;
        private string[] _warnings = [];
        private string? _errorCode;
        private string? _errorMessage;

        public SceneJobEntry(
            string jobId,
            string sourceKind,
            string? requestedSceneId,
            string? displayName,
            string baseUrl,
            DateTimeOffset createdAt)
        {
            JobId = jobId;
            SourceKind = sourceKind;
            _sceneId = requestedSceneId;
            DisplayName = displayName;
            BaseUrl = baseUrl;
            CreatedAt = createdAt;
        }

        public string JobId { get; }
        public string SourceKind { get; }
        public string? DisplayName { get; }
        public string BaseUrl { get; }
        public DateTimeOffset CreatedAt { get; }

        public void MarkRunning()
        {
            lock (_gate)
            {
                _status = SceneJobStatusNames.Running;
                _startedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkSucceeded(SceneJobExecutionResult result)
        {
            lock (_gate)
            {
                _status = SceneJobStatusNames.Succeeded;
                _sceneId = result.SceneId;
                _tileCount = result.TileCount;
                _fileCount = result.FileCount;
                _byteSize = result.ByteSize;
                _warnings = result.Warnings.ToArray();
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkFailed(string errorCode, string errorMessage)
        {
            lock (_gate)
            {
                _status = SceneJobStatusNames.Failed;
                _errorCode = errorCode;
                _errorMessage = errorMessage;
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        public void MarkCancelled()
        {
            lock (_gate)
            {
                _status = SceneJobStatusNames.Cancelled;
                _completedAt = DateTimeOffset.UtcNow;
            }
        }

        public SceneJobSnapshot ToSnapshot()
        {
            lock (_gate)
            {
                string? tilesetUrl = null;
                string? i3sUrl = null;
                if (_status == SceneJobStatusNames.Succeeded && !string.IsNullOrWhiteSpace(_sceneId))
                {
                    tilesetUrl = SceneUrls.AbsoluteTilesetUrl(BaseUrl, _sceneId);
                    i3sUrl = string.Concat(BaseUrl, "/rest/services/", Uri.EscapeDataString(_sceneId), "/SceneServer");
                }

                return new SceneJobSnapshot(
                    JobId: JobId,
                    SourceKind: SourceKind,
                    Status: _status,
                    SceneId: _sceneId,
                    DisplayName: DisplayName,
                    StatusUrl: $"/api/v1/admin/scenes/jobs/{JobId}",
                    TilesetUrl: tilesetUrl,
                    I3sUrl: i3sUrl,
                    TileCount: _tileCount,
                    FileCount: _fileCount,
                    ByteSize: _byteSize,
                    Warnings: _warnings,
                    ErrorCode: _errorCode,
                    ErrorMessage: _errorMessage,
                    CreatedAt: CreatedAt,
                    StartedAt: _startedAt,
                    CompletedAt: _completedAt);
            }
        }
    }

    internal static partial class SceneJobCoordinatorLog
    {
        [LoggerMessage(EventId = 8510, Level = LogLevel.Information,
            Message = "Scene async job queued: job {JobId}, sourceKind {SourceKind}, scene {SceneId}")]
        public static partial void JobQueued(ILogger logger, string jobId, string sourceKind, string sceneId);

        [LoggerMessage(EventId = 8511, Level = LogLevel.Information,
            Message = "Scene async job started: job {JobId}, sourceKind {SourceKind}")]
        public static partial void JobStarted(ILogger logger, string jobId, string sourceKind);

        [LoggerMessage(EventId = 8512, Level = LogLevel.Information,
            Message = "Scene async job completed: job {JobId}, sourceKind {SourceKind}, scene {SceneId}")]
        public static partial void JobCompleted(ILogger logger, string jobId, string sourceKind, string sceneId);

        [LoggerMessage(EventId = 8513, Level = LogLevel.Warning,
            Message = "Scene async job rejected: job {JobId}, sourceKind {SourceKind}, code {Code}, reason {Reason}")]
        public static partial void JobRejected(ILogger logger, string jobId, string sourceKind, string code, string reason);

        [LoggerMessage(EventId = 8514, Level = LogLevel.Error,
            Message = "Scene async job failed: job {JobId}, sourceKind {SourceKind}")]
        public static partial void JobFailed(ILogger logger, string jobId, string sourceKind, Exception exception);

        [LoggerMessage(EventId = 8515, Level = LogLevel.Warning,
            Message = "Scene async job cancelled: job {JobId}, sourceKind {SourceKind}")]
        public static partial void JobCancelled(ILogger logger, string jobId, string sourceKind);

        [LoggerMessage(EventId = 8516, Level = LogLevel.Warning,
            Message = "Scene async job cache invalidation failed for scene {SceneId}.")]
        public static partial void CacheInvalidationFailed(ILogger logger, string sceneId, Exception exception);
    }
}

internal sealed record SceneJobExecutionResult(
    string SceneId,
    int? TileCount,
    int? FileCount,
    long? ByteSize,
    IReadOnlyList<string> Warnings);

internal sealed record SceneJobSnapshot(
    string JobId,
    string SourceKind,
    string Status,
    string? SceneId,
    string? DisplayName,
    string StatusUrl,
    string? TilesetUrl,
    string? I3sUrl,
    int? TileCount,
    int? FileCount,
    long? ByteSize,
    string[] Warnings,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);
