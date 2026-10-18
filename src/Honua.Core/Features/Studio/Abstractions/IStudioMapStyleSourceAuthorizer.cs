// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Abstractions;

/// <summary>Checks a style's published source bindings against the current caller's access.</summary>
public interface IStudioMapStyleSourceAuthorizer
{
    /// <summary>
    /// Returns synchronized portable bindings and, when absent, an initialized map style
    /// resolved from authorized publications. Throws when current source access is denied.
    /// </summary>
    Task<StudioCompositionBody> AuthorizeAsync(ClaimsPrincipal principal, StudioCompositionBody body, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves live catalog bindings using current read authority and validates the
    /// effective appearance against the map's actual sources and visible fields.
    /// </summary>
    Task<StudioCompositionBody> AuthorizeStyleReferencesAsync(ClaimsPrincipal principal,
        StudioCompositionBody body, CancellationToken cancellationToken);
}
