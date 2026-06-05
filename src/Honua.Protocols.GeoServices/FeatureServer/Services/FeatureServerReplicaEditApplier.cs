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
/// wire payload and dispatches a single <c>applyEdits</c> per layer. Edits are applied with
/// <see cref="ApplyEditsRequest.RollbackOnFailure"/> set to <c>false</c> so a single bad row does not
/// discard the rest of the upload (matching the prior synchronize behavior and Esri sync semantics).
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
        CancellationToken cancellationToken = default)
    {
        if (edits.IsDefaultOrEmpty)
        {
            return new ReplicaLayerApplyResult(publicLayerId, 0, 0, 0, Failed: false, FailureMessage: null);
        }

        var adds = new List<GeoServicesFeature>();
        var updates = new List<GeoServicesFeature>();
        var deletes = new List<object>();

        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case FeatureEditOperationKind.Create when edit.Payload is GeoServicesFeature add:
                    adds.Add(add);
                    break;
                case FeatureEditOperationKind.Update when edit.Payload is GeoServicesFeature update:
                    updates.Add(update);
                    break;
                case FeatureEditOperationKind.Delete when edit.ObjectId is { } objectId:
                    deletes.Add(objectId);
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
            RollbackOnFailure = false,
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
            FailureMessage: failed ? "Uploaded replica edits failed to apply." : null);
    }

    private static int CountSuccessful(EditResult[]? results)
        => results is null ? 0 : results.Count(static r => r.Success);

    private static bool HasFailure(EditResult[]? results)
        => results is not null && Array.Exists(results, static r => !r.Success);
}
