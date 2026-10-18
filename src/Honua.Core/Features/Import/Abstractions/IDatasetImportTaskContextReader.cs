// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Read-only projection of an exact retained task for its owner or the deployment-linked
/// workspace agent. This port never authorizes transfer, upload admission, jobs or mutations.
/// </summary>
public interface IDatasetImportTaskContextReader
{
    /// <summary>
    /// Returns original typed context under fresh credential, source and destination-read
    /// authority. Delegation requires the server-retained agent identity and equality of the
    /// task owner and workspace owner; arbitrary membership cannot grant this projection.
    /// </summary>
    Task<DatasetImportTask?> ReadAsync(ClaimsPrincipal principal, string workspaceId,
        string taskId, long revision, string contentHash, CancellationToken cancellationToken = default);
}
