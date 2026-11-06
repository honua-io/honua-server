// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Guardrails;

public sealed class DefaultGuardrailLadder : IGuardrailLadder
{
    private readonly ILicenseEntitlementService _entitlements;
    private readonly GuardrailLadderOptions _options;
    private readonly IOpsActionGuardrailCatalog? _actionCatalog;

    public DefaultGuardrailLadder(
        ILicenseEntitlementService entitlements,
        IOptions<GuardrailLadderOptions> options,
        IOpsActionGuardrailCatalog? actionCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(entitlements);
        ArgumentNullException.ThrowIfNull(options);
        _entitlements = entitlements;
        _options = options.Value;
        _actionCatalog = actionCatalog;
    }

    public GuardrailDecision Resolve(OperationClass operationClass)
        => Resolve(operationClass, _entitlements.GetSnapshot().Edition);

    public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
        => Resolve(operationClass, actionDiscriminator, _entitlements.GetSnapshot().Edition);

    public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator, HonuaEdition edition)
    {
        // No discriminator: fall back to operation-class-only resolution.
        if (string.IsNullOrWhiteSpace(actionDiscriminator))
        {
            return Resolve(operationClass, edition);
        }

        // Platform-declared actions resolve without the ops-action catalog, so a host that
        // registers no catalog still routes them through their declared floor.
        if (BuiltInGuardrailActions.TryGetTier(actionDiscriminator, out var builtInTier))
        {
            return ApplyActionFloor(operationClass, edition, builtInTier, $"built-in-action:{actionDiscriminator}");
        }

        // Discriminated resolution: an action the catalog does not recognize (or when
        // no catalog is registered) fails closed to Blocked, so a malformed or unknown
        // action can never slip through as direct-execute.
        if (_actionCatalog is null ||
            !_actionCatalog.TryGetTier(actionDiscriminator, out var actionTier) ||
            !Enum.IsDefined(actionTier))
        {
            return new GuardrailDecision(GuardrailTier.Blocked, operationClass, edition, "unknown-action-blocked");
        }

        return ApplyActionFloor(operationClass, edition, actionTier, $"action-catalog:{actionDiscriminator}");
    }

    // A per-action tier can only tighten the edition policy.
    private GuardrailDecision ApplyActionFloor(
        OperationClass operationClass,
        HonuaEdition edition,
        GuardrailTier actionTier,
        string source)
    {
        // A discriminated action is a governed step rather than draft composition, so its
        // baseline never takes the Disabled-mode Studio composition tier.
        var baseline = ResolveClass(operationClass, edition, disabledStudioComposition: false);
        var effective = (GuardrailTier)Math.Max((int)actionTier, (int)baseline.Tier);
        return new GuardrailDecision(effective, operationClass, edition, source);
    }

    public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition)
        => ResolveClass(operationClass, edition, disabledStudioComposition: true);

    private GuardrailDecision ResolveClass(
        OperationClass operationClass,
        HonuaEdition edition,
        bool disabledStudioComposition)
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

        // #4758: Licensing:Mode=Disabled grants every entitlement and reports Enterprise, but it
        // is the unlicensed 2026.1 posture rather than an Enterprise governance deployment. Studio
        // draft composition therefore executes directly, as on Community. Publication, rollback
        // and agent proposals resolve with an action discriminator and keep the approval baseline.
        if (disabledStudioComposition &&
            operationClass == OperationClass.StudioDraftMutation &&
            _entitlements.GetSnapshot().Mode == LicenseMode.Disabled)
        {
            return new GuardrailDecision(
                GuardrailTier.DirectExecute, operationClass, edition, "licensing-disabled-studio-composition");
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
