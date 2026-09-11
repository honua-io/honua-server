// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
/// Default metadata-release activator over the canonical Metadata v2 graph store. Reads go to the
/// persisted store (never the cached read provider) so every optimistic-concurrency precondition is
/// exact. Resolves scoped graph services per call through a scope factory because the reconciler is
/// a singleton.
/// </summary>
internal sealed class MetadataReleaseActivator(IServiceScopeFactory scopeFactory) : IMetadataReleaseActivator
{
    internal const string StagingUnsupported = "metadata-release-staging-unsupported";

    public async Task<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await RequireStore(scope).GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MetadataV2GraphSnapshot?> GetRevisionAsync(long revision, CancellationToken cancellationToken = default)
    {
        if (revision <= 0)
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        return await RequireStore(scope).GetByRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MetadataV2GraphSnapshot> StageAsync(MetadataV2Graph candidate, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (RequireStore(scope) is not IMetadataV2GraphRevisionStager stager)
        {
            throw new MetadataReleasePreparationException(
                StagingUnsupported,
                "The Metadata v2 graph store cannot stage revisions, so the candidate cannot be prepared without changing the active revision.");
        }

        return await stager.StageAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    public async Task DiscardAsync(long revision, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (RequireStore(scope) is IMetadataV2GraphRevisionStager stager)
        {
            await stager.DiscardStagedAsync(revision, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<MetadataReleaseActivationResult> ActivateAsync(
        long revision,
        string expectedCurrentEtag,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = RequireStore(scope);
        var current = await store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision == revision)
        {
            // A resumed activation: the pointer already moved before the operation record was saved.
            return Result(MetadataReleaseActivationOutcome.AlreadyActive, current);
        }

        try
        {
            var activated = await store.ActivateRevisionAsync(revision, expectedCurrentEtag, cancellationToken).ConfigureAwait(false);
            return Result(MetadataReleaseActivationOutcome.Activated, activated);
        }
        catch (MetadataV2GraphConcurrencyException)
        {
            var observed = await store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return Result(
                observed.Revision == revision ? MetadataReleaseActivationOutcome.AlreadyActive : MetadataReleaseActivationOutcome.Conflict,
                observed);
        }
    }

    public async Task<MetadataV2GraphSnapshot> CommitRevertAsync(
        MetadataV2Graph reverted,
        string expectedCurrentEtag,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await RequireStore(scope).SaveAsync(reverted, expectedCurrentEtag, cancellationToken).ConfigureAwait(false);
    }

    private static IMetadataV2GraphStore RequireStore(AsyncServiceScope scope)
        => scope.ServiceProvider.GetService<IMetadataV2GraphStore>()
            ?? throw new MetadataReleasePreparationException(
                StagingUnsupported,
                "The Metadata v2 graph backend is read-only; protected metadata releases need a writable graph store.");

    private static MetadataReleaseActivationResult Result(MetadataReleaseActivationOutcome outcome, MetadataV2GraphSnapshot current)
        => new() { Outcome = outcome, CurrentRevision = current.Revision, CurrentEtag = current.Etag };
}

/// <summary>
/// Default smoke checker. Exercises one explicit retained revision — the staged candidate before
/// activation, the live revision after activation, or the recovered revision after rollback — and
/// never "whatever is current". It checks the schema expectation, that the resource is still
/// published and bound to queryable storage, that rendering/schema references resolving in the
/// baseline still resolve, that the shared access-policy evaluator reaches the same decisions as for
/// the baseline, and finally queries the resource through the canonical provider router bound to the
/// exercised snapshot (<see cref="FeatureProviderQueryRouter.ResolveReaderAsync"/>).
/// </summary>
internal sealed class MetadataReleaseSmokeChecker(
    IServiceScopeFactory scopeFactory,
    IOptions<MetadataReleaseOperationOptions> options) : IMetadataReleaseSmokeChecker
{
    // AccessPolicyEvaluator treats a non-zero integer scope as a write request.
    private const int WriteScope = 1;
    private readonly MetadataReleaseFaultInjectionOptions _faultInjection = options.Value.FaultInjection;

    public async Task<MetadataReleaseSmokeResult> RunAsync(
        MetadataReleaseExecutionPlan plan,
        MetadataReleaseSmokeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        // Demo-only deterministic fault injection: when explicitly enabled for an allowed (non-prod)
        // target environment, report a post-activation smoke failure so the verified-rollback closed
        // loop runs. It never applies to the candidate or recovery checks, and ShouldFailSmoke returns
        // false unless Enabled+ForceSmokeFailure and the target environment is on the allow-list.
        if (request.Phase == MetadataReleaseSmokePhase.Activated && _faultInjection.ShouldFailSmoke(plan.TargetEnvironment))
        {
            return Failed(newFieldPresent: true, _faultInjection.Reason);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphProvider>();
        var label = $"{request.Phase.ToString().ToLowerInvariant()} revision {request.Revision}";

        var snapshot = await provider.GetByRevisionAsync(request.Revision, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Failed(newFieldPresent: false, $"The {label} is not retained, so it cannot be exercised.");
        }

        if (!snapshot.Index.ResourcesById.TryGetValue(plan.ResourceSemanticId, out var resource))
        {
            return Failed(newFieldPresent: false, $"Resource '{plan.ResourceSemanticId}' was not found in the {label}.");
        }

        var newFieldPresent = HasField(resource, plan.NewFieldName);
        if (newFieldPresent != request.ExpectNewField)
        {
            return Failed(
                newFieldPresent,
                request.ExpectNewField
                    ? $"Schema of the {label} does not include the new field '{plan.NewFieldName}'."
                    : $"Schema of the {label} still includes the reverted field '{plan.NewFieldName}'.");
        }

        var publication = snapshot.Index.PublicationsByResource[resource.Metadata.Id]
            .FirstOrDefault(candidate => snapshot.Index.ServicesById.ContainsKey(candidate.ServiceId));
        if (publication is null)
        {
            return Failed(newFieldPresent, $"Resource '{plan.ResourceSemanticId}' is not published by any service in the {label}.");
        }

        var service = snapshot.Index.ServicesById[publication.ServiceId];
        if (snapshot.ResolveStorageBinding(publication)?.StorageLayerId is not int storageLayerId)
        {
            return Failed(newFieldPresent, $"Publication '{publication.Metadata.Id}' has no queryable storage binding in the {label}.");
        }

        if (request.BaselineRevision is long baselineRevision && baselineRevision != request.Revision)
        {
            var baseline = await provider.GetByRevisionAsync(baselineRevision, cancellationToken).ConfigureAwait(false);
            if (baseline is not null && baseline.Index.ResourcesById.TryGetValue(resource.Metadata.Id, out var baselineResource))
            {
                var regressed = FindRegressedReference(baseline, baselineResource, snapshot, resource);
                if (regressed is not null)
                {
                    return Failed(newFieldPresent, $"The {label} breaks {regressed}, which resolves in baseline revision {baselineRevision}.");
                }

                baseline.Index.ServicesById.TryGetValue(service.Metadata.Id, out var baselineService);
                var evaluator = scope.ServiceProvider.GetRequiredService<IAccessPolicyEvaluator>();
                var drift = FindAuthorizationDrift(
                    evaluator,
                    (baselineResource.AccessPolicy, baselineService?.AccessPolicy),
                    (resource.AccessPolicy, service.AccessPolicy));
                if (drift is not null)
                {
                    return Failed(newFieldPresent, $"The {label} changes authorization relative to baseline revision {baselineRevision}: {drift}.");
                }
            }
        }

        var router = scope.ServiceProvider.GetService<FeatureProviderQueryRouter>();
        if (router is null)
        {
            return Failed(newFieldPresent, "No canonical feature provider router is registered, so the revision cannot be queried.");
        }

        var reader = await router.ResolveReaderAsync(
                snapshot,
                service,
                resource,
                publication,
                storageLayerId,
                FeatureProviderReadOperation.Query,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await reader.QueryAsync(
                storageLayerId,
                new FeatureQuery
                {
                    Limit = 1,
                    OutFields = request.ExpectNewField ? ImmutableArray.Create(plan.NewFieldName) : null
                },
                cancellationToken)
            .ConfigureAwait(false);

        var rowCount = result.TotalCount > 0 ? result.TotalCount : result.Items.Length;
        var rowsReturned = result.Items.Length > 0 || result.TotalCount > 0;
        return new MetadataReleaseSmokeResult
        {
            Passed = rowsReturned,
            RowCount = rowCount,
            NewFieldPresent = newFieldPresent,
            Message = rowsReturned
                ? $"Queried the {label} of '{plan.ResourceSemanticId}' through the canonical provider path: {rowCount} row(s) on layer {storageLayerId}; " +
                  "schema, bindings, rendering references and authorization verified."
                : $"Canonical query of the {label} (layer {storageLayerId}) returned no rows."
        };
    }

    private static MetadataReleaseSmokeResult Failed(bool newFieldPresent, string message)
        => new() { Passed = false, NewFieldPresent = newFieldPresent, Message = message };

    private static bool HasField(MetadataV2Resource resource, string? fieldName)
        => !string.IsNullOrWhiteSpace(fieldName) &&
           resource.SchemaFields.Any(field => string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A rendering or schema reference that resolves in the baseline but not in the exercised
    /// revision. Comparing against the baseline keeps the check honest for graphs whose references
    /// point at system columns that are never declared as schema fields.
    /// </summary>
    private static string? FindRegressedReference(
        MetadataV2GraphSnapshot baselineSnapshot,
        MetadataV2Resource baseline,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Resource resource)
    {
        (string Label, string? Field)[] references =
        [
            ("display field", baseline.Display?.DisplayField),
            ("global id field", baseline.Editing?.GlobalIdField),
            ("creator field", baseline.Editing?.CreatorField),
            ("created-at field", baseline.Editing?.CreatedAtField),
            ("editor field", baseline.Editing?.EditorField),
            ("updated-at field", baseline.Editing?.UpdatedAtField),
        ];
        foreach (var (label, field) in references)
        {
            if (HasField(baseline, field) && !HasField(resource, field))
            {
                return $"{label} '{field}'";
            }
        }

        foreach (var styleId in baseline.StyleResourceIds)
        {
            if (baselineSnapshot.Index.ResourcesById.ContainsKey(styleId) &&
                (!resource.StyleResourceIds.Contains(styleId, StringComparer.Ordinal) ||
                 !snapshot.Index.ResourcesById.ContainsKey(styleId)))
            {
                return $"style '{styleId}'";
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates the shared access-policy evaluator for the exercised and baseline policies across
    /// anonymous, authenticated and every referenced role, for read and write, and reports the first
    /// principal whose decision changed.
    /// </summary>
    private static string? FindAuthorizationDrift(
        IAccessPolicyEvaluator evaluator,
        (AccessPolicy? Layer, AccessPolicy? Service) baseline,
        (AccessPolicy? Layer, AccessPolicy? Service) exercised)
    {
        var roles = new[] { baseline.Layer, baseline.Service, exercised.Layer, exercised.Service }
            .Where(static policy => policy is not null)
            .SelectMany(static policy => (policy!.AllowedRoles ?? []).Concat(policy.AllowedWriteRoles ?? []))
            .Select(static role => role.Trim())
            .Where(static role => role.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var principals = new List<(string Label, ClaimsPrincipal Principal)>
        {
            ("anonymous", new ClaimsPrincipal(new ClaimsIdentity())),
            ("authenticated", Authenticated())
        };
        principals.AddRange(roles.Select(role => ($"role '{role}'", Authenticated(role))));

        foreach (var (label, principal) in principals)
        {
            foreach (var (access, scope) in new (string, object?)[] { ("read", null), ("write", WriteScope) })
            {
                var before = evaluator.Evaluate(principal, baseline.Layer, baseline.Service, scope);
                var after = evaluator.Evaluate(principal, exercised.Layer, exercised.Service, scope);
                if (before.IsAllowed != after.IsAllowed || before.RequiresAuthentication != after.RequiresAuthentication)
                {
                    return $"{label} {access} was {(before.IsAllowed ? "allowed" : "denied")} and is now {(after.IsAllowed ? "allowed" : "denied")}";
                }
            }
        }

        return null;
    }

    private static ClaimsPrincipal Authenticated(string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "metadata-release-smoke") };
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "metadata-release-smoke"));
    }
}
