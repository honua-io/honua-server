// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Deterministic migration planner for OGC WMS sources. Given a slice-1 source
/// inventory artifact, produces classified plan entries that tag layer metadata
/// as <c>automated</c>, SLD/SE style references as <c>assisted</c>, and the
/// render-only data path as <c>unsupported</c>. The planner does not perform
/// any I/O; it operates purely on the inventory artifact and is idempotent so
/// re-planning produces an equal output.
/// </summary>
public static class OgcWmsMigrationPlanner
{
    private const string SourceKindOgcWms = "ogc-wms";

    /// <summary>
    /// Returns <c>true</c> when the inventory describes a WMS source the planner can plan.
    /// </summary>
    /// <param name="inventory">Source inventory artifact.</param>
    /// <returns>Whether the inventory is a WMS source.</returns>
    public static bool CanPlan(MigrationSourceInventoryArtifact inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return string.Equals(inventory.SourceKind, SourceKindOgcWms, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the classified plan entries and diagnostics for the WMS service plan.
    /// </summary>
    /// <param name="inventory">Source inventory artifact.</param>
    /// <param name="containerId">Service container identifier to plan for.</param>
    /// <returns>Deterministic plan-entry and diagnostic collections.</returns>
    public static OgcRenderMigrationPlanResult Plan(
        MigrationSourceInventoryArtifact inventory,
        string containerId)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrEmpty(containerId);

        if (!CanPlan(inventory))
        {
            return OgcRenderMigrationPlanResult.Empty;
        }

        var entries = new List<MigrationManifestPlanEntry>();
        var diagnostics = new List<MigrationManifestPlanDiagnostic>();

        var companionWfsCapabilitiesUrl = DeriveCompanionWfsCapabilitiesUrl(inventory.Source.BaseUrl);

        foreach (var resource in inventory.Resources
                     .Where(r => string.Equals(r.ContainerId, containerId, StringComparison.Ordinal))
                     .OrderBy(static r => r.Id, StringComparer.Ordinal))
        {
            // Layer metadata projects deterministically (name, title, abstract,
            // bbox, srs list). The render data path is separately unsupported.
            // Attach a deterministic preferred-companion-source hint so operators
            // can probe a sibling WFS GetCapabilities to pair the layer with a
            // feature source for applied import.
            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wms:metadata:{resource.Id}",
                SourceId = resource.Id,
                SourceKind = resource.Kind,
                Category = "layer-metadata",
                AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
                Code = ImportCompatibilityCodes.OgcWmsMetadataAutomated,
                Name = resource.Name,
                Reason = "WMS layer metadata (name, abstract, bounds, srs list, keywords) projects deterministically.",
                Metadata = BuildMetadata(
                    ("capabilities", JoinOrdered(resource.Capabilities)),
                    ("spatialReferences", JoinSpatialReferences(resource.SpatialReferences)),
                    ("companionSourceKind", string.IsNullOrEmpty(companionWfsCapabilitiesUrl) ? null : "ogc-wfs"),
                    ("companionCapabilitiesUrl", companionWfsCapabilitiesUrl),
                    ("companionTypeNameHint", string.IsNullOrEmpty(companionWfsCapabilitiesUrl) ? null : resource.Name))
            });

            if (!string.IsNullOrEmpty(companionWfsCapabilitiesUrl))
            {
                diagnostics.Add(new MigrationManifestPlanDiagnostic
                {
                    SourceId = resource.Id,
                    Code = ImportCompatibilityCodes.OgcWmsCompanionSourceHint,
                    Severity = "info",
                    Message = $"WMS layer '{resource.Name}' offers a deterministic WFS companion-source hint at {companionWfsCapabilitiesUrl}; probe to confirm before pairing for applied import."
                });
            }

            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wms:render-data:{resource.Id}",
                SourceId = resource.Id,
                SourceKind = resource.Kind,
                Category = "render-data",
                AutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
                Code = ImportCompatibilityCodes.OgcWmsRenderDataUnsupported,
                Name = resource.Name,
                Reason = "WMS exposes rendered map images and cannot supply automated feature data-copy by itself.",
                ManualSteps =
                [
                    "Pair this WMS layer with a WFS, coverage, database, or file source before planning data import."
                ]
            });
        }

        foreach (var style in inventory.Styles
                     .Where(s => string.Equals(s.ContainerId, containerId, StringComparison.Ordinal))
                     .OrderBy(static s => s.Id, StringComparer.Ordinal))
        {
            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wms:style:{style.Id}",
                SourceId = style.Id,
                SourceKind = style.Kind,
                Category = "style",
                AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
                Code = ImportCompatibilityCodes.OgcRenderStyleAssisted,
                Name = style.Name,
                Reason = "WMS style reference was captured for assisted style import in a later slice; no auto-import was performed.",
                ManualSteps =
                [
                    "Confirm SLD/SE document is reachable and approve assisted style import in the follow-up slice."
                ],
                Metadata = BuildMetadata(
                    ("format", style.Format),
                    ("resources", JoinOrdered(style.ResourceIds)))
            });

            diagnostics.Add(new MigrationManifestPlanDiagnostic
            {
                SourceId = style.Id,
                Code = ImportCompatibilityCodes.OgcRenderStyleAssisted,
                Severity = "info",
                Message = $"WMS style '{style.Name}' captured for assisted SLD/SE import; not auto-imported by this slice."
            });
        }

        foreach (var dependency in inventory.ExternalDependencies
                     .Where(d => string.Equals(d.ContainerId, containerId, StringComparison.Ordinal))
                     .Where(static d => IsRenderEndpoint(d))
                     .OrderBy(static d => d.Id, StringComparer.Ordinal))
        {
            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wms:render-endpoint:{dependency.Id}",
                SourceId = dependency.Id,
                SourceKind = dependency.Kind,
                Category = "render-endpoint",
                AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
                Code = ImportCompatibilityCodes.OgcRenderEndpointPlanned,
                Name = dependency.Name,
                Reason = "WMS render endpoint metadata captured for manual map-service migration planning.",
                ManualSteps =
                [
                    "Review equivalent Honua map-service routing, supported formats, and client cutover URLs."
                ],
                Metadata = BuildMetadata(
                    ("dependencyType", dependency.DependencyType),
                    ("address", dependency.Address))
            });
        }

        return new OgcRenderMigrationPlanResult(
            entries
                .OrderBy(static e => e.Id, StringComparer.Ordinal)
                .ToArray(),
            diagnostics
                .OrderBy(static d => d.SourceId, StringComparer.Ordinal)
                .ThenBy(static d => d.Code, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Derive a deterministic WFS GetCapabilities URL adjacent to a WMS service base URL.
    /// Reuses scheme, host, port, and path; replaces the query with a normalized
    /// <c>service=WFS&amp;request=GetCapabilities</c>. Returns an empty string when the
    /// base URL is missing or unparseable so callers can omit the hint cleanly.
    /// </summary>
    /// <param name="baseUrl">WMS service base URL recorded on the inventory source identity.</param>
    /// <returns>Normalized companion WFS capabilities URL or empty string when not derivable.</returns>
    internal static string DeriveCompanionWfsCapabilitiesUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var parsed))
        {
            return string.Empty;
        }

        var builder = new UriBuilder(parsed)
        {
            Query = "service=WFS&request=GetCapabilities",
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static bool IsRenderEndpoint(MigrationExternalDependency dependency)
        => string.Equals(dependency.Kind, "ogc-endpoint", StringComparison.OrdinalIgnoreCase) &&
           dependency.Metadata.TryGetValue("service", out var service) &&
           string.Equals(service, "WMS", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildMetadata(params (string Key, string? Value)[] pairs)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value.Trim();
            }
        }

        return metadata;
    }

    private static string JoinOrdered(IEnumerable<string> values)
        => string.Join(",", values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal));

    private static string JoinSpatialReferences(IEnumerable<MigrationSpatialReferenceInfo> references)
        => string.Join(",", references
            .Select(static reference => reference.SourceValue ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal));
}

/// <summary>
/// Result of a render-only OGC migration planning pass.
/// </summary>
/// <param name="Entries">Classified plan entries.</param>
/// <param name="Diagnostics">Plan diagnostics.</param>
public readonly record struct OgcRenderMigrationPlanResult(
    MigrationManifestPlanEntry[] Entries,
    MigrationManifestPlanDiagnostic[] Diagnostics)
{
    /// <summary>
    /// Empty plan result.
    /// </summary>
    public static OgcRenderMigrationPlanResult Empty { get; } = new([], []);
}
