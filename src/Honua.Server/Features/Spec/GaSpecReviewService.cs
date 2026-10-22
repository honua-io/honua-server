// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Workflow;

namespace Honua.Server.Features.Spec;

internal sealed class GaSpecReviewService(IWorkflowOperationStore store, GaLayerPublicationAdapter adapter, IGaSpecContextReader contexts)
{
    internal async Task<WorkflowOperationRecord> PlanAsync(SpecLayerIntent spec, string tenant,
        string actor, CancellationToken cancellationToken)
    {
        if (spec.BundleVersion != WorkflowBundle.LoadDefault().BundleVersion || string.IsNullOrWhiteSpace(spec.ContextRevision) ||
            spec.ConnectionId == Guid.Empty || new[] { spec.Schema, spec.Table, spec.LayerName, spec.ServiceName }
                .Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)))
        {
            throw new GaSpecBlockedException("invalid-intent");
        }

        if (spec.ExpectEnabled == false) throw new GaSpecBlockedException("assertion-unsupported");
        var contextHash = await contexts.ReadAuthorizedContextHashAsync(spec, tenant, actor, cancellationToken).ConfigureAwait(false);
        var actionHash = GaLayerPublicationAdapter.Hash(JsonSerializer.Serialize(spec with { ExpectEnabled = null },
            GaSpecJsonContext.Default.SpecLayerIntent));
        var verificationHash = GaLayerPublicationAdapter.Hash($"layer.readback.v1|{spec.ExpectEnabled}");
        var observed = await adapter.ObserveAsync(spec, tenant, cancellationToken).ConfigureAwait(false);
        var review = new SpecReviewContext
        {
            Spec = spec, ActionHash = actionHash, VerificationHash = verificationHash,
            ContextHash = contextHash, ObservedHash = observed.Observation, PolicyRevision = observed.Policy,
            Verdict = observed.Verdict, DraftedBy = actor, LayerId = observed.LayerId,
            Revision = GaLayerPublicationAdapter.Hash(string.Join('|', tenant, actionHash,
                verificationHash, contextHash, observed.Policy, observed.Observation))
        };
        var now = DateTimeOffset.UtcNow;
        var operation = new WorkflowOperationRecord
        {
            OperationId = $"spec-{Guid.NewGuid():N}", Kind = WorkflowOperationKind.SpecApply,
            Status = WorkflowOperationStatus.Planned, CreatedAt = now, UpdatedAt = now,
            Audit = new OperationAuditInfo { RequestedBy = actor, TenantId = tenant }, SpecReview = review
        };
        if (!await store.TryCreateAsync(operation, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new GaSpecBlockedException("plan-persistence-failed");
        }

        return await store.GetAsync(operation.OperationId, cancellationToken).ConfigureAwait(false)
            ?? throw new GaSpecBlockedException("plan-persistence-failed");
    }

    internal async Task<WorkflowOperationRecord?> GetAsync(string id, string tenant, CancellationToken cancellationToken)
    {
        var operation = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return operation?.Kind == WorkflowOperationKind.SpecApply && operation.Audit.TenantId == tenant
            ? operation : null;
    }

    internal Task AuthorizeReadAsync(WorkflowOperationRecord operation, string tenant, string actor,
        CancellationToken cancellationToken) => contexts.ReadAuthorizedContextHashAsync(operation.SpecReview!.Spec,
            tenant, actor, cancellationToken, requireCurrent: false);

    internal async Task<WorkflowOperationRecord> ApplyAsync(string id, GaSpecApplyRequest command,
        string tenant, string actor, CancellationToken cancellationToken)
    {
        var operation = await GetAsync(id, tenant, cancellationToken).ConfigureAwait(false)
            ?? throw new GaSpecBlockedException("plan-not-found");
        var review = operation.SpecReview!;
        if (review.Revision != command.Revision || command.CommandId == Guid.Empty)
        {
            throw new GaSpecBlockedException("review-stale");
        }

        if (operation.Status != WorkflowOperationStatus.Planned)
        {
            // The persisted command is the only replay key. A transport retry never
            // starts another effect, including after process restart or a lost response.
            if (review.CommandId == command.CommandId.ToString("D") && review.AppliedBy == actor)
            {
                await AuthorizeReadAsync(operation, tenant, actor, cancellationToken).ConfigureAwait(false);
                return operation;
            }

            throw new GaSpecBlockedException("plan-already-applied");
        }

        var contextHash = await contexts.ReadAuthorizedContextHashAsync(review.Spec, tenant, actor, cancellationToken).ConfigureAwait(false);
        if (contextHash != review.ContextHash) throw new GaSpecBlockedException("workspace-context-stale");
        var observed = await adapter.ObserveAsync(review.Spec, tenant, cancellationToken).ConfigureAwait(false);
        if (observed.Policy != review.PolicyRevision || observed.Observation != review.ObservedHash)
        {
            throw new GaSpecBlockedException("review-stale");
        }

        var accepted = operation with
        {
            Status = WorkflowOperationStatus.Submitted, UpdatedAt = DateTimeOffset.UtcNow,
            SpecReview = review with { AppliedBy = actor, CommandId = command.CommandId.ToString("D") }
        };
        if (!await store.TrySetAsync(accepted, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            throw new GaSpecBlockedException("command-conflict");
        }

        // Durable admission precedes the effect. An unrecorded outcome stays
        // interrupted and cannot be blindly replayed by any caller.
        operation = await GetAsync(id, tenant, CancellationToken.None).ConfigureAwait(false)
            ?? throw new GaSpecBlockedException("run-unavailable");
        try
        {
            if (review.Verdict == "create")
            {
                await adapter.PublishAsync(review.Spec, tenant, CancellationToken.None).ConfigureAwait(false);
            }

            observed = await adapter.ObserveAsync(review.Spec, tenant, CancellationToken.None).ConfigureAwait(false);
            var verified = observed.LayerId.HasValue && observed.Verdict == "noop" && review.Spec.ExpectEnabled != false;
            operation = operation with
            {
                Status = verified ? WorkflowOperationStatus.Succeeded : WorkflowOperationStatus.Failed,
                UpdatedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
                BlockingReasons = verified ? [] : ["verification-failed"],
                SpecReview = operation.SpecReview! with { LayerId = observed.LayerId, ReadbackHash = observed.Observation, Evidence = verified ? "verified" : "unverified" }
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never expose adapter exceptions: providers may include credentials in
            // exception messages. Unknown effects require operator reconciliation.
            operation = operation with
            {
                Status = WorkflowOperationStatus.ManualInterventionRequired,
                UpdatedAt = DateTimeOffset.UtcNow,
                BlockingReasons = [exception is GaSpecBlockedException blocked ? blocked.Code : "execution-outcome-unknown"]
            };
        }

        if (!await store.TrySetAsync(operation, cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            throw new GaSpecBlockedException("run-update-conflict");
        }

        return await GetAsync(id, tenant, CancellationToken.None).ConfigureAwait(false)
            ?? throw new GaSpecBlockedException("run-unavailable");
    }

    internal static GaSpecView ToView(WorkflowOperationRecord operation)
    {
        var review = operation.SpecReview!;
        var execution = operation.Status switch
        {
            WorkflowOperationStatus.Planned => "planned",
            WorkflowOperationStatus.Succeeded => "succeeded",
            WorkflowOperationStatus.Failed => "failed",
            // Without a durable job receipt, a submitted effect may have committed.
            // Readback exposes uncertainty instead of claiming a live process.
            _ => "interrupted"
        };
        return new GaSpecView(operation.OperationId, review.CommandId is null ? null : operation.OperationId,
            review.Revision, review.Spec, review.ActionHash, review.VerificationHash, review.ObservedHash,
            review.Verdict, execution, review.Evidence, review.DraftedBy, review.AppliedBy,
            review.LayerId, operation.BlockingReasons);
    }
}
