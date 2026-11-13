// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ControlPlane;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.TileOperations;

internal sealed partial class TileCacheJobExecutor : IJobExecutor
{
    // The local backend runs the job inside a serving process, so it keeps that process's tile ceiling;
    // a remote backend runs on dedicated compute and is bounded by TileCacheBatchOptions.MaxTiles.
    internal const int LocalMaxTiles = 5_000;

    private readonly IUniversalProgressStore _progressStore;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<TileOptions> _tileOptions;
    private readonly IOptions<LimitsOptions> _limitsOptions;
    private readonly IOptionsMonitor<TileCacheBatchOptions> _batchOptions;
    private readonly ILogger<TileCacheJobExecutor> _logger;

    private static readonly TimeSpan ProgressRetention = TimeSpan.FromHours(24);

    public TileCacheJobExecutor(
        IUniversalProgressStore progressStore,
        IServiceScopeFactory serviceScopeFactory,
        IOptions<TileOptions> tileOptions,
        IOptions<LimitsOptions> limitsOptions,
        IOptionsMonitor<TileCacheBatchOptions> batchOptions,
        ILogger<TileCacheJobExecutor> logger)
    {
        _progressStore = progressStore ?? throw new ArgumentNullException(nameof(progressStore));
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _tileOptions = tileOptions ?? throw new ArgumentNullException(nameof(tileOptions));
        _limitsOptions = limitsOptions ?? throw new ArgumentNullException(nameof(limitsOptions));
        _batchOptions = batchOptions ?? throw new ArgumentNullException(nameof(batchOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ExecutionJobKind Kind => ExecutionJobKind.TileCache;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        if (!TileCacheExecutionSpecBuilder.TryParse(
                job.Spec.Parameters,
                out var request,
                out var schemaName,
                out var parseError))
        {
            Log.InvalidSpec(_logger, job.OperationId, parseError);
            return JobExecutionResult.Failed($"Invalid tile-cache job spec: {parseError}") with
            { IsRetryable = false };
        }

        // A spec without a generation id resumes under the operation id, which durable retries keep,
        // so a retried attempt continues the same generation checkpoint.
        if (string.IsNullOrWhiteSpace(request.GenerationId))
        {
            request = request with
            { GenerationId = job.OperationId };
        }

        var run = new TileCacheRun(job, request, schemaName);
        var started = await StartAsync(run, context, cancellationToken).ConfigureAwait(false);
        var finalProgress = await RunAsync(run, started, cancellationToken).ConfigureAwait(false);
        await _progressStore.SetProgressAsync(job.OperationId, finalProgress, ProgressRetention, cancellationToken)
            .ConfigureAwait(false);
        await ReportAsync(finalProgress, context, cancellationToken).ConfigureAwait(false);

        // A failure is already reported on the tile progress; an operator retries it explicitly.
        return finalProgress.Status == OperationStatus.Completed
            ? JobExecutionResult.Succeeded(finalProgress.Warnings)
            : JobExecutionResult.Failed(finalProgress.ErrorMessage ?? "Tile operation failed.", finalProgress.Warnings)
                with
                { IsRetryable = false };
    }

    // GET /admin/tile-operations/jobs/{id} reads the tile-shaped progress, not the job record.
    private async Task<TileOperationProgress> StartAsync(
        TileCacheRun run,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var request = run.Request;
        await context.ReportProgressAsync(0, $"Starting {request.Operation}", cancellationToken).ConfigureAwait(false);
        var started = TileOperationProgress.CreateInitial(
            run.Job.OperationId,
            request.Operation,
            request.ServiceId,
            request.LayerId,
            request.TileMatrixSetId) with
        {
            Status = OperationStatus.Processing,
            CurrentPhase = $"Running {request.Operation} operation"
        };
        await _progressStore.SetProgressAsync(run.Job.OperationId, started, ProgressRetention, cancellationToken).ConfigureAwait(false);
        return started;
    }

    private async Task<TileOperationProgress> RunAsync(
        TileCacheRun run,
        TileOperationProgress started,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        TileOperationProgress finalProgress;
        try
        {
            finalProgress = await RunCoreAsync(run, started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // The job's execution boundary: a failure becomes a Failed progress, so the worker always
        // observes a terminal outcome.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ExecutionFailed(_logger, run.Job.OperationId, run.Request.Operation, ex);
            finalProgress = started with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Tile operation failed.",
                CurrentPhase = "Failed"
            };
        }

        var operation = new TagList { { "operation", run.Request.Operation } };
        TileOperationMetrics.JobDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, operation);
        operation.Add("status", finalProgress.Status.ToString().ToLowerInvariant());
        TileOperationMetrics.JobCount.Add(1, operation);
        return finalProgress;
    }

    private async Task<TileOperationProgress> RunCoreAsync(
        TileCacheRun run,
        TileOperationProgress started,
        CancellationToken cancellationToken)
    {
        // Generated tiles are served to every reader of the layer, so the layer is read as the system.
        using var systemRead = JobSecurityScope.BeginSystem();
        using var scope = _serviceScopeFactory.CreateScope();
        var schemaContext = scope.ServiceProvider.GetService<SchemaContext>();
        var previousSchema = schemaContext?.CurrentSchema;
        try
        {
            if (!string.IsNullOrWhiteSpace(run.SchemaName) && schemaContext != null)
            {
                schemaContext.CurrentSchema = run.SchemaName;
            }

            var core = new TileOperationExecutionCore(
                _progressStore,
                scope.ServiceProvider.GetRequiredService<OutputCacheInvalidationService>(),
                _tileOptions,
                _limitsOptions,
                _logger,
                MaxTilesFor(run.Job),
                scope.ServiceProvider.GetService<ITileCacheGenerationCheckpointStore>());
            return await core.ExecuteAsync(started, run.Request, scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (schemaContext != null)
            {
                schemaContext.CurrentSchema = previousSchema;
            }
        }
    }

    private int MaxTilesFor(ExecutionJobRecord job)
        => string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            ? LocalMaxTiles
            : Math.Max(1, _batchOptions.CurrentValue.MaxTiles);

    private static async Task ReportAsync(
        TileOperationProgress finalProgress,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(finalProgress.PercentComplete, finalProgress.CurrentPhase, cancellationToken)
            .ConfigureAwait(false);
        var accessUrl = finalProgress.PublishedArtifact?.AccessUrl;
        var artifact = string.IsNullOrWhiteSpace(accessUrl) ? finalProgress.DownloadUrl : accessUrl;
        if (!string.IsNullOrWhiteSpace(artifact))
        {
            await context.PublishArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record TileCacheRun(
        ExecutionJobRecord Job,
        TileOperationStartRequest Request,
        string? SchemaName);

    private static partial class Log
    {
        [LoggerMessage(9210, LogLevel.Warning,
            "Tile-cache executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidSpec(ILogger logger, string operationId, string reason);

        [LoggerMessage(9211, LogLevel.Error,
            "Tile-cache executor failed job {OperationId} during {Operation}")]
        public static partial void ExecutionFailed(ILogger logger, string operationId, string operation, Exception exception);
    }
}
