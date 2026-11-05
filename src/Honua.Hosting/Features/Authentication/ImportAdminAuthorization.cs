// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Infrastructure.Authentication;

public static class ImportAdminAuthorization
{
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
