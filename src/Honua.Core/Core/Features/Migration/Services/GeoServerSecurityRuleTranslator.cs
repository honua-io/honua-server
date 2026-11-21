// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Migration.Services;

internal sealed record GeoServerLayerSecurityTranslation(
    AccessPolicy? AccessPolicy,
    MigrationSecurityRuleDisposition[] Dispositions,
    MigrationManifestReviewItem[] ReviewItems);

internal static class GeoServerSecurityRuleTranslator
{
    public const string UnexpressedRuleCode = "geoserver-security-rule-unexpressed";

    private static readonly string[] DisallowedRoleTokens = [";", "=", "!", " ", "\t", "cql:"];

    private enum LayerRuleMatch { None, Exact, Wildcard }

    private sealed record RoleClassification
    {
        public bool IsValid { get; init; }
        public bool IsAnonymous { get; init; }
        public bool IsAuthenticatedOnly { get; init; }
        public string[] NamedRoles { get; init; } = [];
        public string? FailureReason { get; init; }
    }

    private sealed class PolicyAccumulator(string layerSourceId)
    {
        public string LayerSourceId { get; } = layerSourceId;
        public bool HasRead { get; private set; }
        public bool AllowAnonymousRead { get; private set; }
        public SortedSet<string> ReadRoles { get; } = new(StringComparer.Ordinal);
        public bool HasWrite { get; private set; }
        public bool AllowAnonymousWrite { get; private set; }
        public SortedSet<string> WriteRoles { get; } = new(StringComparer.Ordinal);
        public List<MigrationSecurityRuleDisposition> Dispositions { get; } = [];
        public List<MigrationManifestReviewItem> ReviewItems { get; } = [];

        public void Apply(string mode, RoleClassification classification)
        {
            if (mode == "r")
            {
                HasRead = true;
                if (classification.IsAnonymous)
                {
                    AllowAnonymousRead = true;
                }
                else if (!classification.IsAuthenticatedOnly)
                {
                    ReadRoles.UnionWith(classification.NamedRoles);
                }

                return;
            }

            HasWrite = true;
            if (classification.IsAnonymous)
            {
                AllowAnonymousWrite = true;
            }
            else if (!classification.IsAuthenticatedOnly)
            {
                WriteRoles.UnionWith(classification.NamedRoles);
            }
        }

        public void RecordTranslated(GeoServerSecurityRule rule, string mode, RoleClassification classification)
        {
            Apply(mode, classification);
            Dispositions.Add(new MigrationSecurityRuleDisposition
            {
                RuleKey = rule.RuleKey,
                Mode = mode,
                Roles = rule.Roles,
                Status = "translated",
                Reason = mode == "r"
                    ? "Translated into Honua per-layer read access policy."
                    : "Translated into Honua per-layer write access policy."
            });
        }

        public AccessPolicy? BuildPolicy()
        {
            if (!HasRead && !HasWrite)
            {
                return null;
            }

            return new AccessPolicy
            {
                AllowAnonymous = AllowAnonymousRead,
                AllowAnonymousWrite = AllowAnonymousWrite,
                AllowedRoles = AllowAnonymousRead || ReadRoles.Count == 0 ? null : [.. ReadRoles],
                AllowedWriteRoles = AllowAnonymousWrite || WriteRoles.Count == 0 ? null : [.. WriteRoles]
            };
        }
    }

