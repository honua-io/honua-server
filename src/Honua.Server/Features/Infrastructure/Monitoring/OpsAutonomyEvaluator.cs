// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Configuration-backed defaults for deterministic ops-finding autonomy policy.
/// </summary>
internal sealed class OpsAutonomyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Observability:OpsFindings:Autonomy";

    /// <summary>Gets or sets a value indicating whether autonomy evaluation is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets a global kill switch that forces all rules back to propose-only.</summary>
    public bool KillSwitchEnabled { get; set; }

    /// <summary>Gets or sets the background evaluator poll interval in seconds.</summary>
    [Range(5, 3600)]
    public int EvaluationSeconds { get; set; } = 60;

    /// <summary>Gets or sets the default maximum auto-actions per rule window.</summary>
    [Range(1, int.MaxValue)]
    public int DefaultMaxAutoActionsPerWindow { get; set; } = 1;

    /// <summary>Gets or sets the default rolling rate-limit window in seconds.</summary>
    [Range(1, 86400)]
    public int DefaultWindowSeconds { get; set; } = 3600;

    /// <summary>Gets or sets the default maximum blast-radius value.</summary>
    [Range(1, int.MaxValue)]
    public int DefaultMaxBlastRadius { get; set; } = 10;

    /// <summary>Gets or sets per-rule policy defaults from configuration.</summary>
    public Dictionary<string, OpsAutonomyRuleOptions> Rules { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Configuration-backed policy defaults for one rule.
/// </summary>
internal sealed class OpsAutonomyRuleOptions
{
    /// <summary>Gets or sets the autonomy mode (<c>ProposeOnly</c> or <c>AutoApply</c>).</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the maximum auto-actions per rolling window.</summary>
    public int? MaxAutoActionsPerWindow { get; set; }

    /// <summary>Gets or sets the rolling rate-limit window in seconds.</summary>
    public int? WindowSeconds { get; set; }

    /// <summary>Gets or sets the maximum blast-radius value.</summary>
    public int? MaxBlastRadius { get; set; }
}

/// <summary>
/// Default deterministic autonomy evaluator. It never performs work itself; it only decides whether
/// the operation gateway may convert an approval-tier finding remediation to direct execution.
/// </summary>
internal sealed class OpsAutonomyEvaluator(
    IOptionsMonitor<OpsAutonomyOptions> options,
    IOpsAutonomyPolicyStore? store = null,
    IOpsActionSafetyCatalog? safetyCatalog = null) : IOpsAutonomyEvaluator
{
    public async Task<OpsAutonomyFindingDecision> EvaluateFindingAsync(
        OpsFinding finding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finding);

        var action = finding.RecommendedAction;
        if (action is null)
        {
            return FindingDenied("no-recommended-action");
        }

        var policy = await ResolvePolicyAsync(finding.Rule, cancellationToken).ConfigureAwait(false);
        var guard = await CheckCommonGuardsAsync(
                finding.Rule,
                action.Kind,
                action.ActionDiscriminator,
                action.AutoSafe,
                Math.Max(1, action.BlastRadius),
                policy,
                requireStore: true,
                cancellationToken)
            .ConfigureAwait(false);
        return guard is not null
            ? FindingDenied(guard, policy)
            : new OpsAutonomyFindingDecision { CanAutoApply = true, Policy = policy };
    }

    public async Task<OpsAutonomyRouteDecision> EvaluateRouteAsync(
        OperationGatewayRequest request,
        GuardrailDecision currentDecision,
        string? actionDiscriminator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentDecision.Tier != GuardrailTier.RequiresApproval)
        {
            return RouteDenied("not-approval-tier");
        }

        var context = request.AutonomyContext;
        if (context is null)
        {
            return RouteDenied("no-autonomy-context");
        }

        var policy = await ResolvePolicyAsync(context.Rule, cancellationToken).ConfigureAwait(false);
        var guard = await CheckCommonGuardsAsync(
                context.Rule,
                request.Kind,
                actionDiscriminator,
                context.ActionMarkedAutoSafe,
                Math.Max(1, context.BlastRadius),
                policy,
                requireStore: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (guard is not null)
        {
            return RouteDenied(guard, policy);
        }

        var reservation = await store!.TryReserveAutoActionAsync(
                new OpsAutonomyReservationRequest
                {
                    Rule = context.Rule,
                    FindingId = context.FindingId,
                    OperationClass = request.Kind,
                    ActionDiscriminator = actionDiscriminator,
                    BlastRadius = Math.Max(1, context.BlastRadius),
                    MaxAutoActionsPerWindow = policy.MaxAutoActionsPerWindow,
                    Window = policy.Window,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!reservation.Reserved || string.IsNullOrWhiteSpace(reservation.ReservationId))
        {
            return RouteDenied(reservation.Reason ?? "reservation-denied", policy);
        }

        return new OpsAutonomyRouteDecision
        {
            ShouldAutoApply = true,
            Decision = new GuardrailDecision(
                GuardrailTier.DirectExecute,
                request.Kind,
                currentDecision.Edition,
                $"autonomy-policy:{context.Rule}"),
            ReservationId = reservation.ReservationId,
            Policy = policy,
            Reason = "auto-apply-reserved",
        };
    }

    public Task RecordAutoActionOutcomeAsync(
        OpsAutonomyRouteDecision decision,
        OpsAutonomyActionOutcome outcome,
        string? operationId = null,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (store is null || string.IsNullOrWhiteSpace(decision.ReservationId))
        {
            return Task.CompletedTask;
        }

        return store.RecordAutoActionOutcomeAsync(
            decision.ReservationId,
            outcome,
            operationId,
            message,
            cancellationToken);
    }

    public Task RecordProposalRaisedAsync(
        OperationGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = request.AutonomyContext?.Rule;
        return store is null || string.IsNullOrWhiteSpace(rule)
            ? Task.CompletedTask
            : store.IncrementProposalRaisedAsync(rule, cancellationToken);
    }

    private async Task<string?> CheckCommonGuardsAsync(
        string rule,
        OperationClass operationClass,
        string? actionDiscriminator,
        bool actionMarkedAutoSafe,
        int blastRadius,
        OpsAutonomyPolicy policy,
        bool requireStore,
        CancellationToken cancellationToken)
    {
        var currentOptions = options.CurrentValue;
        if (!currentOptions.Enabled)
        {
            return "autonomy-disabled";
        }

        if (currentOptions.KillSwitchEnabled)
        {
            return "config-kill-switch";
        }

        if (store is not null)
        {
            var settings = await store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (settings.KillSwitchEnabled)
            {
                return "store-kill-switch";
            }
        }
        else if (requireStore)
        {
            return "policy-store-unavailable";
        }

        if (policy.Mode != OpsAutonomyMode.AutoApply)
        {
            return "policy-propose-only";
        }

        if (!actionMarkedAutoSafe)
        {
            return "finding-action-not-auto-safe";
        }

        if (safetyCatalog is null || !safetyCatalog.IsAutoSafe(operationClass, actionDiscriminator))
        {
            return "action-catalog-not-auto-safe";
        }

        if (blastRadius > policy.MaxBlastRadius)
        {
            return "blast-radius-exceeded";
        }

        return string.IsNullOrWhiteSpace(rule) ? "missing-rule" : null;
    }

    private async Task<OpsAutonomyPolicy> ResolvePolicyAsync(string rule, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(rule) && store is not null)
        {
            var stored = await store.GetPolicyAsync(rule, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return Normalize(stored);
            }
        }

        var current = options.CurrentValue;
        var mode = OpsAutonomyMode.ProposeOnly;
        int maxActions = current.DefaultMaxAutoActionsPerWindow;
        int windowSeconds = current.DefaultWindowSeconds;
        int maxBlastRadius = current.DefaultMaxBlastRadius;

        if (!string.IsNullOrWhiteSpace(rule) &&
            current.Rules.TryGetValue(rule, out var ruleOptions))
        {
            mode = ParseMode(ruleOptions.Mode) ?? mode;
            maxActions = ruleOptions.MaxAutoActionsPerWindow ?? maxActions;
            windowSeconds = ruleOptions.WindowSeconds ?? windowSeconds;
            maxBlastRadius = ruleOptions.MaxBlastRadius ?? maxBlastRadius;
        }

        return Normalize(new OpsAutonomyPolicy
        {
            Rule = rule,
            Mode = mode,
            MaxAutoActionsPerWindow = maxActions,
            Window = TimeSpan.FromSeconds(windowSeconds),
            MaxBlastRadius = maxBlastRadius,
        });
    }

    private static OpsAutonomyPolicy Normalize(OpsAutonomyPolicy policy)
        => policy with
        {
            MaxAutoActionsPerWindow = Math.Max(1, policy.MaxAutoActionsPerWindow),
            Window = policy.Window <= TimeSpan.Zero ? TimeSpan.FromHours(1) : policy.Window,
            MaxBlastRadius = Math.Max(1, policy.MaxBlastRadius),
        };

    private static OpsAutonomyMode? ParseMode(string? raw)
        => Enum.TryParse<OpsAutonomyMode>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : null;

    private static OpsAutonomyFindingDecision FindingDenied(string reason, OpsAutonomyPolicy? policy = null)
        => new() { CanAutoApply = false, Policy = policy, Reason = reason };

    private static OpsAutonomyRouteDecision RouteDenied(string reason, OpsAutonomyPolicy? policy = null)
        => new() { ShouldAutoApply = false, Policy = policy, Reason = reason };
}
