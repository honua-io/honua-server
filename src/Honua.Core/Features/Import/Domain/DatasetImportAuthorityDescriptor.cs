// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>Authorized current task-authority metadata without credential snapshots or policy claims.</summary>
/// <param name="TaskId">Original task.</param>
/// <param name="TaskRevision">Original immutable task revision.</param>
/// <param name="TaskContentHash">Original immutable content hash.</param>
/// <param name="ExecutorActorId">Actual immutable executor owner.</param>
/// <param name="InitiatingHumanActorId">Optional original human initiator.</param>
/// <param name="OriginatingTurnId">Original retained turn.</param>
/// <param name="OriginalTurnHash">Original turn content hash.</param>
/// <param name="AuthorityRevision">Current separate authority revision.</param>
/// <param name="AuthorityFingerprint">Current credential-authority fingerprint.</param>
/// <param name="PolicyContextFingerprint">Immutable original policy-context fingerprint.</param>
/// <param name="CredentialExpiresAt">Current sealed authority expiry, independent of upload deadline.</param>
/// <param name="UploadAdmissionDeadline">Original immutable deadline.</param>
/// <param name="PreparedOperationId">Original prepared operation, if any; preparation is not acceptance proof.</param>
public sealed record DatasetImportAuthorityDescriptor(string TaskId, long TaskRevision, string TaskContentHash,
    string ExecutorActorId, string? InitiatingHumanActorId, string OriginatingTurnId, string OriginalTurnHash,
    long AuthorityRevision, string AuthorityFingerprint, string PolicyContextFingerprint,
    DateTimeOffset CredentialExpiresAt, DateTimeOffset UploadAdmissionDeadline, string? PreparedOperationId);
