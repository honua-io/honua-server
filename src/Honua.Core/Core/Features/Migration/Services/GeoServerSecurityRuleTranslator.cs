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

        var dispositions = new List<MigrationSecurityRuleDisposition>();
        var reviewItems = new List<MigrationManifestReviewItem>();

        var hasTranslatedRead = false;
        var allowAnonymousRead = false;
        var allowedReadRoles = new SortedSet<string>(StringComparer.Ordinal);

        var hasTranslatedWrite = false;
        var allowAnonymousWrite = false;
        var allowedWriteRoles = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules.OrderBy(static r => r.RuleKey, StringComparer.Ordinal))
        {
            if (!AppliesToLayer(rule, layer, out var isExactLayerRule))
            {
                continue;
            }

            var mode = rule.Mode.Trim().ToLowerInvariant();
            if (!isExactLayerRule)
            {
                AddUnexpressed(
                    rule,
                    layerSourceId,
                    $"Wildcard security rule '{rule.RuleKey}' cannot be expressed as a per-layer Honua access policy without risking a broader grant; review and apply explicitly.",
                    dispositions,
                    reviewItems);
                continue;
            }

            if (mode is not ("r" or "w"))
            {
                AddUnexpressed(
                    rule,
                    layerSourceId,
                    $"GeoServer security rule '{rule.RuleKey}' with mode '{rule.Mode}' cannot be expressed as a Honua read/write access policy; not applied as a broader grant.",
                    dispositions,
                    reviewItems);
                continue;
            }

            if (!TryClassifyRoles(rule.Roles, out var isAnonymous, out var isAuthenticatedOnly, out var namedRoles, out var failureReason))
            {
                AddUnexpressed(
                    rule,
                    layerSourceId,
                    failureReason!,
                    dispositions,
                    reviewItems);
                continue;
            }

            if (mode == "r")
            {
                hasTranslatedRead = true;
                if (isAnonymous)
                {
                    allowAnonymousRead = true;
                }
                else if (!isAuthenticatedOnly)
                {
                    foreach (var role in namedRoles)
                    {
                        allowedReadRoles.Add(role);
                    }
                }
            }
            else
            {
                hasTranslatedWrite = true;
                if (isAnonymous)
                {
                    allowAnonymousWrite = true;
                }
                else if (!isAuthenticatedOnly)
                {
                    foreach (var role in namedRoles)
                    {
                        allowedWriteRoles.Add(role);
                    }
                }
            }

            dispositions.Add(new MigrationSecurityRuleDisposition
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

        AccessPolicy? policy = null;
        if (hasTranslatedRead || hasTranslatedWrite)
        {
            policy = new AccessPolicy
            {
                AllowAnonymous = allowAnonymousRead,
                AllowAnonymousWrite = allowAnonymousWrite,
                AllowedRoles = allowAnonymousRead || allowedReadRoles.Count == 0
                    ? null
                    : [.. allowedReadRoles],
                AllowedWriteRoles = allowAnonymousWrite || allowedWriteRoles.Count == 0
                    ? null
                    : [.. allowedWriteRoles]
            };
        }

        return new GeoServerLayerSecurityTranslation(
            policy,
            dispositions.ToArray(),
            reviewItems.ToArray());
    }

    public static MigrationManifestReviewItem[] CollectUnmatchedUnexpressedRules(
        IReadOnlyList<GeoServerSecurityRule> rules,
        IReadOnlyList<GeoServerLayerInfo> layers)
    {
        if (rules.Count == 0)
        {
            return [];
        }

        var items = new List<MigrationManifestReviewItem>();
        foreach (var rule in rules.OrderBy(static r => r.RuleKey, StringComparer.Ordinal))
        {
            var matchedAnyLayer = layers.Any(layer => AppliesToLayer(rule, layer, out _));
            if (matchedAnyLayer)
            {
                continue;
            }

            // Directives like catalog "mode=HIDE" or rules targeting unmatched/wildcard scopes
            // still surface in the plan report so operators see every rule that was not applied.
            if (string.Equals(rule.Mode, "catalog-mode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rule.WorkspacePattern, "*", StringComparison.Ordinal) ||
                string.Equals(rule.LayerPattern, "*", StringComparison.Ordinal))
            {
                items.Add(new MigrationManifestReviewItem
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
                });
            }
        }

        return items.ToArray();
    }

    private static bool AppliesToLayer(
        GeoServerSecurityRule rule,
        GeoServerLayerInfo layer,
        out bool isExactLayerRule)
    {
        isExactLayerRule = false;
        if (string.Equals(rule.Mode, "catalog-mode", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var wsWildcard = string.Equals(rule.WorkspacePattern, "*", StringComparison.Ordinal);
        var layerWildcard = string.Equals(rule.LayerPattern, "*", StringComparison.Ordinal);

        var wsMatch = wsWildcard || string.Equals(rule.WorkspacePattern, layer.WorkspaceName, StringComparison.OrdinalIgnoreCase);
        var layerMatch = layerWildcard || string.Equals(rule.LayerPattern, layer.Name, StringComparison.OrdinalIgnoreCase);

        if (!wsMatch || !layerMatch)
        {
            return false;
        }

        isExactLayerRule = !wsWildcard && !layerWildcard;
        return true;
    }

    private static bool TryClassifyRoles(
        string[] roles,
        out bool isAnonymous,
        out bool isAuthenticatedOnly,
        out List<string> namedRoles,
        out string? failureReason)
    {
        isAnonymous = false;
        isAuthenticatedOnly = false;
        namedRoles = [];
        failureReason = null;

        if (roles.Length == 0)
        {
            failureReason = "Security rule specifies no roles and cannot be expressed safely; not applied as a broader grant.";
            return false;
        }

        var hasAnonymousToken = false;
        var hasAuthenticatedToken = false;

        foreach (var rawRole in roles)
        {
            var role = rawRole.Trim();
            if (role.Length == 0)
            {
                continue;
            }

            if (ContainsDisallowedSyntax(role))
            {
                failureReason = $"Security rule role expression '{role}' uses syntax that cannot be expressed as a Honua role list; not applied as a broader grant.";
                return false;
            }

            if (string.Equals(role, "*", StringComparison.Ordinal) ||
                string.Equals(role, "ROLE_ANONYMOUS", StringComparison.OrdinalIgnoreCase))
            {
                hasAnonymousToken = true;
                continue;
            }

            if (string.Equals(role, "ROLE_AUTHENTICATED", StringComparison.OrdinalIgnoreCase))
            {
                hasAuthenticatedToken = true;
                continue;
            }

            namedRoles.Add(role);
        }

        if (hasAnonymousToken && (hasAuthenticatedToken || namedRoles.Count > 0))
        {
            failureReason = "Security rule mixes anonymous wildcard access with restricted roles; kept for manual review instead of widening access.";
            return false;
        }

        if (hasAuthenticatedToken && namedRoles.Count > 0)
        {
            failureReason = "Security rule mixes ROLE_AUTHENTICATED with specific roles; kept for manual review instead of widening access.";
            return false;
        }

        isAnonymous = hasAnonymousToken;
        isAuthenticatedOnly = hasAuthenticatedToken;
        return true;
    }

    private static bool ContainsDisallowedSyntax(string role)
    {
        foreach (var token in DisallowedRoleTokens)
        {
            if (role.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddUnexpressed(
        GeoServerSecurityRule rule,
        string layerSourceId,
        string reason,
        List<MigrationSecurityRuleDisposition> dispositions,
        List<MigrationManifestReviewItem> reviewItems)
    {
        dispositions.Add(new MigrationSecurityRuleDisposition
        {
            RuleKey = rule.RuleKey,
            Mode = rule.Mode,
            Roles = rule.Roles,
            Status = "unexpressed",
            Reason = reason
        });

        reviewItems.Add(new MigrationManifestReviewItem
        {
            SourceId = layerSourceId,
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
