// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// GeoServices-backed <see cref="IReplicaEditApplier"/> that applies resolved replica upload edits
/// through the shared FeatureServer applyEdits pipeline (<see cref="FeatureServerEditsHandler"/>), so
/// uploaded edits reuse the same validation, authorization, geometry conversion, telemetry, and
/// transactional outbox behavior as ordinary edits rather than the replica path issuing its own data
/// access (#1272).
/// </summary>
/// <remarks>
/// The canonical <see cref="IReplicaSyncService"/> owns conflict detection and only passes the edits
/// it decided to apply; this applier maps each <see cref="ReplicaUploadEdit"/> back to its GeoServices
/// wire payload and dispatches a single <c>applyEdits</c> per layer. The synchronize adapter's
/// <c>rollbackOnFailure</c> parameter flows through to <see cref="ApplyEditsRequest.RollbackOnFailure"/>:
/// when true, the shared edit pipeline wraps the layer's edits in an explicit transaction so a single
/// bad row rolls back the whole layer batch (Esri sync semantics, #2136); when false the upload is
/// applied best-effort per row (the prior synchronize behavior).
/// </remarks>
internal sealed class FeatureServerReplicaEditApplier : IReplicaEditApplier
{
    private readonly FeatureServerEditsHandler _editsHandler;
    private readonly EditLimits _editLimits;

    public FeatureServerReplicaEditApplier(FeatureServerEditsHandler editsHandler, EditLimits editLimits)
    {
        _editsHandler = editsHandler ?? throw new ArgumentNullException(nameof(editsHandler));
        _editLimits = editLimits;
    }

    public async Task<ReplicaLayerApplyResult> ApplyAsync(
        string serviceId,
        int publicLayerId,
        ImmutableArray<ReplicaUploadEdit> edits,
        bool rollbackOnFailure,
        CancellationToken cancellationToken = default)
    {
        if (edits.IsDefaultOrEmpty)
        {
            return new ReplicaLayerApplyResult(publicLayerId, 0, 0, 0, Failed: false, FailureMessage: null);
        }

        var adds = new List<GeoServicesFeature>();
        var updates = new List<GeoServicesFeature>();
        var deletes = new List<object>();
        // Request slot of each dispatched row, so per-row outcomes map back to the exact uploaded edit
        // rather than to an object id that several edits may share (#2430).
        var updateSlots = new List<int>();
        var deleteSlots = new List<int>();

        for (var slot = 0; slot < edits.Length; slot++)
        {
            var edit = edits[slot];
            switch (edit.Kind)
            {
                case FeatureEditOperationKind.Create when edit.Payload is GeoServicesFeature add:
                    adds.Add(add);
                    break;
                case FeatureEditOperationKind.Update when edit.Payload is GeoServicesFeature update:
                    updates.Add(update);
                    updateSlots.Add(slot);
                    break;
                case FeatureEditOperationKind.Delete when edit.ObjectId is { } objectId:
                    deletes.Add(objectId);
                    deleteSlots.Add(slot);
                    break;
                default:
                    // A payload that does not match its declared kind is a programming error in the
                    // adapter; treat the layer apply as failed rather than silently dropping the edit.
                    return new ReplicaLayerApplyResult(
                        publicLayerId, 0, 0, 0, Failed: true,
                        FailureMessage: "Uploaded replica edit payload did not match its operation kind.");
            }
        }

        var request = new ApplyEditsRequest
        {
            Adds = adds.Count > 0 ? adds.ToArray() : null,
            Updates = updates.Count > 0 ? updates.ToArray() : null,
            Deletes = deletes.Count > 0 ? deletes.ToArray() : null,
            RollbackOnFailure = rollbackOnFailure,
            RollbackOnFailureExplicitlySet = true,
        };

        var editResult = await _editsHandler.HandleApplyEditsAsync(
            serviceId, publicLayerId, request, _editLimits, cancellationToken).ConfigureAwait(false);

        if (editResult is not JsonHttpResult<ApplyEditsResponse> { Value: { } response })
        {
            // The shared handler returned an error result (validation/auth/etc.). Report the layer as
            // failed; the synchronize adapter maps this to a bad-request without leaking internals.
            return new ReplicaLayerApplyResult(
                publicLayerId, 0, 0, 0, Failed: true,
                FailureMessage: "Uploaded replica edits failed to apply.");
        }

        var appliedAdds = CountSuccessful(response.AddResults);
        var appliedUpdates = CountSuccessful(response.UpdateResults);
        var appliedDeletes = CountSuccessful(response.DeleteResults);
        var failed = !response.Success
            || HasFailure(response.AddResults)
            || HasFailure(response.UpdateResults)
            || HasFailure(response.DeleteResults);

        return new ReplicaLayerApplyResult(
            publicLayerId,
            appliedAdds,
            appliedUpdates,
            appliedDeletes,
            Failed: failed,
            FailureMessage: failed ? "Uploaded replica edits failed to apply." : null,
            CommittedEditIndexes: CollectEditIndexes(response, updateSlots, deleteSlots, committed: true),
            IndeterminateEditIndexes: CollectEditIndexes(response, updateSlots, deleteSlots, committed: false));
    }

    /// <summary>
    /// Collects the request slots of the update/delete rows that definitely committed
    /// (<paramref name="committed"/> true), or those whose commit outcome the writer could not
    /// determine (false). With <c>rollbackOnFailure=false</c> the shared pipeline commits rows
    /// independently, so the layer-wide failed flag cannot tell a caller whether a <em>particular</em>
    /// uploaded edit landed; the conflict recorder needs exactly that (#2430).
    /// </summary>
    /// <remarks>
    /// The two sets are disjoint, and an indeterminate row belongs to neither "committed" nor
    /// "did not commit": treating it as a definite failure recorded the conflict as
    /// <c>ClientEditApplied=false</c>, and a later keepServer then planned a no-op while the client
    /// overwrite may well have been in place.
    /// <para>
    /// Result arrays are index-aligned with the request arrays (Esri applyEdits semantics), so each
    /// result maps back to the slot recorded when the row was dispatched. Adds are excluded: they mint
    /// new server-assigned ids and can never be the target of an upload conflict.
    /// </para>
    /// </remarks>
    private static ImmutableArray<int> CollectEditIndexes(
        ApplyEditsResponse response,
        List<int> updateSlots,
        List<int> deleteSlots,
        bool committed)
    {
        var slotsOut = ImmutableArray.CreateBuilder<int>();
        Append(response.UpdateResults, updateSlots);
        Append(response.DeleteResults, deleteSlots);
        return slotsOut.ToImmutable();

        void Append(EditResult[]? results, List<int> slots)
        {
            if (results is null)
            {
                return;
            }

            var count = Math.Min(results.Length, slots.Count);
            for (var index = 0; index < count; index++)
            {
                var result = results[index];
                var matches = committed
                    ? result.Success
                    : !result.Success && result.Error?.Code == GeoServicesEditErrorCodes.CommitOutcomeUnknown;
                if (matches)
                {
                    slotsOut.Add(slots[index]);
                }
            }
        }
    }

    private static int CountSuccessful(EditResult[]? results)
        => results is null ? 0 : results.Count(static r => r.Success);

    private static bool HasFailure(EditResult[]? results)
        => results is not null && Array.Exists(results, static r => !r.Success);
}
