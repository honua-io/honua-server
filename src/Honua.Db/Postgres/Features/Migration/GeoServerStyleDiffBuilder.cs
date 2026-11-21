// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed record GeoServerStyleDiffBuildResult(
    IReadOnlyDictionary<string, MigrationLayerStyleDiff[]> LayerStyleDiffsBySourceId,
    MigrationManifestReviewItem[] ReviewItems);

internal static class GeoServerStyleDiffBuilder
{
    private const string ReviewCode = "geoserver.style.diff";

    private readonly record struct StyleInspectionTarget(
        string StyleId,
        string StyleName,
        string SldContent);

    public static GeoServerStyleDiffBuildResult Build(
        GeoServerServiceInfo serviceInfo,
        ISldStyleConverter? sldConverter)
    {
        var styleDiffsByStyleId = InspectAllStyles(serviceInfo.Styles, sldConverter);
        if (styleDiffsByStyleId.Count == 0)
        {
            return new GeoServerStyleDiffBuildResult(
                new Dictionary<string, MigrationLayerStyleDiff[]>(StringComparer.Ordinal),
                []);
        }

        var stylesByKey = serviceInfo.Styles
            .GroupBy(static s => (Workspace: s.WorkspaceName ?? string.Empty, Name: s.Name), StyleKeyComparer.Instance)
            .ToDictionary(static g => g.Key, static g => g.Select(BuildStyleId).ToArray(), StyleKeyComparer.Instance);

        return MapDiffsToLayers(serviceInfo.Layers, stylesByKey, styleDiffsByStyleId);
    }

