// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

/// <summary>
/// Entry point for the grounding + intent-drafting capability. Consumed by the
/// <c>honua_ground_candidates</c> and <c>honua_clarify_intent</c> MCP tools.
/// Implementations must be deterministic for a given engine + catalog snapshot
/// so honua-server-734 eval fixtures stay stable across runs.
/// </summary>
public interface IGroundingService
{
    /// <summary>
    /// Runs a single grounding pass. Returns the workflow-family
    /// classification, a draft typed intent, ranked catalog candidates, and an
    /// optional clarification envelope.
    /// </summary>
    Task<GroundingResult> GroundAsync(
        GroundingRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}