    public static GeoServerLayerSecurityTranslation TranslateForLayer(
        GeoServerLayerInfo layer,
        IReadOnlyList<GeoServerSecurityRule> rules,
        string layerSourceId)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0)
        {
            return new GeoServerLayerSecurityTranslation(null, [], []);
        }

        var accumulator = new PolicyAccumulator(layerSourceId);
        foreach (var rule in rules.OrderBy(static r => r.RuleKey, StringComparer.Ordinal))
        {
            ProcessLayerRule(layer, rule, accumulator);
        }

        return new GeoServerLayerSecurityTranslation(
            accumulator.BuildPolicy(),
            accumulator.Dispositions.ToArray(),
            accumulator.ReviewItems.ToArray());
    }

    public static MigrationManifestReviewItem[] CollectUnmatchedUnexpressedRules(
        IReadOnlyList<GeoServerSecurityRule> rules,
        IReadOnlyList<GeoServerLayerInfo> layers)
    {
        if (rules.Count == 0)
        {
            return [];
        }

        return rules
            .OrderBy(static r => r.RuleKey, StringComparer.Ordinal)
            .Where(rule => !layers.Any(layer => MatchRuleToLayer(rule, layer) != LayerRuleMatch.None) && IsUnmatchedReportable(rule))
            .Select(CreateUnmatchedReviewItem)
            .ToArray();
    }

    private static void ProcessLayerRule(
        GeoServerLayerInfo layer,
        GeoServerSecurityRule rule,
        PolicyAccumulator accumulator)
    {
        var match = MatchRuleToLayer(rule, layer);
        if (match == LayerRuleMatch.None)
        {
            return;
        }

        if (match == LayerRuleMatch.Wildcard)
        {
            RecordUnexpressed(accumulator, rule, $"Wildcard security rule '{rule.RuleKey}' cannot be expressed as a per-layer Honua access policy without risking a broader grant; review and apply explicitly.");
            return;
        }

        var mode = rule.Mode.Trim().ToLowerInvariant();
        if (mode is not ("r" or "w"))
        {
            RecordUnexpressed(accumulator, rule, $"GeoServer security rule '{rule.RuleKey}' with mode '{rule.Mode}' cannot be expressed as a Honua read/write access policy; not applied as a broader grant.");
            return;
        }

        var classification = ClassifyRoles(rule.Roles);
        if (!classification.IsValid)
        {
            RecordUnexpressed(accumulator, rule, classification.FailureReason!);
            return;
        }

        accumulator.RecordTranslated(rule, mode, classification);
    }

    private static LayerRuleMatch MatchRuleToLayer(GeoServerSecurityRule rule, GeoServerLayerInfo layer)
    {
        if (string.Equals(rule.Mode, "catalog-mode", StringComparison.OrdinalIgnoreCase))
        {
            return LayerRuleMatch.None;
        }

        var wsWildcard = string.Equals(rule.WorkspacePattern, "*", StringComparison.Ordinal);
        var layerWildcard = string.Equals(rule.LayerPattern, "*", StringComparison.Ordinal);
        var wsMatch = wsWildcard || string.Equals(rule.WorkspacePattern, layer.WorkspaceName, StringComparison.OrdinalIgnoreCase);
        var layerMatch = layerWildcard || string.Equals(rule.LayerPattern, layer.Name, StringComparison.OrdinalIgnoreCase);

        if (!wsMatch || !layerMatch)
        {
            return LayerRuleMatch.None;
        }

        return wsWildcard || layerWildcard ? LayerRuleMatch.Wildcard : LayerRuleMatch.Exact;
    }

    private static RoleClassification ClassifyRoles(string[] roles)
    {
        if (roles.Length == 0)
        {
            return InvalidRoles("Security rule specifies no roles and cannot be expressed safely; not applied as a broader grant.");
        }

        var hasAnonymous = false;
        var hasAuthenticated = false;
        var namedRoles = new List<string>();

        foreach (var role in roles.Select(static r => r.Trim()).Where(static r => r.Length > 0))
        {
            if (ContainsDisallowedSyntax(role))
            {
                return InvalidRoles($"Security rule role expression '{role}' uses syntax that cannot be expressed as a Honua role list; not applied as a broader grant.");
            }

            if (string.Equals(role, "*", StringComparison.Ordinal) ||
                string.Equals(role, "ROLE_ANONYMOUS", StringComparison.OrdinalIgnoreCase))
            {
                hasAnonymous = true;
            }
            else if (string.Equals(role, "ROLE_AUTHENTICATED", StringComparison.OrdinalIgnoreCase))
            {
                hasAuthenticated = true;
            }
            else
            {
                namedRoles.Add(role);
            }
        }

        return ValidateRoleCombination(new ParsedRoleTokens(hasAnonymous, hasAuthenticated, namedRoles));
    }

    private sealed record ParsedRoleTokens(bool HasAnonymous, bool HasAuthenticated, List<string> NamedRoles);

    private static RoleClassification ValidateRoleCombination(ParsedRoleTokens parsed)
    {
        if (parsed.HasAnonymous && (parsed.HasAuthenticated || parsed.NamedRoles.Count > 0))
        {
            return InvalidRoles("Security rule mixes anonymous wildcard access with restricted roles; kept for manual review instead of widening access.");
        }

        if (parsed.HasAuthenticated && parsed.NamedRoles.Count > 0)
        {
            return InvalidRoles("Security rule mixes ROLE_AUTHENTICATED with specific roles; kept for manual review instead of widening access.");
        }

        return new RoleClassification
        {
            IsValid = true,
            IsAnonymous = parsed.HasAnonymous,
            IsAuthenticatedOnly = parsed.HasAuthenticated,
            NamedRoles = [.. parsed.NamedRoles]
        };
    }

    private static RoleClassification InvalidRoles(string reason)
        => new() { IsValid = false, FailureReason = reason };

    private static bool ContainsDisallowedSyntax(string role)
        => DisallowedRoleTokens.Any(token => role.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool IsUnmatchedReportable(GeoServerSecurityRule rule)
        => string.Equals(rule.Mode, "catalog-mode", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(rule.WorkspacePattern, "*", StringComparison.Ordinal) ||
           string.Equals(rule.LayerPattern, "*", StringComparison.Ordinal);

    private static MigrationManifestReviewItem CreateUnmatchedReviewItem(GeoServerSecurityRule rule)
        => new()
        {
            SourceId = $"security-rule:{rule.RuleKey}",
            Kind = "security-rule",
            Code = UnexpressedRuleCode,
            Severity = "manual-review",
            Reason = $"GeoServer security rule '{rule.RuleKey}' could not be expressed as a per-layer Honua access policy and was not applied as a broader grant.",
            ManualSteps =
            [
                $"Review GeoServer security rule '{rule.RuleKey}' ({string.Join(", ", rule.Roles)}) and configure the equivalent Honua service or layer access policy manually."
            ]
        };

    private static void RecordUnexpressed(
        PolicyAccumulator accumulator,
        GeoServerSecurityRule rule,
        string reason)
    {
        accumulator.Dispositions.Add(new MigrationSecurityRuleDisposition
        {
            RuleKey = rule.RuleKey,
            Mode = rule.Mode,
            Roles = rule.Roles,
            Status = "unexpressed",
            Reason = reason
        });

        accumulator.ReviewItems.Add(new MigrationManifestReviewItem
        {
            SourceId = accumulator.LayerSourceId,
            Kind = "security-rule",
            Code = UnexpressedRuleCode,
            Severity = "manual-review",
            Reason = reason,
            ManualSteps =
            [
                $"Review GeoServer security rule '{rule.RuleKey}' ({string.Join(", ", rule.Roles)}) and configure the equivalent Honua access policy manually."
            ]
        });
    }
}
