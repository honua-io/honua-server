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
        var manualReviewItems = new List<MigrationManifestReviewItem>();
        var unsupportedItems = new List<MigrationManifestReviewItem>();

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
                Fields = resource.Fields.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray(),
                Capabilities = Order(resource.Capabilities),
                SpatialReferences = resource.SpatialReferences
                    .OrderBy(static item => item.Role, StringComparer.Ordinal)
                    .ThenBy(static item => item.SourceValue, StringComparer.Ordinal)
                    .ToArray(),
                StyleIds = Order(resource.StyleIds),
                ExternalDependencyIds = Order(resource.ExternalDependencyIds),
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
                ManualReviewCount = manualReviewItems.Count,
                UnsupportedCount = unsupportedItems.Count
            },
            TargetResources = targetResources.ToArray(),
            StyleActions = styleActions.ToArray(),
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
