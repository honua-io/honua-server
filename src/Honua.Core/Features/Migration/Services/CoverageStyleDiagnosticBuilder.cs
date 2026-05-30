// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Inspects <see cref="MigrationInventoryResource"/> coverage records and emits
/// <see cref="MigrationCoverageStyleDiagnostic"/> entries describing per-style
/// hints (color maps, band statistics, NoData markers, transparency presets,
/// legends, vendor-specific rendering hints) that need separate operator
/// attention when migrating to Honua. Slice 4 of issue #1030.
/// </summary>
/// <remarks>
/// Classification rules:
/// <list type="bullet">
///   <item><description>Single-band grayscale with stats → automated; suggests
///   <c>grayscale-linear-stretch</c>.</description></item>
///   <item><description>Indexed color tables (≤256 entries) → assisted; preserves
///   the source table and emits an informational diagnostic so it can be
///   transferred verbatim.</description></item>
///   <item><description>Continuous color ramps with formulas or unresolved color
///   tables → manual-review; the raw style is preserved but a warning is
///   raised.</description></item>
///   <item><description>Vendor-specific extension markers (Esri, GeoServer, etc.)
///   → manual-review with the vendor name surfaced so operators know which
///   product authored the style.</description></item>
/// </list>
/// The builder is deterministic: it ignores input ordering and emits records
/// sorted by source coverage id, kind, then source style id.
/// </remarks>
internal static class CoverageStyleDiagnosticBuilder
{
    private const string KindColorMap = "colorMap";
    private const string KindBandStatistics = "bandStatistics";
    private const string KindNoDataValue = "noDataValue";
    private const string KindTransparency = "transparency";
    private const string KindLegend = "legend";
    private const string KindRenderingHint = "renderingHint";

    private const string ClassificationAutomated = "automated";
    private const string ClassificationAssisted = "assisted";
    private const string ClassificationManualReview = "manual-review";

    private const string SuggestedGrayscaleLinearStretch = "grayscale-linear-stretch";
    private const string SuggestedIndexedColor = "indexed-color-table";

