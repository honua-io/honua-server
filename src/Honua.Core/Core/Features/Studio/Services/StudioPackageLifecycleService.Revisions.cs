// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public sealed partial class StudioPackageLifecycleService
{
    public async Task<StudioDraftRevision?> GetDraftRevisionAsync(
        Guid draftId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        using var activity = ActivitySource.StartActivity("studio.package.draft.revision.get");
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));
        activity?.SetTag("studio.draft.revision.generation", generation);
        return await _store.GetDraftRevisionAsync(draftId, generation, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StudioDraftRevisionSummary>> ListDraftRevisionsAsync(
        StudioDraftRevisionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var activity = ActivitySource.StartActivity("studio.package.draft.revision.list");
        activity?.SetTag("studio.draft.id", query.DraftId.ToString("D"));
        activity?.SetTag("studio.list.limit", query.Limit);
        var result = await _store.ListDraftRevisionsAsync(query, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.list.count", result.Count);
        return result;
    }

    public async Task<StudioPackageDraft?> RestoreDraftRevisionAsync(
        Guid draftId,
        RestoreStudioDraftRevisionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.TargetGeneration, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.ExpectedGeneration, 1);

        var source = await LoadRestoreSourceAsync(draftId, command, cancellationToken).ConfigureAwait(false);
        if (source is not { } found)
        {
            return null;
        }

        var (existing, revision) = found;

        var envelope = NormalizeEnvelope(NormalizeVersionForReopen(revision.Envelope)) with
        {
            PublicationIntent = existing.Envelope.PublicationIntent,
        };
        using var activity = Start("studio.package.draft.revision.restore", envelope.Family, existing.ItemId);
        activity?.SetTag("studio.draft.id", draftId.ToString("D"));
        activity?.SetTag("studio.draft.revision.generation", command.TargetGeneration);

        var validation = _validator.Validate(envelope);
        var updated = existing with
        {
            Family = envelope.Family,
            Envelope = envelope with { Validation = validation },
            Validation = validation,
            Generation = command.ExpectedGeneration,
            UpdatedBy = command.ActorId,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };

        var stored = await _store.UpdateDraftAsync(updated, cancellationToken).ConfigureAwait(false);
        activity?.SetTag("studio.validation.status", stored?.Validation.Status.ToString());
        return stored;
    }

    private async Task<(StudioPackageDraft Existing, StudioDraftRevision Revision)?> LoadRestoreSourceAsync(
        Guid draftId,
        RestoreStudioDraftRevisionCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (existing.Generation != command.ExpectedGeneration)
        {
            throw new InvalidOperationException("Stale draft generation; refresh and retry.");
        }

        var revision = await _store.GetDraftRevisionAsync(draftId, command.TargetGeneration, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Studio draft revision '{command.TargetGeneration}' was not found.");
        return (existing, revision);
    }
}
