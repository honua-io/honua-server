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

    private sealed class StyleDiffAccumulator
    {
        public SortedSet<string> Constructs { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public bool IsEmpty => Constructs.Count == 0 && Warnings.Count == 0 && Errors.Count == 0;
    }

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
            var diff = BuildDiffForStyle(styleId, style, sldConverter);
            if (diff == null)
            {
                continue;
            }

            diffs.Add(diff);
            reviewItems.Add(CreateReviewItem(layerSourceId, diff));
        }

        return new GeoServerLayerStyleDiffResult(diffs.ToArray(), reviewItems.ToArray());
    }

    private static MigrationStyleDiff? BuildDiffForStyle(
        string styleId,
        GeoServerStyleInfo style,
        ISldStyleConverter? sldConverter)
    {
        if (!string.Equals(style.Format, "sld", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(style.SldContent))
        {
            return null;
        }

        var acc = new StyleDiffAccumulator();
        InspectSldXml(style.SldContent!, acc);
        CollectConverterDiagnostics(style.SldContent!, sldConverter, acc);

        if (acc.IsEmpty)
        {
            return null;
        }

        return new MigrationStyleDiff
        {
            StyleId = styleId,
            StyleName = style.Name,
            Format = style.Format,
            UnsupportedConstructs = [.. acc.Constructs],
            Warnings = [.. acc.Warnings],
            Errors = [.. acc.Errors]
        };
    }

    private static void CollectConverterDiagnostics(
        string sldContent,
        ISldStyleConverter? sldConverter,
        StyleDiffAccumulator acc)
    {
        if (sldConverter == null)
        {
            return;
        }

        var conversion = sldConverter.Convert(sldContent);
        foreach (var warning in conversion.Warnings)
        {
            if (!acc.Warnings.Contains(warning))
            {
                acc.Warnings.Add(warning);
            }

            ExtractConstructFromDiagnostic(warning, acc.Constructs);
        }

        foreach (var error in conversion.Errors)
        {
            if (!acc.Errors.Contains(error))
            {
                acc.Errors.Add(error);
            }
        }
    }

    private static MigrationManifestReviewItem CreateReviewItem(string layerSourceId, MigrationStyleDiff diff)
    {
        var constructList = diff.UnsupportedConstructs.Length > 0
            ? string.Join(", ", diff.UnsupportedConstructs)
            : "SLD conversion diagnostics";
        return new MigrationManifestReviewItem
        {
            SourceId = layerSourceId,
            Kind = "style-diff",
            Code = StyleDiffReviewCode,
            Severity = "manual-review",
            Reason = $"Style '{diff.StyleName}' contains SLD constructs the importer will not draw cleanly: {constructList}.",
            Warnings = diff.Warnings.Concat(diff.Errors).ToArray(),
            ManualSteps =
            [
                $"Review SLD style diff for '{diff.StyleName}' ({constructList}) before applying to production."
            ]
        };
    }

    private static void InspectSldXml(string sldContent, StyleDiffAccumulator acc)
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
                    acc.Constructs.Add(elementName);
                    acc.Warnings.Add($"[{elementName}] {description}");
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