    /// <summary>
    /// Builds the deterministic ordered diagnostic collection for the supplied
    /// inventory coverage resources.
    /// </summary>
    /// <param name="resources">Coverage resources from the source migration inventory.</param>
    /// <returns>Empty array when no style hints were discovered.</returns>
    public static MigrationCoverageStyleDiagnostic[] Build(IEnumerable<MigrationInventoryResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var diagnostics = new List<MigrationCoverageStyleDiagnostic>();
        foreach (var resource in resources)
        {
            if (resource is null ||
                !string.Equals(resource.Kind, "coverage", StringComparison.Ordinal))
            {
                continue;
            }

            BuildForResource(resource, diagnostics);
        }

        return diagnostics
            .OrderBy(static d => d.SourceCoverageId, StringComparer.Ordinal)
            .ThenBy(static d => d.Kind, StringComparer.Ordinal)
            .ThenBy(static d => d.SourceStyleId ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static void BuildForResource(
        MigrationInventoryResource resource,
        List<MigrationCoverageStyleDiagnostic> sink)
    {
        var coverageId = resource.Name;

        // Band statistics + grayscale stretch preset.
        var bandCount = resource.Fields.Length;
        if (bandCount > 0)
        {
            var firstBand = resource.Fields[0];
            var hasStats = LooksLikeNumeric(firstBand.FieldType);

            if (bandCount == 1 && hasStats)
            {
                sink.Add(new MigrationCoverageStyleDiagnostic
                {
                    Kind = KindBandStatistics,
                    Classification = ClassificationAutomated,
                    SourceCoverageId = coverageId,
                    SourceStyleId = firstBand.Name,
                    Reason = $"Single-band grayscale coverage ({firstBand.FieldType}) detected; a linear stretch preset can be auto-applied at apply time.",
                    SuggestedTargetStyleId = SuggestedGrayscaleLinearStretch
                });
            }
            else if (bandCount > 1)
            {
                sink.Add(new MigrationCoverageStyleDiagnostic
                {
                    Kind = KindBandStatistics,
                    Classification = ClassificationAssisted,
                    SourceCoverageId = coverageId,
                    Reason = $"Multi-band coverage ({bandCount} bands) requires per-band statistics to recreate the source rendering; preserve the source band metadata when authoring the target style.",
                    ManualSteps =
                    [
                        "Capture per-band min/max/mean/stddev from the source service before promoting the target style.",
                        "Pick a multi-band renderer (RGB, false-color, multispectral composite) on the target layer and bind it to the imported raster."
                    ]
                });
            }
        }

        // NoData values surfaced via field DomainName (range unit) or capability marker.
        foreach (var field in resource.Fields)
        {
            if (HasNoDataMarker(field))
            {
                sink.Add(new MigrationCoverageStyleDiagnostic
                {
                    Kind = KindNoDataValue,
                    Classification = ClassificationAutomated,
                    SourceCoverageId = coverageId,
                    SourceStyleId = field.Name,
                    Reason = $"NoData marker advertised for band {field.Name}; the slice-2 GeoTIFF import preserves NoData verbatim, but the target style must mask matching pixels."
                });
            }
        }

        // StyleIds → color maps / vendor markers / continuous ramps.
        foreach (var styleId in resource.StyleIds)
        {
            if (string.IsNullOrWhiteSpace(styleId))
            {
                continue;
            }

            var classified = ClassifyStyleId(styleId, coverageId);
            if (classified is not null)
            {
                sink.Add(classified);
            }
        }

        // Capabilities / warning markers — transparency presets and rendering hints.
        foreach (var capability in resource.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                continue;
            }

            var diagnostic = ClassifyCapability(capability, coverageId);
            if (diagnostic is not null)
            {
                sink.Add(diagnostic);
            }
        }

        // Compatibility warnings → legend / rendering hint surfaces.
        foreach (var warning in resource.Compatibility.Warnings)
        {
            if (string.IsNullOrWhiteSpace(warning))
            {
                continue;
            }

            var diagnostic = ClassifyWarning(warning, coverageId);
            if (diagnostic is not null)
            {
                sink.Add(diagnostic);
            }
        }
    }

    private static bool LooksLikeNumeric(string? fieldType)
    {
        if (string.IsNullOrWhiteSpace(fieldType))
        {
            return false;
        }

        var lower = fieldType.ToLower(CultureInfo.InvariantCulture);
        return lower.Contains("int", StringComparison.Ordinal) ||
            lower.Contains("float", StringComparison.Ordinal) ||
            lower.Contains("double", StringComparison.Ordinal) ||
            lower.Contains("byte", StringComparison.Ordinal) ||
            lower.Contains("real", StringComparison.Ordinal) ||
            lower.Contains("numeric", StringComparison.Ordinal);
    }

    private static bool HasNoDataMarker(MigrationInventoryField field)
    {
        if (field is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(field.DomainName) &&
            field.DomainName.Contains("nodata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(field.DomainType) &&
            field.DomainType.Contains("nodata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static MigrationCoverageStyleDiagnostic? ClassifyStyleId(string styleId, string coverageId)
    {
        var trimmed = styleId.Trim();
        var lower = trimmed.ToLower(CultureInfo.InvariantCulture);

        // Vendor markers first — these dominate the classification.
        var vendor = DetectVendor(lower);
        if (vendor is not null)
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindRenderingHint,
                Classification = ClassificationManualReview,
                SourceCoverageId = coverageId,
                SourceStyleId = trimmed,
                VendorName = vendor,
                Reason = $"Vendor-specific {vendor} style extension '{trimmed}' cannot be migrated automatically; recreate the equivalent renderer in Honua.",
                ManualSteps =
                [
                    $"Export the source {vendor} style definition for reference.",
                    "Author an equivalent renderer in Honua and bind it to the imported coverage."
                ]
            };
        }

        // Continuous ramp / formula markers → manual review.
        if (LooksLikeContinuousRamp(lower))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindColorMap,
                Classification = ClassificationManualReview,
                SourceCoverageId = coverageId,
                SourceStyleId = trimmed,
                Reason = $"Continuous color ramp '{trimmed}' uses a formula-based interpolation; preserve the source style document and rebuild the equivalent ramp on the target.",
                ManualSteps =
                [
                    "Export the source style document (SLD, GDAL color table, or OGC API styles JSON) and store it alongside the migration manifest.",
                    "Recreate the continuous ramp renderer on the Honua target layer."
                ]
            };
        }

        // Indexed color map → assisted.
        if (LooksLikeIndexedColor(lower))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindColorMap,
                Classification = ClassificationAssisted,
                SourceCoverageId = coverageId,
                SourceStyleId = trimmed,
                SuggestedTargetStyleId = SuggestedIndexedColor,
                Reason = $"Indexed color table '{trimmed}' (≤256 entries) can be transferred verbatim; preserve the source palette when registering the target style."
            };
        }

