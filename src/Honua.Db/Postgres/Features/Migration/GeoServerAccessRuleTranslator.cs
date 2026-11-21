// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Security.Domain;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed record GeoServerAccessRuleTranslationResult(
    IReadOnlyDictionary<string, AccessPolicy> LayerPoliciesBySourceId,
    MigrationManifestReviewItem[] UnexpressibleReviewItems);

internal static class GeoServerAccessRuleTranslator
{
    private const string ReviewCode = "geoserver.access-rule.manual-review";

    private sealed class TranslationContext(Dictionary<string, string> layersByKey)
    {
        public Dictionary<string, string> LayersByKey { get; } = layersByKey;
        public Dictionary<string, LayerAccessAccumulator> Accumulators { get; } = new(StringComparer.Ordinal);
        public List<MigrationManifestReviewItem> ReviewItems { get; } = [];
        public HashSet<string> WildcardWriteWorkspaces { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasGlobalWildcardWrite { get; set; }
    }

    public static GeoServerAccessRuleTranslationResult Translate(
        IReadOnlyList<GeoServerLayerAccessRule> rules,
        IReadOnlyList<GeoServerLayerInfo> layers)
    {
        if (rules.Count == 0)
        {
            return new GeoServerAccessRuleTranslationResult(
                new Dictionary<string, AccessPolicy>(StringComparer.Ordinal),
                []);
        }

        var layersByKey = layers.ToDictionary(
            static layer => BuildLayerKey(layer.WorkspaceName, layer.Name),
            static layer => $"layer:{layer.WorkspaceName}:{layer.Name}",
            StringComparer.OrdinalIgnoreCase);
        var workspaceBySourceId = layers.ToDictionary(
            static layer => $"layer:{layer.WorkspaceName}:{layer.Name}",
            static layer => layer.WorkspaceName,
            StringComparer.Ordinal);

        var context = new TranslationContext(layersByKey);
        foreach (var rule in rules.OrderBy(static r => r.RuleKey, StringComparer.Ordinal))
        {
            ClassifyRule(rule, context);
        }

        var policies = BuildPolicies(context, workspaceBySourceId);
        return new GeoServerAccessRuleTranslationResult(policies, context.ReviewItems.ToArray());
    }

    private static Dictionary<string, AccessPolicy> BuildPolicies(
        TranslationContext context,
        Dictionary<string, string> workspaceBySourceId)
    {
        var policies = new Dictionary<string, AccessPolicy>(StringComparer.Ordinal);
        foreach (var (sourceId, accumulator) in context.Accumulators.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal))
        {
            var hasWildcardWrite = context.HasGlobalWildcardWrite ||
                (workspaceBySourceId.TryGetValue(sourceId, out var ws) && context.WildcardWriteWorkspaces.Contains(ws));
            if (accumulator.TryBuildPolicy(hasWildcardWrite, out var policy, out var conflictReason))
            {
                policies[sourceId] = policy!;
            }
            else if (conflictReason != null)
            {
                context.ReviewItems.Add(CreateReviewItem(sourceId, conflictReason));
            }
        }

        return policies;
    }

    private static void ClassifyRule(GeoServerLayerAccessRule rule, TranslationContext context)
    {
        if (string.IsNullOrWhiteSpace(rule.WorkspaceName) ||
            string.IsNullOrWhiteSpace(rule.LayerName) ||
            string.IsNullOrWhiteSpace(rule.Permission))
        {
            context.ReviewItems.Add(CreateReviewItem(
                $"access-rule:{rule.RuleKey}",
                $"GeoServer access rule '{rule.RuleKey}={rule.RawValue}' is not a per-layer read/write rule and was not applied."));
            return;
        }

        if (rule.WorkspaceName == "*" || rule.LayerName == "*")
        {
            RecordWildcardRule(rule, context);
            return;
        }

        if (!context.LayersByKey.TryGetValue(BuildLayerKey(rule.WorkspaceName, rule.LayerName), out var layerSourceId))
        {
            return;
        }

        ClassifyLayerRule(rule, layerSourceId, context);
    }

    private static void RecordWildcardRule(GeoServerLayerAccessRule rule, TranslationContext context)
    {
        if (rule.Permission == "w")
        {
            if (rule.WorkspaceName == "*")
            {
                context.HasGlobalWildcardWrite = true;
            }
            else if (!string.IsNullOrWhiteSpace(rule.WorkspaceName))
            {
                context.WildcardWriteWorkspaces.Add(rule.WorkspaceName);
            }
        }

        context.ReviewItems.Add(CreateReviewItem(
            $"access-rule:{rule.RuleKey}",
            $"Wildcard GeoServer access rule '{rule.RuleKey}={rule.RawValue}' cannot be expressed as a single-layer access policy and was not applied as a broader grant."));
    }

