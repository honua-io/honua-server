// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane;

/// <summary>
/// Default preflight gate. Reuses the shared compatibility prevalidation analyzer when a persisted
/// release package is addressable, and otherwise classifies the additive plan directly: a plan whose
/// forward operations are all nullable column adds is script-reversible. The reconciler refuses
/// snapshot-required classifications (the deferred Item 2 path).
///
/// The metadata-release reconciler that consumes this gate is a singleton, but the canonical
/// <see cref="IMetadataCompatibilityPrevalidationService"/> (and the graph/package services it
/// depends on) is registered scoped. Resolving it per evaluation through an
/// <see cref="IServiceScopeFactory"/> avoids capturing scoped services from the root provider and
/// keeps the gate compatible with DI scope validation.
/// </summary>
internal sealed class MetadataReleasePreflightGate(
    IServiceScopeFactory scopeFactory) : IMetadataReleasePreflightGate
{
    public async Task<MetadataReleasePreflightResult> EvaluateAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // When the package id resolves to a persisted GUID package, run the canonical prevalidation
        // gate and lift its rollback classification. This is the shared sync-check path.
        if (Guid.TryParse(plan.PackageId, out var packageId))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var prevalidationService = scope.ServiceProvider
                .GetRequiredService<IMetadataCompatibilityPrevalidationService>();
            var report = await prevalidationService.PrevalidateAsync(
                    new MetadataCompatibilityPrevalidationRequest
                    {
                        ReleasePackageId = packageId,
                        TargetEnvironment = plan.TargetEnvironment,
                        DataScripts = BuildAnalyzerScripts(plan)
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var canProceed = report.Status != MetadataCompatibilityStatus.Blocked &&
                report.Status != MetadataCompatibilityStatus.Unknown;
            return new MetadataReleasePreflightResult
            {
                CanProceed = canProceed,
                RollbackClassification = report.RollbackReadiness.Classification,
                Reason = report.RollbackReadiness.Reason,
                Blockers = canProceed
                    ? Array.Empty<string>()
                    : report.Findings
                        .Where(static finding => finding.Severity == MetadataCompatibilityFindingSeverity.Error)
                        .Select(static finding => finding.Code)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
            };
        }

        // No persisted package — classify the executable plan directly. Nullable additive adds are
        // reversible by construction (drop the added column).
        var allAdditiveNullable = plan.Script.ForwardOperations.Count > 0 &&
            plan.Script.ForwardOperations.All(static op =>
                op.Kind == MetadataReleaseScriptOperationKind.AddColumn && op.Nullable);

        return allAdditiveNullable && plan.Script.Reversible
            ? new MetadataReleasePreflightResult
            {
                CanProceed = true,
                RollbackClassification = MetadataRollbackReadinessClassification.ScriptReversible,
                Reason = "Additive nullable column adds are reversible by dropping the added columns."
            }
            : new MetadataReleasePreflightResult
            {
                CanProceed = false,
                RollbackClassification = MetadataRollbackReadinessClassification.Manual,
                Reason = "Plan is not a reversible additive change; manual review is required.",
                Blockers = ["metadata-release-non-additive"]
            };
    }

    private static IReadOnlyList<MetadataDataScriptEntry> BuildAnalyzerScripts(MetadataReleaseExecutionPlan plan)
        => [new MetadataDataScriptEntry
        {
            ScriptId = plan.Script.ScriptId,
            Reversible = plan.Script.Reversible,
            TargetEnvironment = plan.TargetEnvironment,
            DeclaredOperations = plan.Script.ForwardOperations
                .Select(static op => op.Kind == MetadataReleaseScriptOperationKind.AddColumn ? "add-field" : "drop-field")
                .ToArray()
        }];
}

