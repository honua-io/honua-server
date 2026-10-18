// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>Server-derived contribution authorization; it never changes transfer or job ownership.</summary>
/// <param name="Task">Exact immutable server-owned task.</param>
/// <param name="ExecutorActorId">Actual executor and immutable task/session/job owner.</param>
/// <param name="ContributorActorId">Actual authenticated human contributing this request.</param>
/// <param name="InitiatingHumanActorId">Original server-retained initiating human.</param>
/// <param name="OriginatingTurnId">Original retained human turn.</param>
/// <param name="OriginalTurnHash">Immutable original turn content hash.</param>
/// <param name="AuthorityRevision">Current separate executor authority revision.</param>
/// <param name="AuthorityFingerprint">Current executor credential fingerprint.</param>
/// <param name="PolicyContextFingerprint">Original trusted executor policy-context fingerprint.</param>
public sealed record DatasetImportContribution(DatasetImportTask Task, string ExecutorActorId, string ContributorActorId,
    string InitiatingHumanActorId, string OriginatingTurnId, string OriginalTurnHash, long AuthorityRevision,
    string AuthorityFingerprint, string PolicyContextFingerprint);
