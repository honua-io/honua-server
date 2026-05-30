// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Grounding.Abstractions;
using Honua.Core.Features.Grounding.Domain;

namespace Honua.Ai.Grounding;

/// <summary>
/// Default authorization filter. Delegates to
/// <see cref="IOperatorAuthorizationEvaluator"/> so grounding respects the
/// canonical authorization graph (honua-server-733) without inventing its own
/// access check.
/// </summary>
internal sealed class OperatorGroundingAuthorizationFilter : IGroundingAuthorizationFilter
{
    private readonly IOperatorAuthorizationEvaluator _evaluator;
    private readonly ILogger<OperatorGroundingAuthorizationFilter> _logger;

    public OperatorGroundingAuthorizationFilter(
        IOperatorAuthorizationEvaluator evaluator,
        ILogger<OperatorGroundingAuthorizationFilter> logger)
    {
        _evaluator = evaluator;
        _logger = logger;
    }

    public IReadOnlyList<GroundingCandidate> Filter(
        ClaimsPrincipal principal,
        IReadOnlyList<GroundingCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var allowed = new List<GroundingCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var request = new OperatorAuthorizationRequest
            {
                ResourceType = MapResourceType(candidate.Kind),
                Operation = OperatorOperation.Discover,
                ResourceId = candidate.Id
            };

            var decision = _evaluator.EvaluateAsync(principal, request).ConfigureAwait(false).GetAwaiter().GetResult();
            if (decision.IsAllowed)
            {
                allowed.Add(candidate);
                continue;
            }

            GroundingLog.CandidateFiltered(
                _logger,
                candidate.Kind.ToString(),
                candidate.Id,
                decision.FailureReason ?? "unauthorized");
        }

        return allowed;
    }

    private static OperatorResourceType MapResourceType(CandidateKind kind) => kind switch
    {
        CandidateKind.Process => OperatorResourceType.Process,
        CandidateKind.Dataset => OperatorResourceType.Catalog,
        CandidateKind.Template => OperatorResourceType.Catalog,
        CandidateKind.Style => OperatorResourceType.Catalog,
        CandidateKind.Deployment => OperatorResourceType.Deployment,
        _ => OperatorResourceType.Catalog
    };
}
