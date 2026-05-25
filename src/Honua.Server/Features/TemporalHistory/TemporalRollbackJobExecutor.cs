// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.TemporalHistory.Abstractions;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.TemporalHistory;

/// <summary>
/// Job-runner executor that applies an approved temporal rollback as a forward corrective operation.
/// The corrective write appends INSERT/UPDATE/DELETE revisions and stamps a new checkpoint; immutable
/// history is preserved (no rows are deleted). Registered per <see cref="ExecutionJobKind"/> and invoked
/// by the worker host when a rollback job is claimed.
/// </summary>
internal sealed class TemporalRollbackJobExecutor : IJobExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TemporalRollbackJobExecutor> _logger;

    public TemporalRollbackJobExecutor(IServiceScopeFactory scopeFactory, ILogger<TemporalRollbackJobExecutor> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ExecutionJobKind Kind => ExecutionJobKind.TemporalRollback;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        if (!parameters.TryGetValue(ExecutionJobParameterKeys.TemporalRollbackLayerId, out var layerIdText)
            || !int.TryParse(layerIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
        {
            return JobExecutionResult.Failed("Temporal rollback job is missing a valid layer identifier.");
        }

        if (!parameters.TryGetValue(ExecutionJobParameterKeys.TemporalRollbackToCursor, out var toText)
            || !TemporalCursor.TryParse(toText, out var toCursor))
        {
            return JobExecutionResult.Failed("Temporal rollback job is missing a valid target cursor.");
        }

        parameters.TryGetValue(ExecutionJobParameterKeys.TemporalRollbackReason, out var reason);

        try
        {
            await context.ReportProgressAsync(10, "Resolving layer", cancellationToken).ConfigureAwait(false);

            using var scope = _scopeFactory.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ILayerCatalog>();
            var source = scope.ServiceProvider.GetRequiredService<ITemporalHistorySource>();

            var layer = await catalog.GetLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (layer is null)
            {
                return JobExecutionResult.Failed("The target layer was not found.");
            }

            await context.ReportProgressAsync(40, "Applying corrective operation", cancellationToken).ConfigureAwait(false);

            var rollbackContext = new TemporalRollbackContext
            {
                JobId = job.OperationId,
                Actor = job.Audit.RequestedBy,
                Reason = string.IsNullOrEmpty(reason) ? null : reason,
                CorrelationId = job.Audit.CorrelationId ?? job.OperationId
            };

            var result = await source.ExecuteRollbackAsync(layer, toCursor, rollbackContext, cancellationToken).ConfigureAwait(false);

            await context.PublishArtifactAsync($"temporal-checkpoint:{result.Checkpoint}", cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Completed", cancellationToken).ConfigureAwait(false);

            TemporalRollbackJobLog.Applied(_logger, job.OperationId, layerId, result.AppliedCount, result.Checkpoint);
            return JobExecutionResult.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TemporalHistoryException ex)
        {
            TemporalRollbackJobLog.Rejected(_logger, job.OperationId, ex.Message);
            return JobExecutionResult.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            TemporalRollbackJobLog.Failed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed("Temporal rollback failed.");
        }
    }
}

/// <summary>
/// Source-generated structured logging for the temporal rollback job executor.
/// </summary>
internal static partial class TemporalRollbackJobLog
{
    [LoggerMessage(EventId = 7120, Level = LogLevel.Information,
        Message = "temporal.rollback.applied: jobId={JobId} layerId={LayerId} applied={AppliedCount} checkpoint={Checkpoint}")]
    public static partial void Applied(ILogger logger, string jobId, int layerId, int appliedCount, string checkpoint);

    [LoggerMessage(EventId = 7121, Level = LogLevel.Warning,
        Message = "temporal.rollback.rejected: jobId={JobId} reason={Reason}")]
    public static partial void Rejected(ILogger logger, string jobId, string reason);

    [LoggerMessage(EventId = 7122, Level = LogLevel.Error, Message = "temporal.rollback.failed: jobId={JobId}")]
    public static partial void Failed(ILogger logger, string jobId, Exception exception);
}
