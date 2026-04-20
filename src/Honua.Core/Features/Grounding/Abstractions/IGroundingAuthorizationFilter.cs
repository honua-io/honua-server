// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Core.Features.Grounding.Abstractions;

/// <summary>
/// Filters a ranked list down to candidates the caller is allowed to see.
/// The default implementation delegates to
/// <c>Honua.Core.Features.Authorization.Abstractions.IOperatorAuthorizationEvaluator</c>.
/// Extracted to an interface so grounding unit tests can stub authorization
/// without pulling in the full evaluator graph.
/// </summary>
public interface IGroundingAuthorizationFilter
{
    /// <summary>
    /// Returns the subset of <paramref name="candidates"/> the
    /// <paramref name="principal"/> is authorized to discover. Implementations
    /// must keep the input order so the filtered list stays sorted.
    /// </summary>
    IReadOnlyList<GroundingCandidate> Filter(
        ClaimsPrincipal principal,
        IReadOnlyList<GroundingCandidate> candidates);
}
