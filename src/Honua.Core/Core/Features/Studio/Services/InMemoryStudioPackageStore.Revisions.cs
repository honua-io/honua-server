// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public sealed partial class InMemoryStudioPackageStore
{
    private readonly Dictionary<Guid, List<StudioDraftRevision>> _draftRevisions = new();

    public Task<StudioDraftRevision?> GetDraftRevisionAsync(Guid draftId, long generation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var revision = _draftRevisions.TryGetValue(draftId, out var revisions)
                ? revisions.Find(revision => revision.Generation == generation)
                : null;
            return Task.FromResult(revision is null ? null : revision with { Envelope = SnapshotEnvelope(revision.Envelope) });
        }
    }

    public Task<IReadOnlyList<StudioDraftRevisionSummary>> ListDraftRevisionsAsync(
        StudioDraftRevisionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StudioDraftRevisionSummary> result = _draftRevisions.TryGetValue(query.DraftId, out var revisions)
                ? revisions.Where(revision => !query.BeforeGeneration.HasValue || revision.Generation < query.BeforeGeneration.Value)
                    .OrderByDescending(revision => revision.Generation)
                    .Take(Math.Clamp(query.Limit, 1, MaxListLimit))
                    .Select(revision => new StudioDraftRevisionSummary(revision.Generation, revision.UpdatedBy, revision.UpdatedAt))
                    .ToArray()
                : [];
            return Task.FromResult(result);
        }
    }

    private static StudioPackageDraft SnapshotDraft(StudioPackageDraft draft) =>
        draft with { Envelope = SnapshotEnvelope(draft.Envelope) };

    private static StudioPackageEnvelope SnapshotEnvelope(StudioPackageEnvelope envelope) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(envelope, StudioJsonContext.Default.StudioPackageEnvelope),
            StudioJsonContext.Default.StudioPackageEnvelope)!;

    private void ArchiveDraftRevision(StudioPackageDraft draft)
    {
        if (!_draftRevisions.TryGetValue(draft.DraftId, out var revisions))
        {
            revisions = [];
            _draftRevisions.Add(draft.DraftId, revisions);
        }

        revisions.Add(new()
        {
            Generation = draft.Generation,
            Envelope = SnapshotEnvelope(draft.Envelope),
            UpdatedBy = draft.UpdatedBy,
            UpdatedAt = draft.UpdatedAt,
        });
    }
}
