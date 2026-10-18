// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using System.Security.Cryptography;
using System.Text.Json;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>Tracks scheduler writes only to reconcile them against committed canonical publication rows after a failure.</summary>
internal sealed class WorkflowPublicationSchedulerJournal(IWorkflowDefinitionStore inner) : IWorkflowDefinitionStore
{
    private readonly Dictionary<string, WorkflowDefinition?> _before = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowDefinition> _attempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowDefinition> _pendingEnabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _publicationPins = new(StringComparer.Ordinal);
    internal IWorkflowPackageStore? Catalog { get; set; }

    private async Task CaptureAsync(string id, CancellationToken cancellationToken)
    {
        if (!_before.ContainsKey(id)) _before[id] = await inner.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (Catalog is not null)
        {
            if (!_publicationPins.TryGetValue(id, out var pins)) _publicationPins[id] = pins = new(StringComparer.Ordinal);
            foreach (var publication in await Catalog.ListPublicationsAsync(null, cancellationToken).ConfigureAwait(false))
                if (publication.WorkflowDefinitionId == id) pins.Add(Identity(publication));
        }
    }

    public async Task SetAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        await CaptureAsync(definition.WorkflowId, cancellationToken).ConfigureAwait(false);
        _attempted[definition.WorkflowId] = definition;
        if (definition.Trigger?.Enabled == true) _pendingEnabled[definition.WorkflowId] = definition;
        else _pendingEnabled.Remove(definition.WorkflowId);
        await inner.SetAsync(Dormant(definition), cancellationToken).ConfigureAwait(false);
    }
    public async Task<bool> TryCreateAsync(WorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        await CaptureAsync(definition.WorkflowId, cancellationToken).ConfigureAwait(false);
        var created = await inner.TryCreateAsync(Dormant(definition), cancellationToken).ConfigureAwait(false);
        if (created)
        {
            _attempted[definition.WorkflowId] = definition;
            if (definition.Trigger?.Enabled == true) _pendingEnabled[definition.WorkflowId] = definition;
        }
        return created;
    }
    public async Task<bool> DeleteAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        await CaptureAsync(workflowId, cancellationToken).ConfigureAwait(false);
        _pendingEnabled.Remove(workflowId);
        return await inner.DeleteAsync(workflowId, cancellationToken).ConfigureAwait(false);
    }

    private static WorkflowDefinition Dormant(WorkflowDefinition definition)
        => definition.Trigger is { Enabled: true } trigger ? definition with { Trigger = trigger with { Enabled = false } } : definition;

    internal async Task PublishCommittedAsync(IWorkflowPackageStore committed, CancellationToken cancellationToken)
    {
        var publications = await committed.ListPublicationsAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (var (id, definition) in _pendingEnabled)
        {
            var references = publications.Where(publication => publication.WorkflowDefinitionId == id).ToArray();
            if (references.Length == 0) continue;
            if (!_publicationPins.TryGetValue(id, out var pins) || references.Any(publication => !pins.Contains(Identity(publication))))
                throw new InvalidOperationException("The committed workflow publication differs from the scheduler activation intent.");
            if (references.Any(publication => publication.Status == WorkflowPublicationStatus.Active && publication.Schedule?.Enabled != false))
                await inner.SetAsync(definition, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileAsync(IWorkflowPackageStore committed, CancellationToken cancellationToken)
    {
        var publications = await committed.ListPublicationsAsync(null, cancellationToken).ConfigureAwait(false);
        foreach (var (id, original) in _before)
        {
            var references = publications.Where(publication => publication.WorkflowDefinitionId == id).ToArray();
            if (references.Length == 0)
            {
                await inner.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var definition = await inner.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (definition is null)
            {
                // A replacement committed by another writer must never inherit the previous
                // publisher's plan or RLS snapshot just because it reused a definition id.
                if (!_publicationPins.TryGetValue(id, out var pins) || references.Any(publication => !pins.Contains(Identity(publication))))
                    throw new InvalidOperationException("Committed workflow publication requires scheduler reconciliation from its own author snapshot.");
                definition = _attempted.GetValueOrDefault(id) ?? original
                    ?? throw new InvalidOperationException("Committed workflow publication has no recoverable scheduler definition.");
            }
            var enabled = references.Any(publication => publication.Status == WorkflowPublicationStatus.Active && publication.Schedule?.Enabled != false);
            if (definition.Trigger is { } trigger) definition = definition with { Trigger = trigger with { Enabled = enabled } };
            await inner.SetAsync(definition, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Identity(WorkflowPublication publication) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(publication with { Status = default }, WorkflowPackageGovernanceJsonContext.Default.WorkflowPublication)));

    public Task<WorkflowDefinition?> GetAsync(string workflowId, CancellationToken cancellationToken = default)
        => inner.GetAsync(workflowId, cancellationToken);

    public Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken = default)
        => inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<WorkflowDefinition>> ListScheduledAsync(CancellationToken cancellationToken = default)
        => inner.ListScheduledAsync(cancellationToken);

    public Task<bool> TryClaimScheduleFireAsync(
        string workflowId,
        DateTimeOffset fireTime,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
        => inner.TryClaimScheduleFireAsync(workflowId, fireTime, retention, cancellationToken);

    public Task ReleaseScheduleClaimAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
        => inner.ReleaseScheduleClaimAsync(workflowId, fireTime, cancellationToken);

    public Task<DateTimeOffset?> GetScheduleCursorAsync(
        string workflowId,
        CancellationToken cancellationToken = default)
        => inner.GetScheduleCursorAsync(workflowId, cancellationToken);

    public Task AdvanceScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
        => inner.AdvanceScheduleCursorAsync(workflowId, fireTime, cancellationToken);

    public Task SetPendingScheduleCursorAsync(
        string workflowId,
        DateTimeOffset fireTime,
        CancellationToken cancellationToken = default)
        => inner.SetPendingScheduleCursorAsync(workflowId, fireTime, cancellationToken);

    public Task<IReadOnlyList<WorkflowDefinition>> ListEventTriggeredAsync(CancellationToken cancellationToken = default)
        => inner.ListEventTriggeredAsync(cancellationToken);

    public Task<string?> GetTriggerCursorAsync(
        string workflowId,
        string cursorKind,
        CancellationToken cancellationToken = default)
        => inner.GetTriggerCursorAsync(workflowId, cursorKind, cancellationToken);

    public Task SetTriggerCursorAsync(
        string workflowId,
        string cursorKind,
        string marker,
        CancellationToken cancellationToken = default)
        => inner.SetTriggerCursorAsync(workflowId, cursorKind, marker, cancellationToken);

    public Task<bool> TryClaimTriggerFireAsync(
        string workflowId,
        string cursorKind,
        string marker,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
        => inner.TryClaimTriggerFireAsync(workflowId, cursorKind, marker, retention, cancellationToken);

    public Task ReleaseTriggerClaimAsync(
        string workflowId,
        string cursorKind,
        string marker,
        CancellationToken cancellationToken = default)
        => inner.ReleaseTriggerClaimAsync(workflowId, cursorKind, marker, cancellationToken);

}
