// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Worker-side <see cref="IJobExecutor"/> for <see cref="ExecutionJobKind.TileCache"/>
/// jobs (issue #1697). Decodes the tile-cache parameters that
/// <see cref="TileCacheJobService"/> stamped onto the execution-job spec, runs the
/// requested seed/warm/invalidate/purge/archive/publish operation through the shared
/// <see cref="TileOperationExecutionCore"/>, and bridges the resulting
/// <see cref="TileOperationProgress"/> into both the canonical execution-job progress
/// channel (<see cref="IJobExecutionContext"/>) and the
/// <see cref="IUniversalProgressStore"/> the existing admin job-status API reads, so a
/// batch-dispatched job is observable through the same endpoints as an in-process job.
/// <para>
/// On the local backend this runs in-process via <c>JobExecutionService</c>; on AWS
/// Batch / Kubernetes the same executor runs inside the GDAL/tippecanoe worker image.
/// The 5,000-tile cap that protects the serving pod is lifted here to
/// <see cref="TileCacheBatchOptions.MaxTiles"/> because the work runs on dedicated
/// compute bounded by backend timeout/resources.
/// </para>
/// </summary>
internal sealed partial class TileCacheJobExecutor : IJobExecutor
{
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
            return JobExecutionResult.Failed($"Invalid tile-cache job spec: {parseError}");
        }

        // Anchor the resumable generation (issue #2661) to the execution job's operation id when
        // the spec did not carry one. Durable retries rerun the SAME operation id (AttemptCount++),
        // so this preserves the generation checkpoint across attempts instead of forking a new one.
        if (string.IsNullOrWhiteSpace(request.GenerationId))
        {
            request = request with { GenerationId = job.OperationId };
        }

        await context.ReportProgressAsync(0, $"Starting {request.Operation}", cancellationToken).ConfigureAwait(false);

        // Seed a TileOperationProgress so GET /admin/tile-operations/jobs/{id} returns
        // the rich tile-shaped status for batch jobs exactly as for in-process jobs.
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

        var maxTilesCeiling = Math.Max(1, _batchOptions.CurrentValue.MaxTiles);

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

            finalProgress = await core.ExecuteAsync(started, request, scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional broad catch: this is the tile cache job's execution boundary; any failure from
        // the operation core is logged and recorded as a Failed job status below rather than
        // propagated, so the job runner always observes a terminal outcome.
        catch (Exception ex)
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

        return JobExecutionResult.Failed(
            finalProgress.ErrorMessage ?? "Tile operation failed.",
            finalProgress.Warnings);
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
