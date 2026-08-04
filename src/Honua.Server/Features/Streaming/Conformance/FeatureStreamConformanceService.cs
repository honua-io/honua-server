// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Events;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming.Conformance;

/// <summary>
/// Controlled-conformance mutation workflow (honua-server#3038, REQ-005/REQ-006/NFR-001).
/// </summary>
/// <remarks>
/// <para>Scheduled SDK conformance has to prove that a live deployment really does deliver a
/// baseline followed by a correlated mutation on every transport it advertises. Proving it
/// requires writing to that deployment, which is exactly the thing a scheduled job must never
/// do casually. This workflow is the narrow, bounded, reversible way to do it:</para>
/// <list type="bullet">
/// <item><b>Dedicated source.</b> Every write targets the single service/layer named by
/// configuration. There is no request parameter that can redirect a mutation elsewhere, so no
/// combination of inputs reaches an ordinary demo or user record (NFR-001).</item>
/// <item><b>Ownership, not trust.</b> Every controlled record carries a self-describing marker
/// in a configured attribute. Update, touch, delete, and cleanup re-read that marker from the
/// stored row and refuse anything the calling run does not own, so two concurrent runs cannot
/// claim or destroy each other's records even holding the same credential.</item>
/// <item><b>Canonical pipeline.</b> Mutations go through the standard
/// <see cref="IFeatureWriter"/> + <see cref="FeatureMutationEventService"/> plumbing — the same
/// write path, the same transactional-outbox scope, and the same outbox/inline publish decision
/// every protocol adapter uses. A conformance mutation is therefore observable on the feature
/// stream for the same reason a real edit is, not because of a test-only side channel. The
/// protocol-edit adapter and edit processor are not involved because there is no protocol
/// request to translate: the workflow builds the canonical edit batch directly.</item>
/// <item><b>Fail closed.</b> Disabled deployment, unresolvable source, missing deployment
/// revision, mismatched expected revision or source, exhausted lease, unknown run, foreign
/// record — each refuses the request. None of them fall back to a weaker guarantee.</item>
/// </list>
/// <para><b>Bounds.</b> Reads of the conformance source are capped by
/// <see cref="FeatureStreamConformanceOptions.MaxSweepRecords"/> and filter the ownership
/// marker in process rather than composing a predicate from a stored value. The source is
/// dedicated and small by construction, so a bounded full read is both cheap and the option
/// with the least surface area.</para>
/// </remarks>
internal sealed class FeatureStreamConformanceService
{
    private const string ConformanceProtocol = "Conformance";

    /// <summary>
    /// ASCII unit separator used between digest fields. Explicit delimiters keep the hash
    /// unambiguous: without them, differently split key/value pairs could serialize to the
    /// same byte sequence and two distinct baselines would share a digest.
    /// </summary>
    private static ReadOnlySpan<byte> FieldSeparator => [0x1F];

    /// <summary>ASCII record separator used between digest records.</summary>
    private static ReadOnlySpan<byte> RecordSeparator => [0x1E];

    private readonly IOptions<FeatureStreamConformanceOptions> _options;
    private readonly FeatureStreamConformanceRunRegistry _registry;
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IGeometryService _geometryService;
    private readonly FeatureMutationEventService _mutationEventService;
    private readonly DeploymentIdentity _deploymentIdentity;
    private readonly ILogger<FeatureStreamConformanceService> _logger;