        if (lower.Contains("legend", StringComparison.Ordinal))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindLegend,
                Classification = ClassificationManualReview,
                SourceCoverageId = coverageId,
                SourceStyleId = trimmed,
                Reason = $"Legend hint '{trimmed}' must be regenerated against the target rendering pipeline; the source legend graphic is not transferable.",
                ManualSteps = ["Generate a fresh legend graphic after the target style is published."]
            };
        }

        if (lower.Contains("transparen", StringComparison.Ordinal) ||
            lower.Contains("alpha", StringComparison.Ordinal))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindTransparency,
                Classification = ClassificationAssisted,
                SourceCoverageId = coverageId,
                SourceStyleId = trimmed,
                Reason = $"Transparency preset '{trimmed}' detected; mirror the opacity on the target style after import."
            };
        }

        // Unknown but present style id → record as rendering hint manual-review so it isn't silently dropped.
        return new MigrationCoverageStyleDiagnostic
        {
            Kind = KindRenderingHint,
            Classification = ClassificationManualReview,
            SourceCoverageId = coverageId,
            SourceStyleId = trimmed,
            Reason = $"Source style '{trimmed}' could not be classified automatically; review and recreate on the target.",
            ManualSteps = ["Fetch the source style document and decide whether to import verbatim, adapt, or skip."]
        };
    }

    private static MigrationCoverageStyleDiagnostic? ClassifyCapability(string capability, string coverageId)
    {
        var lower = capability.Trim().ToLower(CultureInfo.InvariantCulture);
        if (lower.Contains("transparen", StringComparison.Ordinal) || lower.Contains("alpha", StringComparison.Ordinal))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindTransparency,
                Classification = ClassificationAssisted,
                SourceCoverageId = coverageId,
                Reason = $"Coverage advertises a transparency capability ('{capability}'); confirm the matching alpha treatment on the target style."
            };
        }

        return null;
    }

    private static MigrationCoverageStyleDiagnostic? ClassifyWarning(string warning, string coverageId)
    {
        var lower = warning.ToLower(CultureInfo.InvariantCulture);
        if (lower.Contains("legend", StringComparison.Ordinal))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindLegend,
                Classification = ClassificationManualReview,
                SourceCoverageId = coverageId,
                Reason = $"Coverage scan flagged legend hint: {warning}.",
                ManualSteps = ["Inspect the source legend artifact and regenerate it on the target."]
            };
        }

        if (lower.Contains("color") || lower.Contains("renderer") || lower.Contains("rendering"))
        {
            return new MigrationCoverageStyleDiagnostic
            {
                Kind = KindRenderingHint,
                Classification = ClassificationManualReview,
                SourceCoverageId = coverageId,
                Reason = $"Coverage scan flagged rendering hint: {warning}.",
                ManualSteps = ["Review the rendering hint and reproduce the equivalent treatment on the target style."]
            };
        }

        return null;
    }

    private static string? DetectVendor(string lower)
    {
        if (lower.Contains("esri", StringComparison.Ordinal) ||
            lower.StartsWith("arcgis", StringComparison.Ordinal) ||
            lower.Contains("arcmap", StringComparison.Ordinal))
        {
            return "Esri";
        }

        if (lower.Contains("geoserver", StringComparison.Ordinal) ||
            lower.Contains("gs:", StringComparison.Ordinal) ||
            lower.StartsWith("sld_", StringComparison.Ordinal))
        {
            return "GeoServer";
        }

        if (lower.Contains("mapinfo", StringComparison.Ordinal))
        {
            return "MapInfo";
        }

        if (lower.Contains("hexagon", StringComparison.Ordinal) ||
            lower.Contains("erdas", StringComparison.Ordinal))
        {
            return "Hexagon";
        }

        return null;
    }

    private static bool LooksLikeContinuousRamp(string lower)
        => lower.Contains("ramp", StringComparison.Ordinal) ||
           lower.Contains("continuous", StringComparison.Ordinal) ||
           lower.Contains("gradient", StringComparison.Ordinal) ||
           lower.Contains("interpolat", StringComparison.Ordinal) ||
           lower.Contains("formula", StringComparison.Ordinal);

    private static bool LooksLikeIndexedColor(string lower)
        => lower.Contains("indexed", StringComparison.Ordinal) ||
           lower.Contains("palette", StringComparison.Ordinal) ||
           lower.Contains("colormap", StringComparison.Ordinal) ||
           lower.Contains("color-map", StringComparison.Ordinal) ||
           lower.Contains("colortable", StringComparison.Ordinal) ||
           lower.Contains("color-table", StringComparison.Ordinal) ||
           lower.Contains("lookup", StringComparison.Ordinal) ||
           lower.Contains("classified", StringComparison.Ordinal);
}
