// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Translates source inventory artifacts into deterministic migration manifests.
/// </summary>
public static partial class MigrationManifestTranslator
{
    /// <summary>
    /// Translate source inventory into a target manifest artifact.
    /// </summary>
    /// <param name="inventory">Source inventory artifact.</param>
    /// <param name="options">Optional target naming options.</param>
    /// <returns>Deterministic manifest artifact.</returns>
    public static MigrationManifestArtifact Translate(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestTranslationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var targetServiceName = NormalizeName(
            options?.TargetServiceName ?? inventory.Source.DisplayName,
            fallback: "migration-service");
        var targetResources = new List<MigrationManifestTargetResource>();
        var styleActions = new List<MigrationManifestStyleAction>();
        var servicePlans = new List<MigrationManifestServicePlan>();
        var manualReviewItems = new List<MigrationManifestReviewItem>();
        var unsupportedItems = new List<MigrationManifestReviewItem>();
        var sourceProtocol = GetSourceProtocol(inventory);

        foreach (var resource in inventory.Resources.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AddReviewItems(
                inventory.SourceKind,
                resource.Id,
                resource.Kind,
                resource.Compatibility,
                manualReviewItems,
                unsupportedItems);

            if (IsIncompatible(resource.Compatibility.Level))
            {
                continue;
            }

            targetResources.Add(new MigrationManifestTargetResource
            {
                SourceResourceId = resource.Id,
                SourceKind = resource.Kind,
                Action = IsPartial(resource.Compatibility.Level) ? "manual-review" : "publish",
                TargetServiceName = targetServiceName,
                TargetResourceName = NormalizeName(resource.Name, fallback: resource.Id),
                GeometryType = resource.GeometryType,
                MigrationMode = GetResourceMigrationMode(inventory.SourceKind, resource),
                SourceProtocol = sourceProtocol,
                Fields = resource.Fields.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
                Capabilities = Order(resource.Capabilities),
                SpatialReferences = resource.SpatialReferences
                    .OrderBy(static item => item.Role, StringComparer.Ordinal)
                    .ThenBy(static item => item.SourceValue, StringComparer.Ordinal)
                    .ToArray(),
                StyleIds = Order(resource.StyleIds),
                ExternalDependencyIds = BuildResourceExternalDependencyIds(inventory, resource),
                Compatibility = resource.Compatibility
            });
        }

        foreach (var style in inventory.Styles.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AddReviewItems(
                inventory.SourceKind,
                style.Id,
                style.Kind,
                style.Compatibility,
                manualReviewItems,
                unsupportedItems);

            styleActions.Add(new MigrationManifestStyleAction
            {
                SourceStyleId = style.Id,
                Action = IsCompatible(style.Compatibility.Level) ? "import" : "manual-review",
                Format = style.Format,
                ResourceIds = Order(style.ResourceIds),
                ExternalDependencyIds = Order(style.ExternalDependencyIds),
                Compatibility = style.Compatibility
            });
        }

        foreach (var dependency in inventory.ExternalDependencies.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            AddReviewItems(
                inventory.SourceKind,
                dependency.Id,
                dependency.Kind,
                dependency.Compatibility,
                manualReviewItems,
                unsupportedItems);
        }

        servicePlans.AddRange(BuildServicePlans(inventory, targetServiceName));

        return new MigrationManifestArtifact
        {
            SourceArtifactKind = inventory.ArtifactKind,
            SourceArtifactVersion = inventory.ArtifactVersion,
            SourceKind = inventory.SourceKind,
            Source = inventory.Source,
            Summary = new MigrationManifestSummary
            {
                SourceResourceCount = inventory.Resources.Length,
                TargetResourceCount = targetResources.Count,
                StyleActionCount = styleActions.Count,
                ServicePlanCount = servicePlans.Count,
                ManualReviewCount = manualReviewItems.Count,
                UnsupportedCount = unsupportedItems.Count
            },
            TargetResources = targetResources.ToArray(),
            StyleActions = styleActions.ToArray(),
            ServicePlans = servicePlans
                .OrderBy(static item => item.SourceContainerId, StringComparer.Ordinal)
                .ToArray(),
            ManualReviewItems = manualReviewItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray(),
            UnsupportedItems = unsupportedItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static void AddReviewItems(
        string sourceKind,
        string sourceId,
        string kind,
        MigrationCompatibilityAssessment compatibility,
        List<MigrationManifestReviewItem> manualReviewItems,
        List<MigrationManifestReviewItem> unsupportedItems)
    {
        if (IsCompatible(compatibility.Level))
        {
            return;
        }

        var reviewItem = new MigrationManifestReviewItem
        {
            SourceId = sourceId,
            Kind = kind,
            Code = compatibility.Code ?? BuildFallbackCode(sourceKind, kind, compatibility.Level),
            Severity = IsIncompatible(compatibility.Level) ? "unsupported" : "manual-review",
            Reason = compatibility.Reason,
            ManualSteps = Order(compatibility.ManualSteps),
            Warnings = Order(compatibility.Warnings)
        };

        if (IsIncompatible(compatibility.Level))
        {
            unsupportedItems.Add(reviewItem);
        }
        else
        {
            manualReviewItems.Add(reviewItem);
        }
    }

    private static MigrationManifestServicePlan[] BuildServicePlans(
        MigrationSourceInventoryArtifact inventory,
        string targetServiceName)
    {
        if (!IsOgcRenderOnlySource(inventory.SourceKind))
        {
            return [];
        }

        var serviceType = GetSourceProtocol(inventory);
        return inventory.Containers
            .OrderBy(static container => container.Id, StringComparer.Ordinal)
            .Select(container =>
            {
                var resources = inventory.Resources
                    .Where(resource => string.Equals(resource.ContainerId, container.Id, StringComparison.Ordinal));
                var styles = inventory.Styles
                    .Where(style => string.Equals(style.ContainerId, container.Id, StringComparison.Ordinal));
                var dependencies = inventory.ExternalDependencies
                    .Where(dependency => string.Equals(dependency.ContainerId, container.Id, StringComparison.Ordinal));

                return new MigrationManifestServicePlan
                {
                    SourceContainerId = container.Id,
                    SourceKind = container.Kind,
                    Action = "manual-review",
                    TargetServiceName = targetServiceName,
                    ServiceType = serviceType,
                    ResourceIds = Order(resources.Select(static resource => resource.Id)),
                    StyleIds = Order(styles.Select(static style => style.Id)),
                    ExternalDependencyIds = Order(dependencies.Select(static dependency => dependency.Id)),
                    Compatibility = container.Compatibility
                };
            })
            .ToArray();
    }

    private static string[] BuildResourceExternalDependencyIds(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource resource)
    {
        var dependencyIds = resource.ExternalDependencyIds.AsEnumerable();
        if (string.Equals(inventory.SourceKind, "ogc-wfs", StringComparison.OrdinalIgnoreCase))
        {
            dependencyIds = dependencyIds.Concat(inventory.ExternalDependencies
                .Where(static dependency => IsOgcWfsEndpointDependency(dependency))
                .Select(static dependency => dependency.Id));
        }

        return Order(dependencyIds);
    }

    private static bool IsOgcWfsEndpointDependency(MigrationExternalDependency dependency)
        => string.Equals(dependency.Kind, "ogc-endpoint", StringComparison.OrdinalIgnoreCase) &&
           dependency.Metadata.TryGetValue("service", out var service) &&
           string.Equals(service, "WFS", StringComparison.OrdinalIgnoreCase);

    private static string? GetResourceMigrationMode(string sourceKind, MigrationInventoryResource resource)
        => string.Equals(sourceKind, "ogc-wfs", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(resource.Kind, "feature-type", StringComparison.OrdinalIgnoreCase)
            ? "feature-import"
            : null;

    private static string? GetSourceProtocol(MigrationSourceInventoryArtifact inventory)
    {
        if (!string.IsNullOrWhiteSpace(inventory.Source.ServiceType))
        {
            return inventory.Source.ServiceType.Trim();
        }

        return inventory.SourceKind.ToLowerInvariant() switch
        {
            "ogc-wfs" => "WFS",
            "ogc-wms" => "WMS",
            "ogc-wmts" => "WMTS",
            _ => null
        };
    }

    private static bool IsOgcRenderOnlySource(string sourceKind)
        => sourceKind.Equals("ogc-wms", StringComparison.OrdinalIgnoreCase) ||
           sourceKind.Equals("ogc-wmts", StringComparison.OrdinalIgnoreCase);

    private static string BuildFallbackCode(string sourceKind, string kind, string level)
        => $"{ToCodePart(sourceKind)}_{ToCodePart(kind)}_{ToCodePart(level)}";

    private static string ToCodePart(string value)
        => CodeUnsafeCharacters().Replace(value.Trim(), "_").Trim('_').ToUpperInvariant();

    private static string NormalizeName(string value, string fallback)
    {
        var normalized = NameUnsafeCharacters()
            .Replace(value.Trim(), "-")
            .Trim('-')
            .ToLowerInvariant();

        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized.Length <= 64 ? normalized : normalized[..64].TrimEnd('-');
    }

    private static string[] Order(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCompatible(string? level)
        => string.Equals(level, "compatible", StringComparison.OrdinalIgnoreCase);

    private static bool IsPartial(string? level)
        => string.Equals(level, "partial", StringComparison.OrdinalIgnoreCase);

    private static bool IsIncompatible(string? level)
        => string.Equals(level, "incompatible", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex CodeUnsafeCharacters();

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex NameUnsafeCharacters();
}
