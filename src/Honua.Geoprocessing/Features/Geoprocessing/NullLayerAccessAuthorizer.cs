// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// Permissive <see cref="ILayerAccessAuthorizer"/> used ONLY by the collaborator-level
/// <see cref="GeoprocessingJobService"/> constructor, which hand-wires individual
/// dependencies for tests and the Redis-free dev CLI (honua-server#3046).
/// </summary>
/// <remarks>
/// It is deliberately NOT registered in DI: the production
/// <see cref="GeoprocessingJobAuthorizer"/> takes a required
/// <see cref="ILayerAccessAuthorizer"/>, so a host that failed to register the real
/// implementation fails loudly at container resolution rather than silently degrading
/// to allow-all. Keeping the null object here preserves the pre-#3046 behavior of the
/// hand-constructed test doubles that never exercised layer authorization.
/// </remarks>
internal sealed class NullLayerAccessAuthorizer : ILayerAccessAuthorizer
{
    /// <summary>Shared singleton instance.</summary>
    public static NullLayerAccessAuthorizer Instance { get; } = new();

    private NullLayerAccessAuthorizer()
    {
    }

    /// <inheritdoc />
    public Task<AccessDecision> AuthorizeLayerAsync(
        ClaimsPrincipal principal,
        int layerId,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AccessDecision.Allowed());
}
