// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing.Execution;

/// <summary>Creates a new schema-preserving layer through the canonical provider copy capability.</summary>
internal sealed partial class CopyFeaturesExecutor(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<GeoprocessingExecutorOptions> options,
    ILogger<CopyFeaturesExecutor> logger) : IProcessExecutor
{
    internal const string HandledProcessId = "data-management.copy-features";
    public IReadOnlySet<string> ProcessIds { get; } = new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters) != HandledProcessId)
        {
            return JobExecutionResult.Failed("Unsupported process for copy-features.");
        }
        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGet("sourceLayerId", out var rawId) || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceId) || sourceId < 0
            || !inputs.TryGet("targetLayerName", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return JobExecutionResult.Failed("copy-features requires a nonnegative sourceLayerId and a nonempty targetLayerName.");
        }
        ImmutableArray<long>? ids = null;
        if (inputs.TryGet("objectIds", out var rawIds) && !string.IsNullOrWhiteSpace(rawIds))
        {
            var parsed = ImmutableArray.CreateBuilder<long>();
            foreach (var value in rawIds.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    return JobExecutionResult.Failed("copy-features objectIds must be comma-separated integer identifiers.");
                }
                parsed.Add(id);
            }
            ids = parsed.ToImmutable();
        }
        try
        {
            using var scope = scopeFactory.CreateScope();
            var copier = scope.ServiceProvider.GetRequiredService<IFeatureLayerCopyService>();
            var result = await copier.CopyAsync(sourceId, name.Trim(), new FeatureQuery
            {
                Where = inputs.TryGet("where", out var where) ? where : null,
                ObjectIds = ids,
                IncludeZ = true,
                IncludeM = true
            }, job.OperationId, options.CurrentValue.MaxArtifactBytes, cancellationToken).ConfigureAwait(false);
            await context.PublishArtifactAsync(SinkResultArtifact.Build(HandledProcessId,
                ("sourceLayerId", sourceId), ("layerId", result.LayerId), ("featureCount", result.FeatureCount),
                ("srid", result.Srid), ("operationId", job.OperationId)), cancellationToken).ConfigureAwait(false);
            return JobExecutionResult.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.CopyFailed(logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"copy-features failed: {ex.GetType().Name}.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9480, LogLevel.Error, "Feature copy failed for job {OperationId}")]
        public static partial void CopyFailed(ILogger logger, string operationId, Exception exception);
    }
}
