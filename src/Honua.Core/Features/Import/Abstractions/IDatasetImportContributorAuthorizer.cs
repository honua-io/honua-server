// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>Authorizes an initiating human separately from strict executor ownership.</summary>
public interface IDatasetImportContributorAuthorizer
{
    /// <summary>Rechecks actual contributor and executor authority against one original immutable task.</summary>
    /// <param name="contributor">Actual framework-authenticated caller, never an executor substitute.</param>
    /// <param name="workspaceId">Original workspace.</param>
    /// <param name="taskId">Original server-owned task.</param>
    /// <param name="revision">Original immutable task revision.</param>
    /// <param name="contentHash">Original immutable task content hash.</param>
    /// <param name="requireWrite">True for upload, resume, cancellation or acceptance mutations; false for observation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The immutable derived contribution identity, or null when either principal lacks current authority.</returns>
    Task<DatasetImportContribution?> AuthorizeAsync(ClaimsPrincipal contributor, string workspaceId, string taskId,
        long revision, string contentHash, bool requireWrite, CancellationToken cancellationToken = default);
}