    private static void ClassifyLayerRule(
        GeoServerLayerAccessRule rule,
        string layerSourceId,
        TranslationContext context)
    {
        if (rule.Permission is not ("r" or "w"))
        {
            context.ReviewItems.Add(CreateReviewItem(
                layerSourceId,
                $"GeoServer access rule '{rule.RuleKey}={rule.RawValue}' uses unsupported permission '{rule.Permission}'; admin or custom rules require manual configuration and were not applied."));
            return;
        }

        var accumulator = GetOrCreateAccumulator(context.Accumulators, layerSourceId);
        if (rule.Roles.Length == 0 || rule.Roles.Any(static role => !IsSupportedRoleToken(role)))
        {
            accumulator.MarkBlocked(rule.Permission);
            context.ReviewItems.Add(CreateReviewItem(
                layerSourceId,
                $"GeoServer access rule '{rule.RuleKey}={rule.RawValue}' has an empty or non-portable role expression and was not applied as a broader grant."));
            return;
        }

        accumulator.Add(rule.Permission, rule.Roles);
    }

    private static bool IsSupportedRoleToken(string role)
        => !string.IsNullOrWhiteSpace(role) &&
           !role.Contains(';', StringComparison.Ordinal) &&
           !role.Contains('=', StringComparison.Ordinal) &&
           !role.Contains(' ', StringComparison.Ordinal);

    private static LayerAccessAccumulator GetOrCreateAccumulator(
        Dictionary<string, LayerAccessAccumulator> accumulators,
        string sourceId)
    {
        if (!accumulators.TryGetValue(sourceId, out var accumulator))
        {
            accumulator = new LayerAccessAccumulator();
            accumulators[sourceId] = accumulator;
        }

        return accumulator;
    }

    private static MigrationManifestReviewItem CreateReviewItem(string sourceId, string reason)
        => new()
        {
            SourceId = sourceId,
            Kind = "access-rule",
            Code = ReviewCode,
            Severity = "manual-review",
            Reason = reason,
            ManualSteps = ["Configure the corresponding Honua access policy or workspace role assignment explicitly after review."],
            Warnings = [reason]
        };

    private static string BuildLayerKey(string workspaceName, string layerName)
        => $"{workspaceName}:{layerName}";

    private sealed class LayerAccessAccumulator
    {
        private bool _hasRead;
        private bool _hasWrite;
        private bool _readBlocked;
        private bool _writeBlocked;
        private bool _allowAnonymousRead;
        private bool _allowAnonymousWrite;
        private bool _anyAuthenticatedRead;
        private bool _anyAuthenticatedWrite;
        private readonly HashSet<string> _readRoles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _writeRoles = new(StringComparer.Ordinal);

        public void MarkBlocked(string permission)
        {
            if (permission == "r")
            {
                _readBlocked = true;
            }
            else if (permission == "w")
            {
                _writeBlocked = true;
            }
        }

        public void Add(string permission, string[] roles)
        {
            if (permission == "r")
            {
                _hasRead = true;
                AddReadRoles(roles);
                return;
            }

            _hasWrite = true;
            AddWriteRoles(roles);
        }

        private void AddReadRoles(string[] roles)
        {
            foreach (var role in roles)
            {
                if (IsAnonymousRole(role))
                {
                    _allowAnonymousRead = true;
                }
                else if (IsAuthenticatedRole(role))
                {
                    _anyAuthenticatedRead = true;
                }
                else
                {
                    _readRoles.Add(role);
                }
            }
        }

        private void AddWriteRoles(string[] roles)
        {
            foreach (var role in roles)
            {
                if (IsAnonymousRole(role))
                {
                    _allowAnonymousWrite = true;
                }
                else if (IsAuthenticatedRole(role))
                {
                    _anyAuthenticatedWrite = true;
                }
                else
                {
                    _writeRoles.Add(role);
                }
            }
        }

        private static bool IsAnonymousRole(string role)
            => role == "*" || string.Equals(role, "ROLE_ANONYMOUS", StringComparison.OrdinalIgnoreCase);

        private static bool IsAuthenticatedRole(string role)
            => string.Equals(role, "ROLE_AUTHENTICATED", StringComparison.OrdinalIgnoreCase);

        public bool TryBuildPolicy(bool hasWildcardWriteRule, out AccessPolicy? policy, out string? conflictReason)
        {
            policy = null;
            conflictReason = null;

            if (_readBlocked || _writeBlocked || (!_hasRead && !_hasWrite))
            {
                return false;
            }

            if (hasWildcardWriteRule && !_hasWrite)
            {
                conflictReason = "A wildcard write rule applies to this layer without an explicit per-layer write rule; policy deferred so read roles do not silently grant write access.";
                return false;
            }

            if (_anyAuthenticatedWrite && _readRoles.Count > 0 && !_allowAnonymousWrite)
            {
                conflictReason = "GeoServer write rule grants ROLE_AUTHENTICATED while read is role-restricted; requires manual review so access is not widened.";
                return false;
            }

            var allowedRoles = _allowAnonymousRead || _anyAuthenticatedRead || _readRoles.Count == 0
                ? null
                : _readRoles.OrderBy(static r => r, StringComparer.Ordinal).ToArray();
            var allowedWriteRoles = _allowAnonymousWrite || _anyAuthenticatedWrite || _writeRoles.Count == 0
                ? null
                : _writeRoles.OrderBy(static r => r, StringComparer.Ordinal).ToArray();

            policy = new AccessPolicy
            {
                AllowAnonymous = _allowAnonymousRead,
                AllowAnonymousWrite = _allowAnonymousWrite,
                AllowedRoles = allowedRoles,
                AllowedWriteRoles = allowedWriteRoles
            };
            return true;
        }
    }
}
