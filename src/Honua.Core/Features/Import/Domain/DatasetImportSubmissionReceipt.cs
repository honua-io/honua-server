// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>Safe observation of one original task preparation or actual accepted durable job; never execution authority.</summary>
/// <param name="TaskId">Original server-issued task identity.</param>
/// <param name="TaskRevision">Immutable task version.</param>
/// <param name="TaskContentHash">Immutable original intent digest.</param>
/// <param name="State">not-prepared, prepared, or accepted. Only accepted names an actual stored job.</param>
/// <param name="OperationId">Original deterministic operation when preparation exists.</param>
/// <param name="AuthorityRevision">Exact original prepared authority revision.</param>
/// <param name="AuthorityFingerprint">Exact prepared credential-authority fingerprint.</param>
/// <param name="PolicyContextFingerprint">Original sealed policy-input fingerprint.</param>
/// <param name="AcceptBefore">Original immutable admission deadline.</param>
/// <param name="Job">Actual independently authorized accepted job, absent for preparation alone.</param>
public sealed record DatasetImportSubmissionReceipt(string TaskId, long TaskRevision, string TaskContentHash, string State,
    string? OperationId = null, long? AuthorityRevision = null, string? AuthorityFingerprint = null,
    string? PolicyContextFingerprint = null, DateTimeOffset? AcceptBefore = null, DatasetImportJobReceipt? Job = null);

/// <summary>Safe projection of an actual durable accepted file-import job with no provider storage identifiers or security context.</summary>
/// <param name="JobId">Actual persisted durable job identity.</param>
/// <param name="LeaseId">Exact accepted execution lease identity; not a reusable capability.</param>
/// <param name="Status">queued, provisioning, running, succeeded, failed, or cancelled.</param>
/// <param name="CreatedAt">Original durable acceptance time.</param>
/// <param name="UpdatedAt">Observed authoritative status time.</param>
/// <param name="CompletedAt">Actual terminal time when present.</param>
/// <param name="PercentComplete">Finite provider progress when present.</param>
/// <param name="Output">Actual persisted successful physical dataset receipt, never reconstructed from task destination.</param>
/// <param name="ErrorCode">Stable safe failure code when terminal failure is recorded.</param>
public sealed record DatasetImportJobReceipt(string JobId, string LeaseId, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt = null, double? PercentComplete = null,
    FileImportOutputReceipt? Output = null, string? ErrorCode = null);
