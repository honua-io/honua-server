// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Shared route-time and operator-projection resolver for configuration-backed autonomy policy.
/// </summary>
internal static class OpsAutonomyPolicyDefaults
{
    /// <summary>
    /// Built-in rules whose deterministic action is registered as auto-safe. These rules remain
    /// visible to operators even before the condition is active or a durable override is written.
    /// </summary>
    public static IReadOnlySet<string> BuiltInAutoSafeRules { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        OpsFindingsService.RuleAlertDispatchBacklog,
    };

    /// <summary>Resolves the exact effective policy enforced at route time.</summary>
    public static OpsAutonomyPolicy Resolve(
        string rule,
        OpsAutonomyOptions options,
        OpsAutonomyPolicy? persisted)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (persisted is not null)
        {
            return Normalize(persisted with { Rule = rule });
        }

        var mode = OpsAutonomyMode.ProposeOnly;
        var maxActions = options.DefaultMaxAutoActionsPerWindow;
        var windowSeconds = options.DefaultWindowSeconds;
        var maxBlastRadius = options.DefaultMaxBlastRadius;

        if (!string.IsNullOrWhiteSpace(rule) && options.Rules.TryGetValue(rule, out var ruleOptions))
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

    /// <summary>Returns whether a rule identifier is safe and canonical for policy lookup.</summary>
    public static bool IsValidRule(string? rule)
        => !string.IsNullOrWhiteSpace(rule)
            && rule.Length <= 128
            && rule.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

    /// <summary>Returns whether an opaque finding/proposal identifier is bounded and printable.</summary>
    public static bool IsValidStableId(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 256
            && value.All(static ch => ch is >= '!' and <= '~');

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
}
