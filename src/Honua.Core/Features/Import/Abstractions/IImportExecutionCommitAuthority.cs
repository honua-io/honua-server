// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>Revalidates an accepted import against its actual job, immutable intent and current canonical policy.</summary>
public interface IImportExecutionCommitAuthority
{
    /// <summary>
    /// Checks the actual execution scope and exact original request before staging and again
    /// inside the destination transaction. It never authorizes replay of an already committed import.
    /// </summary>
    /// <param name="request">Original import options with the verified materialized source handle.</param>
    /// <param name="physicalSchema">Provider-resolved destination schema.</param>
    /// <param name="physicalTable">Provider-resolved destination table.</param>
    /// <param name="cancellationToken">Cancellation and execution deadline.</param>
    /// <returns>Current immutable job binding and destination precondition derived from sealed intent.</returns>
    Task<ImportExecutionCommitBinding> RequireCurrentAsync(ImportRequest request, string physicalSchema,
        string physicalTable, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persistence-only lookup of an original committed result. Callers must separately check
/// current observation authority; possession of an operation ID or fingerprint grants no access.
/// </summary>
public interface IImportCommittedResultReader
{
    /// <summary>Reads the original immutable result without granting execution authority or touching source data.</summary>
    /// <param name="operationId">Canonical accepted job identifier.</param>
    /// <param name="intentFingerprint">Full immutable job-intent fingerprint, including original ownership.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The original result, or null when no committed receipt exists; a mismatched intent is refused.</returns>
    Task<ImportResult?> TryReadAsync(string operationId, string intentFingerprint,
        CancellationToken cancellationToken = default);
}
