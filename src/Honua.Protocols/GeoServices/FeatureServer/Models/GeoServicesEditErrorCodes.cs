// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

// These numeric codes are a stable client contract: each per-feature failure maps to one code, and a published code must not be renumbered or given a new meaning.
public static class GeoServicesEditErrorCodes
{
    public const int GenericFailure = 1000;

    public const int InvalidObjectId = 1001;

    public const int NotFound = 1002;

    public const int DeleteNotFound = 1003;

    // An update conflict uses HTTP 412 semantics, and the client may re-read and retry.
    public const int UpdateConflict = 1004;

    // A lock uses HTTP 423 semantics and leaves the stored row untouched; lock-aware providers that report 423 share this code.
    public const int FeatureLocked = 1005;

    public const int ValidationFailed = 1006;

    // An owner-policy denial is an authorization failure, not a conflict.
    public const int NotPermitted = 1007;

    public const int OperationRolledBack = 1008;

    // A lost commit acknowledgement means the row may have been written, so the caller must not assume it was not, and the reservation stays held.
    public const int CommitOutcomeUnknown = 1009;

    // The mapper never inspects the free-form message; it keys only on typed flags and the numeric code, and a not-found splits by operation.
    public static int ClassifyWriterFailure(EditOperationResult result, FeatureEditOperationKind kind)
    {
        // Optimistic-concurrency conflict (update-update). The writer flags this explicitly via
        // EditOperationResult.PreconditionFailed (ErrorCode == 412); honour both the typed flag
        // and the numeric code so it classifies regardless of how the result was constructed.
        if (result.IsPreconditionFailure || result.ErrorCode == EditOperationResult.PreconditionFailedErrorCode)
        {
            return UpdateConflict;
        }

        // Indeterminate outcome, classified ahead of everything else: the writer is telling us the
        // row MAY have committed. Collapsing it into a definite failure code loses that, and callers
        // that key off the code then treat a possibly-committed write as one that never happened.
        if (result.IsCommitOutcomeUnknown)
        {
            return CommitOutcomeUnknown;
        }

        return result.ErrorCode switch
        {
            // HTTP 404 — the row was gone by the time the write executed (a race that slipped
            // past the handler's pre-read). Reported per operation kind.
            404 => kind == FeatureEditOperationKind.Delete ? DeleteNotFound : NotFound,
            // HTTP 423 — a lock-aware writer reported the feature as locked.
            423 => FeatureLocked,
            // HTTP 403 — the written row would fall outside the caller's row-level security
            // partition (honua-server#6103); an authorization failure, not a conflict.
            EditOperationResult.NotPermittedErrorCode => NotPermitted,
            _ => GenericFailure,
        };
    }
}
