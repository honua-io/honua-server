// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.ControlPlane;
using Honua.Core.Configuration;
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
                out var tenantScope,
                out var parseError))
        {
            Log.InvalidSpec(_logger, job.OperationId, parseError);
            return JobExecutionResult.Failed($"Invalid tile-cache job spec: {parseError}") with { IsRetryable = false };
        }

        // Anchor the resumable generation (issue #2661) to the execution job's operation id when
        // the spec did not carry one. Durable retries rerun the SAME operation id (AttemptCount++),
        // so this preserves the generation checkpoint across attempts instead of forking a new one.
        if (string.IsNullOrWhiteSpace(request.GenerationId))
        {
            request = request with { GenerationId = job.OperationId };
        }

        await context.ReportProgressAsync(0, $"Starting {request.Operation}", cancellationToken).ConfigureAwait(false);

        // GET /admin/tile-operations/jobs/{id} reads the tile-shaped progress, not the job record.
        var started = TileOperationProgress.CreateInitial(
            job.OperationId,
            request.Operation,
            request.ServiceId,
            request.LayerId,
            request.TileMatrixSetId) with
        {
            Status = OperationStatus.Processing,
            CurrentPhase = $"Running {request.Operation} operation"
        };
        await _progressStore.SetProgressAsync(job.OperationId, started, ProgressRetention, cancellationToken).ConfigureAwait(false);

        var maxTilesCeiling = string.Equals(job.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
            ? LocalMaxTiles
            : Math.Max(1, _batchOptions.CurrentValue.MaxTiles);
        if (job.AttemptCount <= 1)
        {
            TileOperationMetrics.QueueDepth.Add(-1, new TagList { { "operation", request.Operation } });
        }

        var stopwatch = Stopwatch.StartNew();

        TileOperationProgress finalProgress;
        using var scope = _serviceScopeFactory.CreateScope();
        var schemaContext = scope.ServiceProvider.GetService<SchemaContext>();
        var previousSchema = schemaContext?.CurrentSchema;
        try
        {
            if (!string.IsNullOrWhiteSpace(schemaName) && schemaContext != null)
            {
                schemaContext.CurrentSchema = schemaName;
            }

            var core = new TileOperationExecutionCore(
                _progressStore,
                scope.ServiceProvider.GetRequiredService<OutputCacheInvalidationService>(),
                _tileOptions,
                _limitsOptions,
                _logger,
                maxTilesCeiling,
                scope.ServiceProvider.GetService<ITileCacheGenerationCheckpointStore>());

            finalProgress = await core.ExecuteAsync(
                    started,
                    request,
                    scope.ServiceProvider,
                    cancellationToken,
                    tenantScope)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional broad catch: this is the tile cache job's execution boundary; any failure from
        // the operation core is logged and recorded as a Failed job status below rather than
        // propagated, so the job runner always observes a terminal outcome.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.ExecutionFailed(_logger, job.OperationId, request.Operation, ex);
            finalProgress = started with
            {
                Status = OperationStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "Tile operation failed.",
                CurrentPhase = "Failed"
            };
        }
        finally
        {
            if (schemaContext != null)
            {
                schemaContext.CurrentSchema = previousSchema;
            }
        }

        await _progressStore.SetProgressAsync(job.OperationId, finalProgress, ProgressRetention, cancellationToken)
            .ConfigureAwait(false);
        TileOperationMetrics.JobDurationMs.Record(
            stopwatch.Elapsed.TotalMilliseconds,
            new TagList { { "operation", request.Operation } });
        TileOperationMetrics.JobCount.Add(
            1,
            new TagList
            {
                { "operation", request.Operation },
                { "status", finalProgress.Status.ToString().ToLowerInvariant() }
            });

        var percent = finalProgress.PercentComplete;
        await context.ReportProgressAsync(percent, finalProgress.CurrentPhase, cancellationToken).ConfigureAwait(false);

        if (finalProgress.PublishedArtifact is { } published && !string.IsNullOrWhiteSpace(published.AccessUrl))
        {
            await context.PublishArtifactAsync(published.AccessUrl, cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(finalProgress.DownloadUrl))
        {
            await context.PublishArtifactAsync(finalProgress.DownloadUrl, cancellationToken).ConfigureAwait(false);
        }

        if (finalProgress.Status == OperationStatus.Completed)
        {
            return JobExecutionResult.Succeeded(finalProgress.Warnings);
        }

        // The failure is already reported on the tile progress; an operator retries it explicitly.
        return JobExecutionResult.Failed(
            finalProgress.ErrorMessage ?? "Tile operation failed.",
            finalProgress.Warnings) with { IsRetryable = false };
    }

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
