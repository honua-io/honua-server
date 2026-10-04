// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Security.Claims;
using Honua.Core.Features.AttributeRules;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Decorates the provider <see cref="IFeatureWriter"/> so a resource's owner-based edit policy
/// is binding at the shared edit-pipeline boundary, not only on the protocol handlers that
/// evaluate it themselves (SEC-5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a decorator.</b> Every protocol adapter that mutates features funnels through
/// <see cref="IFeatureWriter.ApplyEditsAsync(int, FeatureEditBatch, CancellationToken)"/> or a
/// writer transaction: GeoServices <c>applyEdits</c>, OGC API Features, OData single-entity writes
/// and change sets, WFS-T and the gRPC feature service. The GeoServices handler and the attachment
/// endpoints evaluate the owner policy themselves so they can refuse in their own error shape;
/// this decorator makes the same decision for every other path, including one added later.
/// </para>
/// <para>
/// <b>What it enforces.</b> When any resource bound to the target storage layer declares an
/// enabled <see cref="MetadataV2OwnerEditPolicy"/>, updates and deletes are allowed only for the
/// owning principal or an administrator (the owner is read from the current row through the
/// row-security-enforced <see cref="IFeatureReader"/>), creates are refused for anonymous callers
/// and stamped with the creator when the policy says so. The rules are the shared
/// <see cref="OwnerEditPolicyEvaluator"/>, so every surface makes the same decision. The owner read
/// travels to the writer as a row-state precondition, so a row whose owner changes before the
/// write is not written.
/// </para>
/// <para>
/// <b>Who the principal is.</b> The current request's user; inside a background job with no
/// request, the job submitter captured at submission time, evaluated in a service scope of its
/// own. A write with neither is treated as anonymous and refused on an owner-policy layer.
/// </para>
/// <para>
/// <b>How a refusal is reported.</b> As a rolled-back <see cref="FeatureEditResult"/> whose
/// operations carry error code <c>403</c>; nothing is written and the inner writer is never
/// called. The single-feature entry points throw <see cref="FeatureEditNotPermittedException"/>.
/// </para>
/// <para>
/// <b>Cost.</b> Layers without an owner policy pay one metadata snapshot lookup per batch. Only
/// owner-policy layers pay one row read per update or delete target.
/// </para>
/// </remarks>
public sealed class OwnerEditPolicyEnforcingFeatureWriter : IFeatureWriter
{
    /// <summary>HTTP status carried on an operation refused by the owner policy.</summary>
    public const int NotPermittedErrorCode = StatusCodes.Status403Forbidden;

    private readonly IFeatureWriter _inner;
    private readonly OwnerEditPolicyEnforcer _enforcer;

    /// <summary>
    /// Initializes a new instance of the <see cref="OwnerEditPolicyEnforcingFeatureWriter"/> class.
    /// </summary>
    /// <param name="inner">The writer being decorated.</param>
    /// <param name="metadata">Metadata graph provider used to map a storage layer to its resources.</param>
    /// <param name="httpContextAccessor">Accessor for the request being served, if any.</param>
    /// <param name="services">
    /// Service provider that supplies the row-security-enforced <see cref="IFeatureReader"/> (resolved
    /// only when an owner policy applies) and evaluates a job submitter outside a request.
    /// </param>
    public OwnerEditPolicyEnforcingFeatureWriter(
        IFeatureWriter inner,
        IMetadataV2GraphProvider metadata,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _enforcer = new OwnerEditPolicyEnforcer(metadata, httpContextAccessor, services);
    }

