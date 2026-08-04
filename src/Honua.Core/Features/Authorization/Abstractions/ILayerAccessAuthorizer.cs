// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Principal-based, request-context-free seam for per-layer authorization
/// (honua-server#3046). Resolves a catalog layer id against the Metadata v2 graph
/// and evaluates the same grant-then-access-policy decision the HTTP layer
/// validators apply, but for callers that only hold a <see cref="ClaimsPrincipal"/>
/// (the geoprocessing submit pipeline, workflow orchestration, MCP tools).
/// </summary>
/// <remarks>
/// This is the shared enforcement point that keeps the asynchronous job surface at
/// authorization parity with the synchronous query surfaces: both ultimately consult
/// <c>IPermissionResolver</c> first and fall back to the coarse
/// <c>AccessPolicy</c> seam, so a deployment that denies a layer to a role denies it
/// consistently whether the layer is read through a protocol query or through a
/// geoprocessing job.
/// </remarks>
public interface ILayerAccessAuthorizer
{
    /// <summary>
    /// Evaluates whether <paramref name="principal"/> may perform
    /// <paramref name="operation"/> on the catalog layer identified by
    /// <paramref name="layerId"/>.
    /// </summary>
    /// <param name="principal">The principal the decision is evaluated against.</param>
    /// <param name="layerId">The catalog layer index (Metadata v2 publication layer index).</param>
    /// <param name="operation">The canonical operation being authorized.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The access decision. A layer that does not resolve (unknown id, retired
    /// publication, or hidden from the caller's tenant) yields a denied decision
    /// rather than a distinct not-found signal, so callers cannot use the check to
    /// probe which layer ids exist.
    /// </returns>
    Task<AccessDecision> AuthorizeLayerAsync(
        ClaimsPrincipal principal,
        int layerId,
        AuthorizationOperation operation,
        CancellationToken cancellationToken = default);
}
