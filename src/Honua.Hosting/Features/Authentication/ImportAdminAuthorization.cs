// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Canonical caller-authorization decision for file-import mutations, shared by
/// the REST upload and MCP inline-ingest transport adapters.
/// </summary>
public static class ImportAdminAuthorization
{
    /// <summary>
    /// Returns whether the caller has REST-equivalent admin-write authority for
    /// an import mutation. OAuth scopes only narrow that authority.
    /// </summary>
    public static async Task<bool> IsAuthorizedAsync(
        HttpContext transportContext,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportContext);
        ArgumentNullException.ThrowIfNull(principal);

        if (!await OperationAdminAuthorization.IsAuthorizedAsync(
                transportContext,
                principal,
                OperationSideEffectClass.CreatesMetadata,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!OperatorScopeCatalog.IsScopeGoverned(principal))
        {
            return true;
        }

        var scopes = OperatorScopeCatalog.CollectRecognizedScopes(principal);
        return OperatorScopeCatalog.PermitsOperation(scopes, OperatorOperation.Create);
    }
}