    public FeatureStreamConformanceService(
        IOptions<FeatureStreamConformanceOptions> options,
        FeatureStreamConformanceRunRegistry registry,
        FeatureStreamConformanceStore store,
        FeatureMutationEventService mutationEventService,
        DeploymentIdentity deploymentIdentity,
        ILogger<FeatureStreamConformanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _graphProvider = store.GraphProvider;
        _featureReader = store.FeatureReader;
        _featureWriter = store.FeatureWriter;
        _geometryService = store.GeometryService;
        _mutationEventService = mutationEventService ?? throw new ArgumentNullException(nameof(mutationEventService));
        _deploymentIdentity = deploymentIdentity ?? throw new ArgumentNullException(nameof(deploymentIdentity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Projects the anonymous-safe capability advertisement.
    /// </summary>
    public FeatureStreamConformanceCapability DescribeCapability() => _registry.DescribeCapability();

    /// <summary>
    /// Acquires a conformance run lease, binding it to this deployment's immutable revision
    /// and to the configured source.
    /// </summary>
    public async Task<FeatureStreamConformanceResult<FeatureStreamConformanceRunResponse>> LeaseRunAsync(
        FeatureStreamConformanceRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.Value;

        if (!options.Enabled)
        {
            return Disabled<FeatureStreamConformanceRunResponse>();
        }

        // REQ-006: evidence that cannot name the deployment it was produced against is not
        // evidence. Refuse to start rather than emit an unbindable run.
        var revision = _deploymentIdentity.Revision;
        if (options.RequireDeploymentRevision && string.IsNullOrEmpty(revision))
        {
            return FeatureStreamConformanceResult<FeatureStreamConformanceRunResponse>.Fail(
                FeatureStreamConformanceFailure.DeploymentRevisionUnavailable,
                "This deployment reports no immutable revision, so a conformance run could not be bound to the code under review.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedDeploymentRevision) &&
            !string.Equals(request.ExpectedDeploymentRevision, revision, StringComparison.Ordinal))
        {
            return FeatureStreamConformanceResult<FeatureStreamConformanceRunResponse>.Fail(
                FeatureStreamConformanceFailure.DeploymentRevisionMismatch,
                "The requested deployment revision does not match this deployment.");
        }

        var source = await ResolveSourceAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return SourceUnavailable<FeatureStreamConformanceRunResponse>();
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedServiceId) &&
            !string.Equals(request.ExpectedServiceId, source.ServiceId, StringComparison.OrdinalIgnoreCase))
        {
            return FeatureStreamConformanceResult<FeatureStreamConformanceRunResponse>.Fail(
                FeatureStreamConformanceFailure.SourceIdentityMismatch,
                "The requested conformance source does not match the source this deployment provisions.");
        }

        var ttl = ResolveTtl(request.TtlSeconds, options);
        var run = _registry.TryLease(Sanitize(request.ClientLabel), ttl, revision ?? string.Empty);
        if (run is null)
        {
            return FeatureStreamConformanceResult<FeatureStreamConformanceRunResponse>.Fail(
                FeatureStreamConformanceFailure.LeaseUnavailable,
                $"All {options.MaxConcurrentRuns} conformance leases are currently held.");
        }

        var records = await ReadControlledRecordsAsync(source, cancellationToken).ConfigureAwait(false);
        var baseline = ComputeBaselineDigest(records);

        FeatureStreamConformanceLog.RunLeased(_logger, run.RunId, source.ServiceId, source.PublicLayerId, run.ExpiresAt);

        return FeatureStreamConformanceResult<FeatureStreamConformanceRunResponse>.Success(
            new FeatureStreamConformanceRunResponse
            {
                RunId = run.RunId.ToString("N"),
                RunToken = run.Token,
                RunMarker = run.Marker,
                ServiceId = source.ServiceId,
                LayerId = source.PublicLayerId,
                RunIdField = options.RunIdField,
                ExpiresAt = run.ExpiresAt,
                RemainingMutations = run.MaxMutations,
                MaxRecords = run.MaxRecords,
                DeploymentRevision = revision ?? string.Empty,
                BaselineDigest = baseline.Digest,
                BaselineRecordCount = baseline.RecordCount
            });
    }

    /// <summary>
    /// Performs one controlled mutation on behalf of a leased run.
    /// </summary>
    public async Task<FeatureStreamConformanceResult<FeatureStreamConformanceMutationResponse>> MutateAsync(
        HttpContext context,
        Guid runId,
        string? runToken,
        FeatureStreamConformanceMutationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var options = _options.Value;

        if (!options.Enabled)
        {
            return Disabled<FeatureStreamConformanceMutationResponse>();
        }

        var operation = FeatureStreamConformanceOperations.Normalize(request.Operation);
        if (operation is null)
        {
            return FeatureStreamConformanceResult<FeatureStreamConformanceMutationResponse>.Fail(
                FeatureStreamConformanceFailure.InvalidRequest,
                $"Unsupported conformance operation. Supported operations: {string.Join(", ", FeatureStreamConformanceOperations.All)}.");
        }

        var run = _registry.Resolve(runId, runToken);
        if (run is null)
        {
            return RunNotFound<FeatureStreamConformanceMutationResponse>();
        }

        var source = await ResolveSourceAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return SourceUnavailable<FeatureStreamConformanceMutationResponse>();
        }

        var createsRecord = operation == FeatureStreamConformanceOperations.Insert;
        var ordinal = run.TryClaimMutation(createsRecord);
        if (ordinal is null)
        {
            return createsRecord && run.OwnedRecordCount >= run.MaxRecords
                ? FeatureStreamConformanceResult<FeatureStreamConformanceMutationResponse>.Fail(
                    FeatureStreamConformanceFailure.RecordBudgetExhausted,
                    $"This run already holds its maximum of {run.MaxRecords} controlled records.")
                : FeatureStreamConformanceResult<FeatureStreamConformanceMutationResponse>.Fail(
                    FeatureStreamConformanceFailure.MutationBudgetExhausted,
                    $"This run has spent its budget of {run.MaxMutations} mutations.");
        }

        try
        {
            var mutated = operation == FeatureStreamConformanceOperations.Insert
                ? await InsertAsync(context, source, run, request.Label, cancellationToken).ConfigureAwait(false)
                : await MutateExistingAsync(context, source, run, operation, request, cancellationToken).ConfigureAwait(false);

            if (!mutated.IsSuccess)
            {
                run.ReleaseClaim();
                return FeatureStreamConformanceResult<FeatureStreamConformanceMutationResponse>.Fail(
                    mutated.Failure,
                    mutated.Message ?? "The controlled mutation could not be applied.");
            }

            FeatureStreamConformanceLog.MutationApplied(_logger, run.RunId, operation, mutated.Value);

            return FeatureStreamConformanceResult<FeatureStreamConformanceMutationResponse>.Success(
                new FeatureStreamConformanceMutationResponse
                {
                    RunId = run.RunId.ToString("N"),
                    Operation = operation,
                    ObjectId = mutated.Value,
                    MutationOrdinal = ordinal.Value,
                    RemainingMutations = Math.Max(0, run.MaxMutations - run.MutationsUsed),
                    OwnedRecords = run.OwnedRecordCount,
                    RunMarker = run.Marker
                });
        }
        catch (OperationCanceledException)
        {
            run.ReleaseClaim();
            throw;
        }
    }

    /// <summary>
    /// Releases a run and deletes every record it owns. Idempotent, so a client can call it
    /// from a <c>finally</c> block without having to know whether an earlier call already
    /// succeeded (REQ-005).
    /// </summary>
    public async Task<FeatureStreamConformanceResult<FeatureStreamConformanceCleanupResponse>> CleanupRunAsync(
        HttpContext context,
        Guid runId,
        string? runToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = _options.Value;

        if (!options.Enabled)
        {
            return Disabled<FeatureStreamConformanceCleanupResponse>();
        }

        var run = _registry.Resolve(runId, runToken);
        if (run is null)
        {
            // A run whose lease already lapsed has had (or will have) its records swept, and
            // an unknown run id must not be distinguishable from someone else's. Both are
            // refused identically; the sweeper remains the durable guarantee.
            return RunNotFound<FeatureStreamConformanceCleanupResponse>();
        }

        var source = await ResolveSourceAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return SourceUnavailable<FeatureStreamConformanceCleanupResponse>();
        }

        var deleted = await DeleteOwnedRecordsAsync(context, source, [runId], cancellationToken).ConfigureAwait(false);
        _registry.Release(runId);

        var remaining = await ReadControlledRecordsAsync(source, cancellationToken).ConfigureAwait(false);
        var baseline = ComputeBaselineDigest(remaining);

        FeatureStreamConformanceLog.RunReleased(_logger, runId, deleted);

        return FeatureStreamConformanceResult<FeatureStreamConformanceCleanupResponse>.Success(
            new FeatureStreamConformanceCleanupResponse
            {
                RunId = runId.ToString("N"),
                DeletedRecords = deleted,
                BaselineDigest = baseline.Digest,
                BaselineRecordCount = baseline.RecordCount,
                BaselineRestored = !remaining.Any(record => record.Marker.HasValue)
            });
    }

