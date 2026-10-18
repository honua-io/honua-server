// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>Authorizes a retained exact import task without depending on mutable workspace focus.</summary>
public interface IDatasetImportTaskProvider
{
    /// <summary>
    /// Returns the exact immutable task only after fresh stored credential, actor, tenant,
    /// target, workspace membership, source and destination checks. Write access additionally
    /// requires current import/Create authority. Returns null on denied or missing bindings;
    /// observation never creates sessions, dispatches imports or grants approval.
    /// </summary>
    Task<DatasetImportTask?> AuthorizeAsync(ClaimsPrincipal principal, string workspaceId,
        string taskId, long revision, string contentHash, bool requireWrite,
        CancellationToken cancellationToken = default);
}

/// <summary>Current destination-specific access, separate from broad import/Create permission.</summary>
public interface IDatasetImportDestinationAuthorizer
{
    /// <summary>Checks this exact operational destination under current actor/resource grants.</summary>
    Task<bool> IsAuthorizedAsync(ClaimsPrincipal principal, string targetId,
        DatasetImportDestination destination, bool requireWrite, CancellationToken cancellationToken = default);
}
