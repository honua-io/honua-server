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
/// and the license entitlement snapshot. Community executes directly (subject to
/// RBAC + entitlement); Pro and Enterprise route governed operation classes
/// through approval. Studio draft composition remains realtime in every edition,
/// while discriminated publication and rollback steps use their governed tier.
/// Operator overrides can tighten or loosen the default per operation class.
/// </summary>
public sealed class DefaultGuardrailLadder : IGuardrailLadder
{
    private readonly ILicenseEntitlementService _entitlements;
    private readonly GuardrailLadderOptions _options;
    private readonly IOpsActionGuardrailCatalog? _actionCatalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultGuardrailLadder"/> class.
    /// </summary>
    /// <param name="entitlements">Active license entitlement service.</param>
    /// <param name="options">Operator override options.</param>
    /// <param name="actionCatalog">
    /// Optional per-action guardrail catalog. When present, resolution with an
    /// action discriminator sources the tier from it; when absent, any discriminated
    /// resolution fails closed to <see cref="GuardrailTier.Blocked"/>.
    /// </param>
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

    /// <inheritdoc />
    public GuardrailDecision Resolve(OperationClass operationClass)
        => Resolve(operationClass, _entitlements.GetSnapshot().Edition);

    /// <inheritdoc />
    public GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator)
        => Resolve(operationClass, actionDiscriminator, _entitlements.GetSnapshot().Edition);

    /// <inheritdoc />
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

    /// <summary>
    /// Applies a known action's tier as a floor on top of the edition/operator-override policy.
    /// The tier enum is ordered DirectExecute(0) &lt; RequiresApproval(1) &lt; Blocked(2), so a
    /// per-action tier can only TIGHTEN the guardrail — never loosen the edition policy.
    /// </summary>
    private GuardrailDecision ApplyActionFloor(
        OperationClass operationClass,
        HonuaEdition edition,
        GuardrailTier actionTier,
        string source)
    {
        // A discriminated action is a governed step rather than draft composition, so its
        // baseline never takes the Studio draft composition tier.
        var baseline = ResolveClass(operationClass, edition, allowDraftComposition: false);
        var effective = (GuardrailTier)Math.Max((int)actionTier, (int)baseline.Tier);
        return new GuardrailDecision(effective, operationClass, edition, source);
    }

    /// <inheritdoc />
    public GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition)
        => ResolveClass(operationClass, edition, allowDraftComposition: true);

    private GuardrailDecision ResolveClass(
        OperationClass operationClass,
        HonuaEdition edition,
        bool allowDraftComposition)
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

        // Draft composition is the realtime authoring loop in every edition. Publication,
        // rollback and agent proposals carry action discriminators and keep the approval
        // baseline. Explicit operator overrides above still apply to the whole class.
        if (allowDraftComposition &&
            operationClass == OperationClass.StudioDraftMutation && Enum.IsDefined(edition))
        {
            return new GuardrailDecision(
                GuardrailTier.DirectExecute, operationClass, edition,
                _entitlements.GetSnapshot().Mode == LicenseMode.Disabled
                    ? "licensing-disabled-studio-composition" : "studio-draft-composition");
        }

        return new GuardrailDecision(ResolveDefaultTier(edition), operationClass, edition, "default-policy");
    }

    private static GuardrailTier ResolveDefaultTier(HonuaEdition edition) => edition switch
    {
        // Pro and Enterprise include configurable approval governance.
        HonuaEdition.Pro or HonuaEdition.Enterprise => GuardrailTier.RequiresApproval,

        // Community executes directly (subject to RBAC + entitlement).
        HonuaEdition.Community => GuardrailTier.DirectExecute,

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
