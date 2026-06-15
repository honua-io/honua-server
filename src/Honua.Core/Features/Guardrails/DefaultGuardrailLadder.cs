// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Guardrails;

/// <summary>
/// Default <see cref="IGuardrailLadder"/> built over <see cref="HonuaEdition"/>
/// and the license entitlement snapshot. Implements the locked default policy
/// from #1690: Community/Pro execute directly (subject to RBAC + entitlement),
/// Enterprise routes in-scope mutating operation classes through approval.
/// Operator overrides can tighten or loosen the default per operation class.
/// </summary>
public sealed class DefaultGuardrailLadder : IGuardrailLadder
{
    private readonly ILicenseEntitlementService _entitlements;
    private readonly GuardrailLadderOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultGuardrailLadder"/> class.
    /// </summary>
    /// <param name="entitlements">Active license entitlement service.</param>
    /// <param name="options">Operator override options.</param>
    public DefaultGuardrailLadder(
        ILicenseEntitlementService entitlements,
        IOptions<GuardrailLadderOptions> options)
    {
        ArgumentNullException.ThrowIfNull(entitlements);
        ArgumentNullException.ThrowIfNull(options);
        _entitlements = entitlements;
        _options = options.Value;
    }

    /// <inheritdoc />
    public GuardrailDecision Resolve(OperationClass operationClass)
        => Resolve(operationClass, _entitlements.GetSnapshot().Edition);

    /// <inheritdoc />
    public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition)
    {
        // Unknown/undeclared operation classes fail closed (or open in dev).
        if (!Enum.IsDefined(operationClass))
        {
            return _options.FailClosed
                ? new GuardrailDecision(GuardrailTier.RequiresApproval, operationClass, edition, "fail-closed-unknown-class")
                : new GuardrailDecision(GuardrailTier.DirectExecute, operationClass, edition, "dev-open-unknown-class");
        }

        // Operator override takes precedence when present and parseable.
        if (TryResolveOverride(operationClass, edition, out var overridden))
        {
            return overridden;
        }

        return new GuardrailDecision(ResolveDefaultTier(edition), operationClass, edition, "default-policy");
    }

    private static GuardrailTier ResolveDefaultTier(HonuaEdition edition) => edition switch
    {
        // Enterprise routes in-scope mutating classes through approval.
        HonuaEdition.Enterprise => GuardrailTier.RequiresApproval,

        // Community/Pro execute directly (subject to RBAC + entitlement).
        HonuaEdition.Community or HonuaEdition.Pro => GuardrailTier.DirectExecute,

        // Defensive default: any future/unknown edition fails closed to approval.
        _ => GuardrailTier.RequiresApproval,
    };

    private bool TryResolveOverride(
        OperationClass operationClass,
        HonuaEdition edition,
        out GuardrailDecision decision)
    {
        decision = default!;
        if (_options.Overrides.Count == 0)
        {
            return false;
        }

        if (!_options.Overrides.TryGetValue(operationClass.ToString(), out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Enum.TryParse<GuardrailTier>(raw.Trim(), ignoreCase: true, out var tier) ||
            !Enum.IsDefined(tier))
        {
            return false;
        }

        decision = new GuardrailDecision(tier, operationClass, edition, "operator-override");
        return true;
    }
}
