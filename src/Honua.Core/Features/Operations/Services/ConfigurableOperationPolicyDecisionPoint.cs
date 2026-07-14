// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Configuration-driven <see cref="IOperationPolicyDecisionPoint"/>. Evaluates the ordered
/// <see cref="OperationPolicyOptions.Rules"/> against the operation id, caller tier, and caller
/// role(s) on a first-match-wins basis, falling back to <see cref="OperationPolicyOptions.DefaultDecision"/>.
/// When <see cref="OperationPolicyOptions.Enabled"/> is <see langword="false"/> it is a pass-through
/// allow, identical to <see cref="AllowAllPolicyDecisionPoint"/>, so the default deployment stays
/// permissive. Evaluation is deterministic and reflection-free (AOT-safe).
/// </summary>
public sealed class ConfigurableOperationPolicyDecisionPoint : IOperationPolicyDecisionPoint
{
    private readonly OperationPolicyOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="ConfigurableOperationPolicyDecisionPoint"/>.
    /// </summary>
    /// <param name="options">The bound policy options.</param>
    public ConfigurableOperationPolicyDecisionPoint(IOptions<OperationPolicyOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? new OperationPolicyOptions();
    }

    /// <inheritdoc />
    public Task<PolicyDecision> EvaluateAsync(
        IOperationDescriptor descriptor,
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Disabled policy is a pass-through allow — identical to the Community default.
        if (!_options.Enabled)
        {
            return Task.FromResult(PolicyDecision.Allowed);
        }

        var matchedRule = _options.Rules.FirstOrDefault(rule => rule is not null && Matches(rule, request, context));
        if (matchedRule is not null)
        {
            return Task.FromResult(ResolveDecision(matchedRule.Decision, matchedRule.Reason, matchedRule.ApprovalLane, request));
        }

        return Task.FromResult(ResolveDecision(
            _options.DefaultDecision,
            _options.DefaultReason,
            _options.DefaultApprovalLane,
            request));
    }

    private static bool Matches(OperationPolicyRule rule, OperationRequest request, OperationPolicyContext context)
    {
        // Operation id: "*" or empty is a wildcard; otherwise an exact (ordinal) match.
        if (!string.IsNullOrEmpty(rule.OperationId)
            && rule.OperationId != "*"
            && !string.Equals(rule.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            return false;
        }

        // Tier: unset is a wildcard; otherwise case-insensitive equality.
        if (!string.IsNullOrEmpty(rule.Tier)
            && !string.Equals(rule.Tier, context.Tier, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Role: unset is a wildcard; otherwise the caller must hold the role (case-insensitive).
        if (!string.IsNullOrEmpty(rule.Role)
            && !ContainsRole(context.Roles, rule.Role))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsRole(IReadOnlyList<string> roles, string role)
    {
        for (var i = 0; i < roles.Count; i++)
        {
            if (string.Equals(roles[i], role, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static PolicyDecision ResolveDecision(
        PolicyDecisionKind kind,
        string? reason,
        string? approvalLane,
        OperationRequest request)
    {
        // Honor the dry-run contract: when the policy demands a dry-run/preview first and the
        // caller already requested one, the precondition is satisfied — allow it through.
        if (kind == PolicyDecisionKind.DryRunFirst && request.DryRun)
        {
            return PolicyDecision.Allowed;
        }

        if (kind == PolicyDecisionKind.Allow)
        {
            return PolicyDecision.Allowed;
        }

        return new PolicyDecision
        {
            Kind = kind,
            Reason = reason,

            // Only a require-approval decision carries an approval lane.
            ApprovalLane = kind == PolicyDecisionKind.RequireApproval ? approvalLane : null
        };
    }
}