    private static Dictionary<string, MigrationLayerStyleDiff[]> InspectAllStyles(
        GeoServerStyleInfo[] styles,
        ISldStyleConverter? sldConverter)
    {
        var result = new Dictionary<string, MigrationLayerStyleDiff[]>(StringComparer.Ordinal);
        foreach (var style in styles)
        {
            if (!string.Equals(style.Format, "sld", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(style.SldContent))
            {
                continue;
            }

            var styleId = BuildStyleId(style);
            var target = new StyleInspectionTarget(styleId, style.Name, style.SldContent);
            var diffs = InspectStyle(target, sldConverter);
            if (diffs.Length > 0)
            {
                result[styleId] = diffs;
            }
        }

        return result;
    }

    private static GeoServerStyleDiffBuildResult MapDiffsToLayers(
        GeoServerLayerInfo[] layers,
        Dictionary<(string Workspace, string Name), string[]> stylesByKey,
        Dictionary<string, MigrationLayerStyleDiff[]> styleDiffsByStyleId)
    {
        var layerDiffs = new Dictionary<string, MigrationLayerStyleDiff[]>(StringComparer.Ordinal);
        var reviewItems = new List<MigrationManifestReviewItem>();

        foreach (var layer in layers.OrderBy(static l => $"layer:{l.WorkspaceName}:{l.Name}", StringComparer.Ordinal))
        {
            var layerSourceId = $"layer:{layer.WorkspaceName}:{layer.Name}";
            var matchedDiffs = CollectLayerDiffs(layer, stylesByKey, styleDiffsByStyleId);
            if (matchedDiffs.Length == 0)
            {
                continue;
            }

            layerDiffs[layerSourceId] = matchedDiffs;
            reviewItems.Add(BuildLayerStyleDiffReviewItem(layerSourceId, matchedDiffs));
        }

        return new GeoServerStyleDiffBuildResult(layerDiffs, reviewItems.ToArray());
    }

    private static MigrationLayerStyleDiff[] InspectStyle(
        StyleInspectionTarget target,
        ISldStyleConverter? sldConverter)
    {
        var diffs = new List<MigrationLayerStyleDiff>();
        var seenConstructs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (sldConverter != null)
        {
            AddConverterDiagnostics(target, sldConverter.Convert(target.SldContent), seenConstructs, diffs);
        }

        AddXmlConstructDiffs(target, seenConstructs, diffs);

        return diffs
            .OrderBy(static d => d.Construct, StringComparer.Ordinal)
            .ThenBy(static d => d.Severity, StringComparer.Ordinal)
            .ThenBy(static d => d.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddConverterDiagnostics(
        StyleInspectionTarget target,
        SldStyleConversionResult conversion,
        HashSet<string> seenConstructs,
        List<MigrationLayerStyleDiff> diffs)
    {
        AppendDiagnostics(target, conversion.Warnings, "warning", seenConstructs, diffs);
        AppendDiagnostics(target, conversion.Errors, "error", seenConstructs, diffs);
    }

    private static void AppendDiagnostics(
        StyleInspectionTarget target,
        IReadOnlyList<string> messages,
        string severity,
        HashSet<string> seenConstructs,
        List<MigrationLayerStyleDiff> diffs)
    {
        foreach (var raw in messages)
        {
            var (construct, message) = ParseDiagnostic(raw);
            seenConstructs.Add(construct);
            diffs.Add(new MigrationLayerStyleDiff
            {
                StyleId = target.StyleId,
                StyleName = target.StyleName,
                Construct = construct,
                Severity = severity,
                Message = message
            });
        }
    }

    private static void AddXmlConstructDiffs(
        StyleInspectionTarget target,
        HashSet<string> seenConstructs,
        List<MigrationLayerStyleDiff> diffs)
    {
        XDocument doc;
        try
        {
            doc = RemoteXmlDocumentParser.Parse(target.SldContent);
        }
        catch (Exception)
        {
            return;
        }

        if (!seenConstructs.Contains("ElseFilter") && HasElement(doc, "ElseFilter"))
        {
            diffs.Add(new MigrationLayerStyleDiff
            {
                StyleId = target.StyleId,
                StyleName = target.StyleName,
                Construct = "ElseFilter",
                Severity = "warning",
                Message = "ElseFilter has no portable MapLibre equivalent; rule will render unfiltered."
            });
        }

        if (!seenConstructs.Contains("TextSymbolizer") && HasElement(doc, "TextSymbolizer"))
        {
            diffs.Add(new MigrationLayerStyleDiff
            {
                StyleId = target.StyleId,
                StyleName = target.StyleName,
                Construct = "TextSymbolizer",
                Severity = "warning",
                Message = "TextSymbolizer labels are not drawn by the server-side raster renderer."
            });
        }
    }

    private static bool HasElement(XDocument doc, string localName)
        => doc.Descendants().Any(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    private static MigrationLayerStyleDiff[] CollectLayerDiffs(
        GeoServerLayerInfo layer,
        Dictionary<(string Workspace, string Name), string[]> stylesByKey,
        Dictionary<string, MigrationLayerStyleDiff[]> styleDiffsByStyleId)
    {
        var styleRefs = EnumerateLayerStyleReferences(layer);
        var resolvedIds = styleRefs
            .SelectMany(styleRef => ResolveStyleIds(stylesByKey, layer.WorkspaceName, styleRef))
            .Distinct(StringComparer.Ordinal);

        var collected = new List<MigrationLayerStyleDiff>();
        foreach (var styleId in resolvedIds)
        {
            if (styleDiffsByStyleId.TryGetValue(styleId, out var diffs))
            {
                collected.AddRange(diffs);
            }
        }

        return collected.ToArray();
    }

    private static IEnumerable<string> EnumerateLayerStyleReferences(GeoServerLayerInfo layer)
    {
        if (!string.IsNullOrWhiteSpace(layer.DefaultStyle))
        {
            yield return layer.DefaultStyle;
        }

        foreach (var alt in layer.AlternativeStyles)
        {
            if (!string.IsNullOrWhiteSpace(alt))
            {
                yield return alt;
            }
        }
    }

    private static MigrationManifestReviewItem BuildLayerStyleDiffReviewItem(
        string layerSourceId,
        MigrationLayerStyleDiff[] diffs)
    {
        var constructs = diffs
            .Select(static d => d.Construct)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToArray();
        var warnings = diffs
            .Select(static d => $"[{d.Construct}] {d.StyleName}: {d.Message}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new MigrationManifestReviewItem
        {
            SourceId = layerSourceId,
            Kind = "layer-style-diff",
            Code = ReviewCode,
            Severity = "manual-review",
            Reason = $"Layer style contains SLD constructs that will not draw identically: {string.Join(", ", constructs)}.",
            ManualSteps = ["Review the per-layer styleDiffs entries before applying the style in production."],
            Warnings = warnings
        };
    }

    private static (string Construct, string Message) ParseDiagnostic(string raw)
    {
        if (raw.StartsWith('[') && raw.IndexOf(']') is > 1 and var closeIndex)
        {
            var construct = raw[1..closeIndex].Trim();
            var message = raw[(closeIndex + 1)..].Trim();
            return (construct, message.Length > 0 ? message : raw);
        }

        return ("Sld", raw);
    }

    private static string[] ResolveStyleIds(
        Dictionary<(string Workspace, string Name), string[]> stylesByKey,
        string layerWorkspaceName,
        string styleReference)
    {
        var separatorIndex = styleReference.IndexOf(':');
        if (separatorIndex > 0 && separatorIndex < styleReference.Length - 1)
        {
            var qualified = (styleReference[..separatorIndex], styleReference[(separatorIndex + 1)..]);
            return stylesByKey.TryGetValue(qualified, out var qualifiedIds) ? qualifiedIds : [];
        }

        if (stylesByKey.TryGetValue((layerWorkspaceName, styleReference), out var workspaceIds))
        {
            return workspaceIds;
        }

        return stylesByKey.TryGetValue((string.Empty, styleReference), out var globalIds) ? globalIds : [];
    }

    private static string BuildStyleId(GeoServerStyleInfo style)
        => $"style:{(string.IsNullOrWhiteSpace(style.WorkspaceName) ? "global" : style.WorkspaceName)}:{style.Name}";

    private sealed class StyleKeyComparer : IEqualityComparer<(string Workspace, string Name)>
    {
        public static StyleKeyComparer Instance { get; } = new();

        public bool Equals((string Workspace, string Name) x, (string Workspace, string Name) y)
            => string.Equals(x.Workspace, y.Workspace, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Workspace, string Name) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Workspace),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
