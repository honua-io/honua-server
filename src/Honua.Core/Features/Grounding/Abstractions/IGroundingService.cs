// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

/// <summary>
/// Entry point for the grounding + intent-drafting capability. Consumed by the
/// <c>honua_ground_candidates</c> and <c>honua_clarify_intent</c> MCP tools.
/// Workflow-family classification, candidate ranking, and clarification
/// emission must be deterministic for a given engine + catalog snapshot so
/// honua-server-734 eval fixtures stay stable across runs. Initial intent-id
/// allocation (when <see cref="GroundingRequest.IntentId"/> is omitted) is
/// intentionally unique so independent callers never collide; supply the same
/// <c>IntentId</c> across turns to make the full result — including the
/// drafted intent id — stable.
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
