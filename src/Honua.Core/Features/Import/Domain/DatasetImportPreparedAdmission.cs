// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>Server-retained immutable acceptance preparation; never a client-selected execution authority.</summary>
/// <param name="TaskId">Original server-owned task.</param>
/// <param name="TaskRevision">Original immutable task revision.</param>
/// <param name="TaskContentHash">Original immutable intent hash.</param>
/// <param name="OperationId">Deterministic accepted operation identity, reused after lost replies.</param>
/// <param name="AuthorityRevision">Exact task authority revision sealed before queue admission.</param>
/// <param name="AuthorityFingerprint">Exact sealed authority fingerprint.</param>
/// <param name="ExecutorActorId">Actual executor and durable job owner.</param>
/// <param name="InitiatingHumanActorId">Optional server-derived initiating human, distinct from executor.</param>
/// <param name="OriginatingTurnId">Original retained turn.</param>
/// <param name="OriginalTurnHash">Hash of the original turn independently of the newly created task.</param>
/// <param name="ExecutorAuthority">Server-only sealed credential snapshot; contains no bearer secret.</param>
/// <param name="AcceptBefore">Original immutable admission deadline, never extended by renewal or replay.</param>
public sealed record DatasetImportPreparedAdmission(
    string TaskId, long TaskRevision, string TaskContentHash, string OperationId,
    long AuthorityRevision, string AuthorityFingerprint, string ExecutorActorId, string? InitiatingHumanActorId,
    string OriginatingTurnId, string OriginalTurnHash, AgentCredentialSnapshot ExecutorAuthority, DateTimeOffset AcceptBefore);
