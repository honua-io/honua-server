// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>Reads current authority metadata without guessing a revision or changing immutable task intent.</summary>
public interface IDatasetImportAuthorityDescriptorReader
{
    /// <summary>Authorizes the actual executor or explicit contributor and reads current metadata without mutation.</summary>
    /// <param name="caller">Actual framework-authenticated caller.</param>
    /// <param name="workspaceId">Original workspace.</param>
    /// <param name="taskId">Original task.</param>
    /// <param name="revision">Original immutable revision.</param>
    /// <param name="contentHash">Original immutable content hash.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authorized current metadata, or null when absent or unauthorized.</returns>
    Task<DatasetImportAuthorityDescriptor?> ReadAsync(ClaimsPrincipal caller, string workspaceId, string taskId,
        long revision, string contentHash, CancellationToken cancellationToken = default);
}
