// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>Seals task authority atomically against renewal before durable queue acceptance.</summary>
public interface IDatasetImportAdmissionProvider
{
    /// <summary>Prepares once under the actual executor's current authority; a replay returns the original sealed record.</summary>
    /// <param name="executor">Fresh framework-authenticated actual executor.</param>
    /// <param name="workspaceId">Original workspace.</param>
    /// <param name="taskId">Original server-owned task.</param>
    /// <param name="taskRevision">Immutable task revision.</param>
    /// <param name="taskContentHash">Immutable task content hash.</param>
    /// <param name="authorityRevision">Reviewed current authority revision.</param>
    /// <param name="authorityFingerprint">Reviewed current authority fingerprint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The original sealed record, or null when the exact executor or task is unauthorized.</returns>
    Task<DatasetImportPreparedAdmission?> PrepareAsync(ClaimsPrincipal executor, string workspaceId, string taskId,
        long taskRevision, string taskContentHash, long authorityRevision, string authorityFingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Reads an existing preparation without dispatch, mutation, or inferred safe reset.</summary>
    /// <param name="executor">Fresh framework-authenticated actual executor.</param>
    /// <param name="workspaceId">Original workspace.</param>
    /// <param name="taskId">Original task.</param>
    /// <param name="taskRevision">Original immutable revision.</param>
    /// <param name="taskContentHash">Original immutable content hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The retained preparation, or null when absent or unauthorized.</returns>
    Task<DatasetImportPreparedAdmission?> ReadAsync(ClaimsPrincipal executor, string workspaceId, string taskId,
        long taskRevision, string taskContentHash, CancellationToken cancellationToken = default);
}