    /// <inheritdoc />
    public async Task<IFeatureWriterTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var transaction = await _inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return new EnforcingTransaction(transaction, _enforcer);
    }

    /// <inheritdoc />
    public async Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var outcome = await _enforcer.EvaluateAsync(
            layerId,
            FeatureEditBatch.Create(creates: [feature]),
            cancellationToken).ConfigureAwait(false);
        ThrowIfDenied(outcome);
        return await _inner.CreateAsync(layerId, outcome.Batch.Creates[0], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
    {
        var outcome = await _enforcer.EvaluateAsync(
            layerId,
            FeatureEditBatch.Create(updates: [feature]),
            cancellationToken).ConfigureAwait(false);
        ThrowIfDenied(outcome);
        return await _inner.UpdateAsync(layerId, feature, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var outcome = await _enforcer.EvaluateAsync(
            layerId,
            FeatureEditBatch.Create(deletes: [featureId]),
            cancellationToken).ConfigureAwait(false);
        ThrowIfDenied(outcome);
        return await _inner.DeleteAsync(layerId, featureId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FeatureEditResult> ApplyEditsAsync(
        int layerId,
        FeatureEditBatch editBatch,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _enforcer.EvaluateAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
        return outcome.DenialReason is { } reason
            ? BuildNotPermittedRollback(editBatch, reason)
            : await _inner.ApplyEditsAsync(layerId, outcome.Batch, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfDenied(OwnerEditPolicyOutcome outcome)
    {
        if (outcome.DenialReason is { } reason)
        {
            throw new FeatureEditNotPermittedException(reason);
        }
    }

    /// <summary>
    /// Builds the rolled-back result for a batch the owner policy refuses: nothing was written
    /// and every operation reports the refusal.
    /// </summary>
    private static FeatureEditResult BuildNotPermittedRollback(FeatureEditBatch editBatch, string reason)
    {
        static ImmutableArray<EditOperationResult> Failures(int count, string message)
            => count == 0
                ? ImmutableArray<EditOperationResult>.Empty
                : Enumerable
                    .Range(0, count)
                    .Select(_ => EditOperationResult.Failure(message, NotPermittedErrorCode))
                    .ToImmutableArray();

        var creates = editBatch.Creates.IsDefaultOrEmpty ? 0 : editBatch.Creates.Length;
        var updates = editBatch.Updates.IsDefaultOrEmpty ? 0 : editBatch.Updates.Length;
        var deletes = editBatch.Deletes.IsDefaultOrEmpty ? 0 : editBatch.Deletes.Length;
        if (!editBatch.Operations.IsDefaultOrEmpty)
        {
            creates = Math.Max(creates, editBatch.Operations.Count(static op => op.Kind == FeatureEditOperationKind.Create));
            updates = Math.Max(updates, editBatch.Operations.Count(static op => op.Kind == FeatureEditOperationKind.Update));
            deletes = Math.Max(deletes, editBatch.Operations.Count(static op => op.Kind == FeatureEditOperationKind.Delete));
        }

        return FeatureEditResult.Rollback(
            Failures(creates, reason),
            Failures(updates, reason),
            Failures(deletes, reason));
    }

    /// <summary>
    /// Applies the same owner-policy decision to every batch applied through a writer transaction
    /// (the OGC API Features batch, the OData atomic change set and multi-layer WFS-T use it).
    /// </summary>
    private sealed class EnforcingTransaction(
        IFeatureWriterTransaction inner,
        OwnerEditPolicyEnforcer enforcer) : IFeatureWriterTransaction
    {
        public async Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            var outcome = await enforcer.EvaluateAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
            return outcome.DenialReason is { } reason
                ? BuildNotPermittedRollback(editBatch, reason)
                : await inner.ApplyEditsAsync(layerId, outcome.Batch, cancellationToken).ConfigureAwait(false);
        }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => inner.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>
/// Result of evaluating a batch against the owner policies of its target layer: the batch to
/// apply (with owners stamped on creates) or the reason it is refused.
/// </summary>
/// <param name="Batch">The batch to hand to the inner writer.</param>
/// <param name="DenialReason">The refusal reason, or <see langword="null"/> when allowed.</param>
internal readonly record struct OwnerEditPolicyOutcome(FeatureEditBatch Batch, string? DenialReason);

/// <summary>
/// Evaluates a feature-edit batch against the owner-based edit policies of every resource bound
/// to the target storage layer.
/// </summary>
internal sealed class OwnerEditPolicyEnforcer
{
    private readonly IMetadataV2GraphProvider _metadata;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;

    public OwnerEditPolicyEnforcer(
        IMetadataV2GraphProvider metadata,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider services)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _services = services ?? throw new ArgumentNullException(nameof(services));

        // A background job can outlive the request scope that built this writer, so job writes are
        // evaluated in a scope of their own.
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
    }

    public async Task<OwnerEditPolicyOutcome> EvaluateAsync(
        int layerId,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var policies = await ResolvePoliciesAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (policies.Count == 0 || batch.IsEmpty)
        {
            return new OwnerEditPolicyOutcome(batch, null);
        }

        var (principal, services, jobScope) = ResolveEvaluationContext();
        using (jobScope)
        {
            // Anonymous callers are refused and administrators allowed whatever the row owner is,
            // so neither needs a row read.
            if (!principal.IsAuthenticated || string.IsNullOrEmpty(principal.Name) || principal.IsAdmin)
            {
                var decision = OwnerEditPolicyEvaluator.Evaluate(policies[0], AttributeRuleEditEvent.Update, null, principal);
                return decision.IsAllowed
                    ? new OwnerEditPolicyOutcome(StampOwners(batch, policies, principal), null)
                    : new OwnerEditPolicyOutcome(batch, decision.Reason);
            }

            var guarded = batch.Preconditions.IsDefaultOrEmpty
                ? []
                : batch.Preconditions.Select(static precondition => precondition.ObjectId).ToHashSet();
            var snapshotPreconditions = ImmutableArray.CreateBuilder<FeatureEditPrecondition>();
            IFeatureReader? reader = null;
            foreach (var (objectId, editEvent) in CollectTargets(batch))
            {
                reader ??= services.GetRequiredService<IFeatureReader>();
                var existing = await ReadCurrentRowAsync(reader, layerId, objectId, batch.VersionContext, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var policy in policies)
                {
                    // A row the caller cannot read has no owner the caller can be matched against.
                    object? existingOwner = null;
                    if (existing is { } row)
                    {
                        row.Attributes.TryGetValue(policy.OwnerField, out existingOwner);
                    }

                    var decision = OwnerEditPolicyEvaluator.Evaluate(policy, editEvent, existingOwner, principal);
                    if (!decision.IsAllowed)
                    {
                        return new OwnerEditPolicyOutcome(batch, decision.Reason);
                    }
                }

                // The writer re-checks this snapshot inside its transaction, so a row whose owner
                // changes between this read and the write is not written. Named-version edits come
                // only from GeoServices, which evaluates the policy on its own in-version reads.
                if (existing is { } snapshot && batch.VersionContext is not { IsDefault: false } && guarded.Add(objectId))
                {
                    snapshotPreconditions.Add(new FeatureEditPrecondition
                    {
                        ObjectId = objectId,
                        ExpectedStateToken = FeatureStateToken.FromReadSnapshot(snapshot)
                    });
                }
            }

            var stamped = StampOwners(batch, policies, principal);
            if (snapshotPreconditions.Count > 0)
            {
                stamped = stamped with
                {
                    Preconditions = (stamped.Preconditions.IsDefault ? [] : stamped.Preconditions)
                        .AddRange(snapshotPreconditions.ToImmutable())
                };
            }

            return new OwnerEditPolicyOutcome(stamped, null);
        }
    }

    /// <summary>
    /// Reads the row an update or delete targets, in the named version the batch edits when it
    /// carries one, so a row that exists only in that version is matched against its own owner.
    /// </summary>
    private static async Task<Feature?> ReadCurrentRowAsync(
        IFeatureReader reader,
        int layerId,
        long objectId,
        VersionContext? version,
        CancellationToken cancellationToken)
    {
        if (version is not { IsDefault: false } namedVersion)
        {
            return await reader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
        }

        var result = await reader.QueryAsync(
            layerId,
            new FeatureQuery { ObjectIds = [objectId], Limit = 1, VersionContext = namedVersion },
            cancellationToken).ConfigureAwait(false);
        return result.Items.IsDefaultOrEmpty ? null : result.Items[0];
    }

    /// <summary>
    /// Collects the enabled owner policies of every resource bound to <paramref name="layerId"/>.
    /// One storage layer can back several resources; each one's policy applies.
    /// </summary>
    private async Task<IReadOnlyList<MetadataV2OwnerEditPolicy>> ResolvePoliciesAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        MetadataV2GraphSnapshot snapshot;
        try
        {
            snapshot = await _metadata.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No usable graph (a host wired for storage-only access): no resource, so no policy.
            return [];
        }

        List<MetadataV2OwnerEditPolicy>? policies = null;
        foreach (var resource in snapshot.Graph.Resources)
        {
            if (resource.OwnerEditPolicy is not { Enabled: true } policy || !IsBoundTo(snapshot, resource, layerId))
            {
                continue;
            }

            policies ??= [];
            if (!policies.Contains(policy))
            {
                policies.Add(policy);
            }
        }

        return policies is null ? [] : policies;
    }

    private static bool IsBoundTo(MetadataV2GraphSnapshot snapshot, MetadataV2Resource resource, int layerId)
    {
        if (snapshot.ResolveStorageLayerId(resource) == layerId ||
            snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].Any(binding => binding.StorageLayerId == layerId))
        {
            return true;
        }

        foreach (var publication in snapshot.Graph.Publications)
        {
            if (string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal) &&
                snapshot.ResolveStorageLayerId(publication, resource) == layerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the principal the write is performed for: the request user, or the captured job
    /// submitter when the write runs in a background job. Anything else is anonymous.
    /// </summary>
    private (EditPrincipal Principal, IServiceProvider Services, IServiceScope? JobScope) ResolveEvaluationContext()
    {
        if (_httpContextAccessor.HttpContext is { } httpContext)
        {
            return (ResolvePrincipal(httpContext), _services, null);
        }

        if (JobSecurityScope.Current?.Submitter is not { } submitter)
        {
            return (EditPrincipal.Anonymous, _services, null);
        }

        var scope = _scopeFactory.CreateScope();
        var jobContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = JobSecurityContextCapture.Restore(submitter)
        };
        return (ResolvePrincipal(jobContext), scope.ServiceProvider, scope);
    }

    private static EditPrincipal ResolvePrincipal(HttpContext httpContext)
    {
        ClaimsPrincipal? user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return EditPrincipal.Anonymous;
        }

        return new EditPrincipal(
            user.Identity.Name,
            IsAuthenticated: true,
            IsAdmin: ServiceDataEditorAuthorization.IsAdminPrincipal(httpContext));
    }

    private static bool HasCreates(FeatureEditBatch batch)
        => !batch.Creates.IsDefaultOrEmpty ||
           (!batch.Operations.IsDefaultOrEmpty &&
            batch.Operations.Any(static operation => operation.Kind == FeatureEditOperationKind.Create));

    private static IEnumerable<(long ObjectId, AttributeRuleEditEvent EditEvent)> CollectTargets(FeatureEditBatch batch)
    {
        var seen = new HashSet<(long, AttributeRuleEditEvent)>();
        if (!batch.Operations.IsDefaultOrEmpty)
        {
            foreach (var operation in batch.Operations)
            {
                switch (operation.Kind)
                {
                    case FeatureEditOperationKind.Update when operation.Feature is { } feature:
                        if (seen.Add((feature.Id, AttributeRuleEditEvent.Update)))
                        {
                            yield return (feature.Id, AttributeRuleEditEvent.Update);
                        }

                        break;
                    case FeatureEditOperationKind.Delete when operation.ObjectId is { } objectId:
                        if (seen.Add((objectId, AttributeRuleEditEvent.Delete)))
                        {
                            yield return (objectId, AttributeRuleEditEvent.Delete);
                        }

                        break;
                    default:
                        break;
                }
            }
        }

        if (!batch.Updates.IsDefaultOrEmpty)
        {
            foreach (var update in batch.Updates)
            {
                if (seen.Add((update.Id, AttributeRuleEditEvent.Update)))
                {
                    yield return (update.Id, AttributeRuleEditEvent.Update);
                }
            }
        }

        if (!batch.Deletes.IsDefaultOrEmpty)
        {
            foreach (var objectId in batch.Deletes)
            {
                if (seen.Add((objectId, AttributeRuleEditEvent.Delete)))
                {
                    yield return (objectId, AttributeRuleEditEvent.Delete);
                }
            }
        }
    }

    /// <summary>
    /// Stamps the creating principal into each policy's owner field on every create, when the
    /// policy asks for it. Updates and deletes are passed through unchanged.
    /// </summary>
    private static FeatureEditBatch StampOwners(
        FeatureEditBatch batch,
        IReadOnlyList<MetadataV2OwnerEditPolicy> policies,
        EditPrincipal principal)
    {
        var stamped = policies.Where(OwnerEditPolicyEvaluator.ShouldStampOwnerOnInsert).ToArray();
        if (stamped.Length == 0 || string.IsNullOrEmpty(principal.Name) || !HasCreates(batch))
        {
            return batch;
        }

        Feature Stamp(Feature feature)
        {
            var attributes = feature.Attributes;
            foreach (var policy in stamped)
            {
                attributes = attributes.SetItem(policy.OwnerField, principal.Name);
            }

            return feature with { Attributes = attributes };
        }

        return batch with
        {
            Creates = batch.Creates.IsDefaultOrEmpty ? batch.Creates : batch.Creates.Select(Stamp).ToImmutableArray(),
            Operations = batch.Operations.IsDefaultOrEmpty
                ? batch.Operations
                : batch.Operations
                    .Select(operation => operation is { Kind: FeatureEditOperationKind.Create, Feature: { } feature }
                        ? operation with { Feature = Stamp(feature) }
                        : operation)
                    .ToImmutableArray()
        };
    }
}

/// <summary>
/// Thrown by the single-feature writer entry points when a resource's owner-based edit policy
/// refuses the mutation. The batch entry points report the refusal as a rolled-back result.
/// </summary>
public sealed class FeatureEditNotPermittedException : UnauthorizedAccessException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureEditNotPermittedException"/> class.
    /// </summary>
    /// <param name="message">The refusal reason.</param>
    public FeatureEditNotPermittedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureEditNotPermittedException"/> class.
    /// </summary>
    public FeatureEditNotPermittedException()
        : base("Edit not permitted.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureEditNotPermittedException"/> class.
    /// </summary>
    /// <param name="message">The refusal reason.</param>
    /// <param name="innerException">The underlying cause.</param>
    public FeatureEditNotPermittedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