    /// <summary>
    /// Operator lever: drops every lease and deletes every controlled record, returning the
    /// conformance source to its immutable baseline regardless of who owned what.
    /// </summary>
    public async Task<FeatureStreamConformanceResult<FeatureStreamConformanceResetResponse>> ResetAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Value.Enabled)
        {
            return Disabled<FeatureStreamConformanceResetResponse>();
        }

        var source = await ResolveSourceAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return SourceUnavailable<FeatureStreamConformanceResetResponse>();
        }

        var released = _registry.ReleaseAll();
        var deleted = await DeleteOwnedRecordsAsync(context, source, runIds: null, cancellationToken).ConfigureAwait(false);
        var remaining = await ReadControlledRecordsAsync(source, cancellationToken).ConfigureAwait(false);
        var baseline = ComputeBaselineDigest(remaining);

        FeatureStreamConformanceLog.SourceReset(_logger, released, deleted);

        return FeatureStreamConformanceResult<FeatureStreamConformanceResetResponse>.Success(
            new FeatureStreamConformanceResetResponse
            {
                ReleasedRuns = released,
                DeletedRecords = deleted,
                BaselineDigest = baseline.Digest,
                BaselineRecordCount = baseline.RecordCount
            });
    }

    /// <summary>
    /// One TTL sweep: reclaims expired leases and deletes every controlled record whose
    /// marker has expired. This is the bound that covers runner process death — the marker
    /// carries its own deadline, so the sweep needs nothing the dead process was holding
    /// (NFR-001).
    /// </summary>
    public async Task<int> SweepAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!_options.Value.Enabled)
        {
            return 0;
        }

        var reclaimed = _registry.ReclaimExpired();
        var source = await ResolveSourceAsync(cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return 0;
        }

        var records = await ReadControlledRecordsAsync(source, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var expired = records
            .Where(record => record.Marker is { } marker
                && (marker.ExpiresAt <= now || !_registry.IsLeased(marker.RunId)))
            .Select(record => record.ObjectId)
            .ToImmutableArray();

        if (expired.IsEmpty)
        {
            return 0;
        }

        var deleted = await DeleteRecordsAsync(context, source, expired, cancellationToken).ConfigureAwait(false);
        FeatureStreamConformanceLog.RecordsSwept(_logger, reclaimed.Count, deleted);
        return deleted;
    }

    // ── source resolution ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the configured conformance source through the shared catalog. Returns null
    /// when the service or layer cannot be resolved or the layer is not writable — the
    /// fail-closed answer, never a substitute source.
    /// </summary>
    private async Task<ConformanceSource?> ResolveSourceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.ServiceId))
        {
            return null;
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = FeatureStreamEndpoints.ResolveStreamService(snapshot, options.ServiceId);
        if (service is null)
        {
            return null;
        }

        var descriptor = FeatureStreamEndpoints.ResolveStreamLayer(snapshot, service, options.LayerId);
        if (descriptor is null)
        {
            return null;
        }

        var fields = descriptor.Resource.SchemaFields
            .Select(field => field.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        // The ownership marker is not optional: without a place to record who owns a record,
        // cleanup could not be ownership-checked and the sweeper could not tell a controlled
        // record from a baseline one. Checked against the published schema when there is one;
        // a resource that publishes no field schema has nothing to check against, so the
        // configuration stands and a wrong field name surfaces as a rejected write instead.
        if (!fields.IsEmpty && !fields.Contains(options.RunIdField))
        {
            FeatureStreamConformanceLog.SourceMissingMarkerField(_logger, options.ServiceId, options.RunIdField);
            return null;
        }

        var labelField = !string.IsNullOrWhiteSpace(options.LabelField)
            && (fields.IsEmpty || fields.Contains(options.LabelField))
            ? options.LabelField
            : null;

        return new ConformanceSource(
            ServiceId: service.Metadata.Name,
            StorageLayerId: descriptor.LayerId,
            PublicLayerId: descriptor.PublicLayerId ?? options.LayerId,
            Srid: descriptor.Resource.ReadSrid() ?? 4326,
            RunIdField: options.RunIdField,
            LabelField: labelField,
            Resource: descriptor.Resource);
    }

    // ── mutations ───────────────────────────────────────────────────────────────

    private async Task<FeatureStreamConformanceResult<long>> InsertAsync(
        HttpContext context,
        ConformanceSource source,
        FeatureStreamConformanceRun run,
        string? label,
        CancellationToken cancellationToken)
    {
        var attributes = BuildAttributes(source, run, label, ordinal: run.OwnedRecordCount);
        var geometry = BuildGeometry(source, run, run.OwnedRecordCount);
        var feature = new Feature { Id = 0, Geometry = geometry, Attributes = attributes };
        var batch = FeatureEditBatch.Create(creates: [feature], rollbackOnFailure: true);

        var result = await ApplyAsync(context, source, batch, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.CreatedIds.IsDefaultOrEmpty)
        {
            return FeatureStreamConformanceResult<long>.Fail(
                FeatureStreamConformanceFailure.SourceUnavailable,
                "The conformance source rejected the controlled insert.");
        }

        var objectId = result.CreatedIds[0];
        run.TrackRecord(objectId);
        await PublishAsync(context, source, objectId, "create", cancellationToken).ConfigureAwait(false);
        return FeatureStreamConformanceResult<long>.Success(objectId);
    }

    private async Task<FeatureStreamConformanceResult<long>> MutateExistingAsync(
        HttpContext context,
        ConformanceSource source,
        FeatureStreamConformanceRun run,
        string operation,
        FeatureStreamConformanceMutationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ObjectId is not { } objectId)
        {
            return FeatureStreamConformanceResult<long>.Fail(
                FeatureStreamConformanceFailure.InvalidRequest,
                $"The {operation} operation requires the objectId of a record this run owns.");
        }

        // Ownership is re-read from the stored row, never inferred from the run's own
        // bookkeeping: that is what makes it impossible for one run to mutate or delete
        // another's records even when both hold a valid credential.
        var existing = await ReadRecordAsync(source, objectId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.Value.Marker?.RunId != run.RunId)
        {
            return FeatureStreamConformanceResult<long>.Fail(
                FeatureStreamConformanceFailure.RecordNotOwned,
                "No controlled record with that identifier is owned by this run.");
        }

        if (operation == FeatureStreamConformanceOperations.Delete)
        {
            var deleteBatch = FeatureEditBatch.Create(deletes: [objectId], rollbackOnFailure: true);
            var deleteResult = await ApplyAsync(context, source, deleteBatch, cancellationToken).ConfigureAwait(false);
            if (!deleteResult.IsSuccess || deleteResult.DeletedCount == 0)
            {
                return FeatureStreamConformanceResult<long>.Fail(
                    FeatureStreamConformanceFailure.SourceUnavailable,
                    "The conformance source rejected the controlled delete.");
            }

            run.ForgetRecord(objectId);
            await PublishAsync(context, source, objectId, "delete", cancellationToken).ConfigureAwait(false);
            return FeatureStreamConformanceResult<long>.Success(objectId);
        }

        // `touch` rewrites the record with the values it already has: the state is unchanged
        // but the canonical write path still publishes a change event, so two subscriptions
        // opened at different times observe an identical baseline and an identical mutation.
        var attributes = operation == FeatureStreamConformanceOperations.Touch
            ? existing.Value.Feature.Attributes
            : BuildAttributes(source, run, request.Label, ordinal: null);

        var updated = new Feature
        {
            Id = objectId,
            Geometry = existing.Value.Feature.Geometry,
            Attributes = attributes
        };

        var updateBatch = FeatureEditBatch.Create(updates: [updated], rollbackOnFailure: true);
        var updateResult = await ApplyAsync(context, source, updateBatch, cancellationToken).ConfigureAwait(false);
        if (!updateResult.IsSuccess || updateResult.UpdatedCount == 0)
        {
            return FeatureStreamConformanceResult<long>.Fail(
                FeatureStreamConformanceFailure.SourceUnavailable,
                "The conformance source rejected the controlled update.");
        }

        await PublishAsync(context, source, objectId, "update", cancellationToken).ConfigureAwait(false);
        return FeatureStreamConformanceResult<long>.Success(objectId);
    }

    /// <summary>
    /// Deletes controlled records, optionally narrowed to a set of owning runs. Ownership is
    /// always read from the stored marker, so a record this server did not write — or one
    /// owned by a run outside <paramref name="runIds"/> — is left alone.
    /// </summary>
    private async Task<int> DeleteOwnedRecordsAsync(
        HttpContext context,
        ConformanceSource source,
        IReadOnlyCollection<Guid>? runIds,
        CancellationToken cancellationToken)
    {
        var records = await ReadControlledRecordsAsync(source, cancellationToken).ConfigureAwait(false);
        var targets = records
            .Where(record => record.Marker is { } marker && (runIds is null || runIds.Contains(marker.RunId)))
            .Select(record => record.ObjectId)
            .ToImmutableArray();

        return targets.IsEmpty
            ? 0
            : await DeleteRecordsAsync(context, source, targets, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> DeleteRecordsAsync(
        HttpContext context,
        ConformanceSource source,
        ImmutableArray<long> objectIds,
        CancellationToken cancellationToken)
    {
        var batch = FeatureEditBatch.Create(deletes: objectIds, rollbackOnFailure: false);
        var result = await ApplyAsync(context, source, batch, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // A partially applied sweep is left to the next sweep rather than announcing
            // deletes that may not have happened.
            return result.DeletedCount;
        }

        foreach (var objectId in objectIds)
        {
            await PublishAsync(context, source, objectId, "delete", cancellationToken).ConfigureAwait(false);
        }

        return result.DeletedCount;
    }

    /// <summary>
    /// Applies an edit batch through the canonical write path, with the transactional-outbox
    /// scope resolved first so a provider that records change events in the outbox writes
    /// them inside the same transaction as the row mutation.
    /// </summary>
    private async Task<FeatureEditResult> ApplyAsync(
        HttpContext context,
        ConformanceSource source,
        FeatureEditBatch batch,
        CancellationToken cancellationToken)
    {
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            source.StorageLayerId,
            ConformanceProtocol,
            serviceId: source.ServiceId,
            layerSrid: source.Srid,
            geometryChanged: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Activate the scope synchronously in this method so the AsyncLocal flows into
        // ApplyEditsAsync (an async callee's mutation is not observed by its caller).
        using var outboxScope = FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);
        return await _featureWriter
            .ApplyEditsAsync(source.StorageLayerId, batch, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the change event for a committed conformance mutation. No-ops when the
    /// active provider records events through the transactional outbox, exactly like every
    /// protocol adapter's post-commit publish.
    /// </summary>
    private async Task PublishAsync(
        HttpContext context,
        ConformanceSource source,
        long objectId,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_mutationEventService.OutboxEnabled)
        {
            return;
        }

        // Re-read the committed row so the envelope carries the complete after-image a
        // streaming subscriber needs; a delete has none.
        Feature? mutationFeature = null;
        if (operation != "delete")
        {
            var reloaded = await ReadRecordAsync(source, objectId, cancellationToken).ConfigureAwait(false);
            mutationFeature = reloaded?.Feature;
        }

        try
        {
            await _mutationEventService.PublishAsync(
                context,
                source.StorageLayerId,
                objectId,
                operation,
                ConformanceProtocol,
                CancellationToken.None,
                mutationFeature: mutationFeature,
                serviceId: source.ServiceId,
                layerSrid: source.Srid).ConfigureAwait(false);
        }
        // Intentionally generic: the mutation already committed, so a publish failure must
        // not surface as a request failure and must not strand a controlled record.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureStreamConformanceLog.PublishFailed(_logger, source.StorageLayerId, objectId, ex);
        }
    }

    // ── reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bounded read of the conformance source with the ownership marker parsed in process.
    /// </summary>
    private async Task<IReadOnlyList<ConformanceRecord>> ReadControlledRecordsAsync(
        ConformanceSource source,
        CancellationToken cancellationToken)
    {
        var page = await _featureReader.QueryAsync(
            source.StorageLayerId,
            new FeatureQuery
            {
                IncludeNullGeometry = true,
                Limit = _options.Value.MaxSweepRecords
            },
            cancellationToken).ConfigureAwait(false);

        var records = new List<ConformanceRecord>(page.Items.Length);
        foreach (var feature in page.Items)
        {
            feature.Attributes.TryGetValue(source.RunIdField, out var stored);
            records.Add(new ConformanceRecord(
                feature.Id,
                FeatureStreamConformanceMarker.TryParse(stored, out var marker) ? marker : null,
                feature));
        }

        return records;
    }

    private async Task<ConformanceRecord?> ReadRecordAsync(
        ConformanceSource source,
        long objectId,
        CancellationToken cancellationToken)
    {
        var page = await _featureReader.QueryAsync(
            source.StorageLayerId,
            new FeatureQuery
            {
                ObjectIds = [objectId],
                IncludeNullGeometry = true,
                Limit = 1
            },
            cancellationToken).ConfigureAwait(false);

        if (page.Items.Length == 0)
        {
            return null;
        }

        var feature = page.Items[0];
        feature.Attributes.TryGetValue(source.RunIdField, out var stored);
        return new ConformanceRecord(
            feature.Id,
            FeatureStreamConformanceMarker.TryParse(stored, out var marker) ? marker : null,
            feature);
    }

    /// <summary>
    /// Content digest of the source's immutable baseline: every record no run owns, hashed
    /// in object-id order over the identifier and the canonicalized attribute set. Two runs
    /// that see the same digest saw the same baseline, and a run whose cleanup digest matches
    /// its lease digest demonstrably put the source back the way it found it.
    /// </summary>
    private static BaselineDigest ComputeBaselineDigest(IReadOnlyList<ConformanceRecord> records)
    {
        var baseline = records
            .Where(record => record.Marker is null)
            .OrderBy(record => record.ObjectId)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> idBuffer = stackalloc byte[sizeof(long)];
        foreach (var record in baseline)
        {
            BinaryPrimitives.WriteInt64BigEndian(idBuffer, record.ObjectId);
            hash.AppendData(idBuffer);
            foreach (var attribute in record.Feature.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(attribute.Key));
                hash.AppendData(FieldSeparator);
                hash.AppendData(Encoding.UTF8.GetBytes(FormatAttribute(attribute.Value)));
                hash.AppendData(FieldSeparator);
            }

            hash.AppendData(RecordSeparator);
        }

        return new BaselineDigest(
            string.Concat("sha256:", Convert.ToHexStringLower(hash.GetHashAndReset())),
            baseline.Length);
    }

    /// <summary>
    /// Stand-in for a null attribute in the digest. Distinct from an empty string so a null
    /// and an empty value do not hash identically.
    /// </summary>
    private const string NullAttributeSentinel = "<null>";

    private static string FormatAttribute(object? value)
        => value switch
        {
            null => NullAttributeSentinel,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    // ── record construction ─────────────────────────────────────────────────────

    private static ImmutableDictionary<string, object?> BuildAttributes(
        ConformanceSource source,
        FeatureStreamConformanceRun run,
        string? label,
        int? ordinal)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        builder[source.RunIdField] = run.Marker;
        if (source.LabelField is { } labelField)
        {
            builder[labelField] = Sanitize(label)
                ?? (ordinal is { } index
                    ? string.Concat("conformance-", index.ToString(CultureInfo.InvariantCulture))
                    : "conformance");
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Places controlled records on a deterministic point derived from the run id, so a run's
    /// records never collide with the baseline geometry and two runs never write the same
    /// coordinate. The point stays inside the WGS84 domain regardless of the run id.
    /// </summary>
    private byte[]? BuildGeometry(ConformanceSource source, FeatureStreamConformanceRun run, int ordinal)
    {
        var seed = (uint)run.RunId.GetHashCode();
        var longitude = -180d + ((seed % 3600u) / 10d);
        var latitude = -85d + (((seed / 3600u) % 1700u) / 10d) + (ordinal * 0.001d);
        var geoJson = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"type\":\"Point\",\"coordinates\":[{0:0.####},{1:0.####}]}}",
            longitude,
            Math.Clamp(latitude, -85d, 85d));

        return _geometryService.ConvertGeoJsonToWkb(geoJson, source.Srid);
    }

    private static TimeSpan ResolveTtl(int? requestedSeconds, FeatureStreamConformanceOptions options)
    {
        if (requestedSeconds is not { } seconds || seconds <= 0)
        {
            return options.RunTtl;
        }

        var requested = TimeSpan.FromSeconds(seconds);
        return requested > options.MaxRunTtl ? options.MaxRunTtl : requested;
    }

    /// <summary>
    /// Bounds and strips a caller-supplied label before it reaches storage or a log.
    /// </summary>
    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        const int maxLength = 64;
        if (trimmed.Length > maxLength)
        {
            trimmed = trimmed[..maxLength];
        }

        return new string([.. trimmed.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')]);
    }

    private static FeatureStreamConformanceResult<T> Disabled<T>()
        => FeatureStreamConformanceResult<T>.Fail(
            FeatureStreamConformanceFailure.Disabled,
            "This deployment does not provision a controlled-conformance source.");

    private static FeatureStreamConformanceResult<T> SourceUnavailable<T>()
        => FeatureStreamConformanceResult<T>.Fail(
            FeatureStreamConformanceFailure.SourceUnavailable,
            "The configured controlled-conformance source could not be resolved as a writable layer carrying the ownership marker field.");

    private static FeatureStreamConformanceResult<T> RunNotFound<T>()
        => FeatureStreamConformanceResult<T>.Fail(
            FeatureStreamConformanceFailure.RunNotFound,
            "No live conformance run matches that identifier and token.");

    private readonly record struct BaselineDigest(string Digest, int RecordCount);

    private readonly record struct ConformanceRecord(
        long ObjectId,
        FeatureStreamConformanceMarker? Marker,
        Feature Feature);

    private sealed record ConformanceSource(
        string ServiceId,
        int StorageLayerId,
        int PublicLayerId,
        int Srid,
        string RunIdField,
        string? LabelField,
        MetadataV2Resource Resource);
}

/// <summary>
/// Storage collaborators the controlled-conformance workflow needs, bundled so the workflow
/// itself stays inside the injected-collaborator ceiling (the pattern
/// <c>FeatureStreamDependencies</c> uses for the stream endpoints).
/// </summary>
internal sealed class FeatureStreamConformanceStore
{
    public FeatureStreamConformanceStore(
        IMetadataV2GraphProvider graphProvider,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IGeometryService geometryService)
    {
        GraphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        GeometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
    }

    /// <summary>Catalog snapshot provider used to resolve the dedicated conformance source.</summary>
    public IMetadataV2GraphProvider GraphProvider { get; }

    /// <summary>Shared reader used for ownership checks, sweeps, and baseline digests.</summary>
    public IFeatureReader FeatureReader { get; }

    /// <summary>Canonical writer every controlled mutation goes through.</summary>
    public IFeatureWriter FeatureWriter { get; }

    /// <summary>Shared geometry service used to build controlled-record geometry.</summary>
    public IGeometryService GeometryService { get; }
}