/// <summary>
/// Default data-job dispatcher. Submits the optional ETL/data-populate workload through the
/// canonical execution-job store so the execution-job reconciler (and its background worker)
/// actually starts and drives the job, then polls the durable record for a terminal state.
/// Dispatching the local backend's <c>StartAsync</c> directly would only mark the job
/// <c>Running</c> without enqueuing it for any worker, leaving the release to time out and
/// require manual intervention. Returns false when no workload is declared.
/// </summary>
internal sealed class MetadataReleaseDataJobDispatcher(
    IExecutionJobStore jobStore,
    IEnumerable<IBatchComputeBackend> backends) : IMetadataReleaseDataJobDispatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    public async Task<bool> DispatchAndAwaitAsync(
        MetadataReleaseExecutionPlan plan,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plan.DataPopulateWorkloadId))
        {
            return false;
        }

        // Prefer the local in-process backend so the additive demo path runs without a cloud backend.
        // The backend is only used to resolve the target kind/name stamped on the spec; the canonical
        // reconciler is what actually starts and observes the job once it is persisted as Queued.
        var backend = backends.FirstOrDefault(b => b.BackendName == LocalBatchComputeBackend.BackendId)
            ?? backends.FirstOrDefault()
            ?? throw new InvalidOperationException("No batch-compute backend is registered for metadata-release data populate.");

        var jobOperationId = $"metadata-release-etl-{operationId}";
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = jobOperationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = new ExecutionJobSpec
            {
                WorkloadId = plan.DataPopulateWorkloadId,
                TargetKind = backend.TargetKind,
                Backend = backend.BackendName,
                Kind = ExecutionJobKind.ExtractTransformLoad,
                WorkloadName = $"metadata-release-populate:{plan.ResourceSemanticId}"
            }
        };

        // Enqueue through the canonical job store. Idempotent across reconciler re-entry: a job that
        // already exists (because a prior cycle persisted it) is simply observed to completion.
        await jobStore.TryCreateAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + MaxWait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = await jobStore.GetAsync(jobOperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Data-populate job '{jobOperationId}' was not found in the execution-job store after enqueue.");

            if (IsTerminal(current.Status))
            {
                return current.Status switch
                {
                    ExecutionJobStatus.Succeeded => true,
                    ExecutionJobStatus.Cancelled => throw new InvalidOperationException($"Data-populate job '{jobOperationId}' was cancelled."),
                    _ => throw new InvalidOperationException($"Data-populate job '{jobOperationId}' failed."),
                };
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Data-populate job '{jobOperationId}' did not complete within {MaxWait}.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;
}

/// <summary>
/// Default metadata-release activator. Captures the prior current revision and reactivates a prior
/// revision through the canonical Metadata v2 graph store (the same <c>UpsertCurrentAsync</c> path
/// admin activation uses). Resolves scoped graph services per call through a scope factory because
/// the reconciler is a singleton.
/// </summary>
internal sealed class MetadataReleaseActivator(IServiceScopeFactory scopeFactory) : IMetadataReleaseActivator
{
    public async Task<long?> GetCurrentRevisionAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var current = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return current.Revision;
    }

    public async Task ActivateNewRevisionAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        // The new revision is produced by the upstream package-apply step; activation re-persists the
        // current snapshot to stamp it as current. SaveAsync routes through UpsertCurrentAsync.
        var store = scope.ServiceProvider.GetService<IMetadataV2GraphStore>();
        if (store is null)
        {
            // Read-only graph backends have nothing to activate; the snapshot is already current.
            return;
        }

        var current = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(current.Graph, current.Etag, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReactivateRevisionAsync(
        MetadataReleaseExecutionPlan plan,
        long revision,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var store = scope.ServiceProvider.GetService<IMetadataV2GraphStore>();
        if (store is null)
        {
            return;
        }

        var prior = await provider.GetByRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        if (prior is null)
        {
            throw new InvalidOperationException(
                $"Prior Metadata v2 revision {revision} is no longer retained; reversible rollback cannot reactivate it.");
        }

        var current = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(prior.Graph, current.Etag, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Default smoke checker. Queries the just-published layer through the canonical query pipeline
/// (<see cref="IFeatureReader.QueryAsync"/>) and asserts rows return and the activated revision's
/// schema includes the new field. Records nothing itself — the reconciler captures the evidence ref.
/// </summary>
internal sealed class MetadataReleaseSmokeChecker(IServiceScopeFactory scopeFactory) : IMetadataReleaseSmokeChecker
{
    public async Task<MetadataReleaseSmokeResult> RunAsync(
        MetadataReleaseExecutionPlan plan,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var reader = scope.ServiceProvider.GetRequiredService<IFeatureReader>();

        var snapshot = await provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesById.TryGetValue(plan.ResourceSemanticId, out var resource))
        {
            return new MetadataReleaseSmokeResult
            {
                Passed = false,
                Message = $"Resource '{plan.ResourceSemanticId}' was not found in the activated revision."
            };
        }

        var newFieldPresent = resource.SchemaFields.Any(field =>
            string.Equals(field.Name, plan.NewFieldName, StringComparison.OrdinalIgnoreCase));

        var binding = snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].FirstOrDefault();
        var layerId = binding?.StorageLayerId;
        if (layerId is null)
        {
            return new MetadataReleaseSmokeResult
            {
                Passed = false,
                NewFieldPresent = newFieldPresent,
                Message = $"Resource '{plan.ResourceSemanticId}' has no storage layer binding to query."
            };
        }

        var result = await reader.QueryAsync(
                layerId.Value,
                new FeatureQuery { Limit = 1, OutFields = ImmutableArray.Create(plan.NewFieldName) },
                cancellationToken)
            .ConfigureAwait(false);

        var rowCount = result.TotalCount > 0 ? result.TotalCount : result.Items.Length;
        var rowsReturned = result.Items.Length > 0 || result.TotalCount > 0;
        var passed = rowsReturned && newFieldPresent;

        return new MetadataReleaseSmokeResult
        {
            Passed = passed,
            RowCount = rowCount,
            NewFieldPresent = newFieldPresent,
            Message = passed
                ? $"Queried layer {layerId.Value}: {rowCount} row(s) returned and field '{plan.NewFieldName}' is present in the activated schema."
                : !newFieldPresent
                    ? $"Activated schema for '{plan.ResourceSemanticId}' does not include the new field '{plan.NewFieldName}'."
                    : $"Canonical query of layer {layerId.Value} returned no rows."
        };
    }
}
