// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.TemporalHistory;

/// <summary>
/// Resolves and enforces the independent authorization required for each temporal-history operation.
/// Each operation is mapped to an effective <see cref="AccessPolicy"/> derived from the layer's
/// temporal access policy (falling back to the layer's general policy) and evaluated through the shared
/// <see cref="IAccessPolicyEvaluator"/> so behavior matches the rest of the server.
/// </summary>
internal static class TemporalAccessResolver
{
    /// <summary>
    /// Enforces the permission for a temporal operation on a layer.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="layer">The configured layer.</param>
    /// <param name="operation">The temporal operation being requested.</param>
    /// <returns>A 401/403 result when access is denied, or null when allowed.</returns>
    public static IResult? Require(HttpContext context, LayerDefinition layer, TemporalOperation operation)
    {
        var (policy, scope) = BuildEffectivePolicy(layer, operation);
        var evaluator = context.RequestServices.GetRequiredService<IAccessPolicyEvaluator>();
        var decision = evaluator.Evaluate(context.User, policy, servicePolicy: null, scope);
        return AccessPolicyHelpers.CreateAccessDeniedResult(context, decision);
    }

    private static (AccessPolicy? Policy, AccessScope Scope) BuildEffectivePolicy(
        LayerDefinition layer,
        TemporalOperation operation)
    {
        var general = layer.Metadata?.AccessPolicy;
        var temporal = layer.Metadata?.TemporalSource?.AccessPolicy;

        if (temporal is null)
        {
            // No temporal-specific policy: history reads default to current-read; execution defaults to write.
            return operation == TemporalOperation.RollbackExecute
                ? (general, AccessScope.Write)
                : (general, AccessScope.Read);
        }

        return operation switch
        {
            TemporalOperation.CurrentRead => (general, AccessScope.Read),
            TemporalOperation.HistoryRead =>
                (Synthesize(temporal.HistoryReadRoles, temporal.AllowAnonymousHistoryRead) ?? general, AccessScope.Read),
            TemporalOperation.DiffRead =>
                (Synthesize(temporal.DiffReadRoles ?? temporal.HistoryReadRoles, temporal.AllowAnonymousHistoryRead) ?? general, AccessScope.Read),
            TemporalOperation.TimelineRead =>
                (Synthesize(temporal.TimelineReadRoles ?? temporal.HistoryReadRoles, temporal.AllowAnonymousHistoryRead) ?? general, AccessScope.Read),
            TemporalOperation.RollbackPlan =>
                (Synthesize(temporal.RollbackPlanRoles ?? temporal.HistoryReadRoles, anonymous: false) ?? general, AccessScope.Read),
            TemporalOperation.RollbackExecute => temporal.RollbackExecuteRoles is { Length: > 0 }
                ? (new AccessPolicy { AllowedRoles = temporal.RollbackExecuteRoles }, AccessScope.Read)
                : (general, AccessScope.Write),
            _ => (general, AccessScope.Read)
        };
    }

    private static AccessPolicy? Synthesize(string[]? roles, bool anonymous)
        => roles is { Length: > 0 } || anonymous
            ? new AccessPolicy { AllowAnonymous = anonymous, AllowedRoles = roles }
            : null;
}
