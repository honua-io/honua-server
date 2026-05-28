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
/// Deterministic migration planner for OGC WMTS sources. Given a slice-1 source
/// inventory artifact, produces classified plan entries that tag layer metadata
/// as <c>automated</c>, SLD/SE style references as <c>assisted</c>, tile-set
/// definitions as <c>automated</c> when they describe a trivial XYZ/TMS-like
/// grid (WebMercatorQuad and friends) and <c>manual-review</c> otherwise, and
/// the render-only tile data path as <c>unsupported</c>. The planner does not
/// perform any I/O; it operates purely on the inventory artifact and is
/// idempotent.
/// </summary>
public static class OgcWmtsMigrationPlanner
{
    private const string SourceKindOgcWmts = "ogc-wmts";

    /// <summary>
    /// Names of well-known WMTS tile matrix sets that map deterministically to a
    /// trivial XYZ/TMS-like Honua tile cache configuration. Comparison is
    /// case-insensitive.
    /// </summary>
    private static readonly HashSet<string> TrivialTileMatrixSetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "WebMercatorQuad",
        "GoogleMapsCompatible",
        "GoogleCRS84Quad",
        "WorldCRS84Quad",
        "GlobalCRS84Pixel",
        "GlobalCRS84Scale",
        "EPSG:3857",
        "urn:ogc:def:wkss:OGC:1.0:GoogleMapsCompatible"
    };

    /// <summary>
    /// Returns <c>true</c> when the inventory describes a WMTS source the planner can plan.
    /// </summary>
    /// <param name="inventory">Source inventory artifact.</param>
    /// <returns>Whether the inventory is a WMTS source.</returns>
    public static bool CanPlan(MigrationSourceInventoryArtifact inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return string.Equals(inventory.SourceKind, SourceKindOgcWmts, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the classified plan entries and diagnostics for the WMTS service plan.
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

        foreach (var resource in inventory.Resources
                     .Where(r => string.Equals(r.ContainerId, containerId, StringComparison.Ordinal))
                     .OrderBy(static r => r.Id, StringComparer.Ordinal))
        {
            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wmts:metadata:{resource.Id}",
                SourceId = resource.Id,
                SourceKind = resource.Kind,
                Category = "layer-metadata",
                AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
                Code = ImportCompatibilityCodes.OgcWmtsMetadataAutomated,
                Name = resource.Name,
                Reason = "WMTS layer metadata (identifier, title, abstract, bounds, formats) projects deterministically.",
                Metadata = BuildMetadata(
                    ("capabilities", JoinOrdered(resource.Capabilities)),
                    ("tileMatrixSets", JoinOrdered(resource.ExternalDependencyIds)))
            });

            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wmts:tile-data:{resource.Id}",
                SourceId = resource.Id,
                SourceKind = resource.Kind,
                Category = "tile-data",
                AutomationStatus = MigrationFidelityAutomationStatuses.Unsupported,
                Code = ImportCompatibilityCodes.OgcWmtsTileDataUnsupported,
                Name = resource.Name,
                Reason = "WMTS exposes pre-rendered tiles and cannot supply automated feature data-copy by itself.",
                ManualSteps =
                [
                    "Pair this WMTS layer with a WFS, coverage, database, or file source before planning data import."
                ]
            });
        }

        foreach (var style in inventory.Styles
                     .Where(s => string.Equals(s.ContainerId, containerId, StringComparison.Ordinal))
                     .OrderBy(static s => s.Id, StringComparer.Ordinal))
        {
            entries.Add(new MigrationManifestPlanEntry
            {
                Id = $"plan:wmts:style:{style.Id}",
                SourceId = style.Id,
                SourceKind = style.Kind,
                Category = "style",
                AutomationStatus = MigrationFidelityAutomationStatuses.Assisted,
                Code = ImportCompatibilityCodes.OgcRenderStyleAssisted,
                Name = style.Name,
                Reason = "WMTS style reference was captured for assisted style import in a later slice; no auto-import was performed.",
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
                Message = $"WMTS style '{style.Name}' captured for assisted SLD/SE import; not auto-imported by this slice."
            });
        }

        foreach (var dependency in inventory.ExternalDependencies
                     .Where(d => string.Equals(d.ContainerId, containerId, StringComparison.Ordinal))
                     .OrderBy(static d => d.Id, StringComparer.Ordinal))
        {
            if (IsTileMatrixSet(dependency))
            {
                var trivial = IsTrivialTileMatrixSet(dependency);
                entries.Add(new MigrationManifestPlanEntry
                {
                    Id = $"plan:wmts:tile-set:{dependency.Id}",
                    SourceId = dependency.Id,
                    SourceKind = dependency.Kind,
                    Category = "tile-set",
                    AutomationStatus = trivial
                        ? MigrationFidelityAutomationStatuses.Automated
                        : MigrationFidelityAutomationStatuses.ManualReview,
                    Code = trivial
                        ? ImportCompatibilityCodes.OgcWmtsTileMatrixAutomated
                        : ImportCompatibilityCodes.OgcWmtsTileMatrixManualReview,
                    Name = dependency.Name,
                    Reason = trivial
                        ? "Tile matrix set matches a well-known XYZ/TMS-compatible grid and can be projected to a Honua tile cache automatically."
                        : "Tile matrix set is non-trivial; operator review is required before mapping to a Honua tile cache.",
                    ManualSteps = trivial
                        ? []
                        :
                        [
                            "Compare tile matrix scales, origin, tile size, and CRS against the target Honua cache and confirm the mapping."
                        ],
                    Metadata = BuildMetadata(("dependencyType", dependency.DependencyType))
                });
                continue;
            }

            if (IsTileRenderEndpoint(dependency))
            {
                entries.Add(new MigrationManifestPlanEntry
                {
                    Id = $"plan:wmts:render-endpoint:{dependency.Id}",
                    SourceId = dependency.Id,
                    SourceKind = dependency.Kind,
                    Category = "render-endpoint",
                    AutomationStatus = MigrationFidelityAutomationStatuses.ManualReview,
                    Code = ImportCompatibilityCodes.OgcRenderEndpointPlanned,
                    Name = dependency.Name,
                    Reason = "WMTS tile endpoint metadata captured for manual tile-service migration planning.",
                    ManualSteps =
                    [
                        "Review equivalent Honua tile-service routing, cache grid configuration, and client cutover URLs."
                    ],
                    Metadata = BuildMetadata(
                        ("dependencyType", dependency.DependencyType),
                        ("address", dependency.Address))
                });
            }
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

    private static bool IsTileMatrixSet(MigrationExternalDependency dependency)
        => string.Equals(dependency.Kind, "tile-matrix-set", StringComparison.OrdinalIgnoreCase);

    private static bool IsTileRenderEndpoint(MigrationExternalDependency dependency)
        => string.Equals(dependency.Kind, "ogc-endpoint", StringComparison.OrdinalIgnoreCase) &&
           dependency.Metadata.TryGetValue("service", out var service) &&
           string.Equals(service, "WMTS", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrivialTileMatrixSet(MigrationExternalDependency dependency)
    {
        if (!string.IsNullOrWhiteSpace(dependency.Name) &&
            TrivialTileMatrixSetNames.Contains(dependency.Name.Trim()))
        {
            return true;
        }

        if (dependency.Metadata.TryGetValue("wellKnownScaleSet", out var wellKnown) &&
            !string.IsNullOrWhiteSpace(wellKnown) &&
            TrivialTileMatrixSetNames.Contains(wellKnown.Trim()))
        {
            return true;
        }

        return false;
    }

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
}
