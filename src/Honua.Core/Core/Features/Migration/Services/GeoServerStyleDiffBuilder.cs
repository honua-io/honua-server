// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Core.Features.Migration.Services;

internal sealed record GeoServerLayerStyleDiffResult(
    MigrationStyleDiff[] Diffs,
    MigrationManifestReviewItem[] ReviewItems);

internal static class GeoServerStyleDiffBuilder
{
    public const string StyleDiffReviewCode = "geoserver-style-diff";

    private static readonly (string ElementName, string Description)[] NonDrawnSldConstructs =
    [
        ("ElseFilter", "ElseFilter has no portable MapLibre equivalent; rule filter is dropped."),
        ("TextSymbolizer", "TextSymbolizer labels and placement are not drawn by the GeoServer style importer without manual review."),
        ("RasterSymbolizer", "RasterSymbolizer is not drawn by the vector SLD importer."),
        ("ExternalGraphic", "ExternalGraphic references external assets that are not mirrored automatically."),
        ("GraphicFill", "GraphicFill pattern fills are not portable to MapLibre."),
        ("GraphicStroke", "GraphicStroke pattern strokes are not portable to MapLibre."),
        ("VendorOption", "GeoServer VendorOption hints are ignored during SLD conversion."),
        ("Transformation", "GeoServer rendering Transformation functions are not supported.")
    ];

    public static GeoServerLayerStyleDiffResult BuildForLayer(
        string layerSourceId,
        IReadOnlyList<(string StyleId, GeoServerStyleInfo Style)> layerStyles,
        ISldStyleConverter? sldConverter)
    {
        if (layerStyles.Count == 0)
        {
            return new GeoServerLayerStyleDiffResult([], []);
        }

        var diffs = new List<MigrationStyleDiff>();
        var reviewItems = new List<MigrationManifestReviewItem>();

        foreach (var (styleId, style) in layerStyles.OrderBy(static item => item.StyleId, StringComparer.Ordinal))
        {
            if (!string.Equals(style.Format, "sld", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var unsupportedConstructs = new SortedSet<string>(StringComparer.Ordinal);
            var warnings = new List<string>();
            var errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(style.SldContent))
            {
                InspectSldXml(style.SldContent!, unsupportedConstructs, warnings);

                if (sldConverter != null)
                {
                    var conversion = sldConverter.Convert(style.SldContent!);
                    foreach (var warning in conversion.Warnings)
                    {
                        if (!warnings.Contains(warning, StringComparer.Ordinal))
                        {
                            warnings.Add(warning);
                        }

                        ExtractConstructFromDiagnostic(warning, unsupportedConstructs);
                    }

                    foreach (var error in conversion.Errors)
                    {
                        if (!errors.Contains(error, StringComparer.Ordinal))
                        {
                            errors.Add(error);
                        }
                    }
                }
            }

            if (unsupportedConstructs.Count == 0 && warnings.Count == 0 && errors.Count == 0)
            {
                continue;
            }

            var constructArray = unsupportedConstructs.ToArray();
            var warningArray = warnings.ToArray();
            var errorArray = errors.ToArray();

            diffs.Add(new MigrationStyleDiff
            {
                StyleId = styleId,
                StyleName = style.Name,
                Format = style.Format,
                UnsupportedConstructs = constructArray,
                Warnings = warningArray,
                Errors = errorArray
            });

            var constructList = constructArray.Length > 0
                ? string.Join(", ", constructArray)
                : "SLD conversion diagnostics";
            reviewItems.Add(new MigrationManifestReviewItem
            {
                SourceId = layerSourceId,
                Kind = "style-diff",
                Code = StyleDiffReviewCode,
                Severity = "manual-review",
                Reason = $"Style '{style.Name}' contains SLD constructs the importer will not draw cleanly: {constructList}.",
                Warnings = warningArray.Concat(errorArray).ToArray(),
                ManualSteps =
                [
                    $"Review SLD style diff for '{style.Name}' ({constructList}) before applying to production."
                ]
            });
        }

        return new GeoServerLayerStyleDiffResult(diffs.ToArray(), reviewItems.ToArray());
    }

    private static void InspectSldXml(
        string sldContent,
        SortedSet<string> unsupportedConstructs,
        List<string> warnings)
    {
        try
        {
            var document = RemoteXmlDocumentParser.Parse(sldContent, LoadOptions.None);
            var presentElements = document
                .Descendants()
                .Select(static element => element.Name.LocalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (elementName, description) in NonDrawnSldConstructs)
            {
                if (presentElements.Contains(elementName))
                {
                    unsupportedConstructs.Add(elementName);
                    warnings.Add($"[{elementName}] {description}");
                }
            }
        }
        catch (XmlException)
        {
            // Malformed XML diagnostics are reported by ISldStyleConverter when available.
        }
    }

    private static void ExtractConstructFromDiagnostic(string diagnostic, SortedSet<string> unsupportedConstructs)
    {
        if (diagnostic.Length > 2 && diagnostic[0] == '[')
        {
            var closingBracket = diagnostic.IndexOf(']');
            if (closingBracket > 1)
            {
                var construct = diagnostic[1..closingBracket].Trim();
                if (construct.Length > 0)
                {
                    unsupportedConstructs.Add(construct);
                }
            }
        }
    }
}
