// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Db.Postgres.Features.Migration;

internal sealed partial class GeoServerImportService
{
    private sealed record LayerPublishBuildContext
    {
        public required MigrationApplyPlanStep Step { get; init; }
        public required GeoServerLayerInfo Layer { get; init; }
        public required string Schema { get; init; }
        public required string Table { get; init; }
        public required GeoServerSecurityRule[] SecurityRules { get; init; }
        public required GeoServerImportRequest Request { get; init; }
    }

    private sealed record StepEnrichmentContext
    {
        public required Dictionary<string, GeoServerLayerInfo> LayersById { get; init; }
        public required Dictionary<string, GeoServerStyleInfo> StylesById { get; init; }
        public required Dictionary<StyleReferenceKey, string[]> StyleIdsByReference { get; init; }
        public required GeoServerSecurityRule[] SecurityRules { get; init; }
        public required List<MigrationManifestReviewItem> ExtraReview { get; init; }
    }

    private MigrationApplyPlanArtifact EnrichApplyPlan(
        MigrationApplyPlanArtifact plan,
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources)
    {
        var extraReview = new List<MigrationManifestReviewItem>();
        var context = BuildStepEnrichmentContext(serviceInfo, filteredResources, extraReview);
        var updatedSteps = new MigrationApplyPlanStep[plan.Steps.Length];
        var anyChanged = false;
        for (var i = 0; i < plan.Steps.Length; i++)
        {
            (updatedSteps[i], var changed) = EnrichLayerStep(plan.Steps[i], context);
            anyChanged |= changed;
        }

        extraReview.AddRange(GeoServerSecurityRuleTranslator.CollectUnmatchedUnexpressedRules(serviceInfo.SecurityRules, filteredResources.Layers));
        if (!anyChanged && extraReview.Count == 0)
        {
            return plan;
        }

        return plan with
        {
            Summary = plan.Summary with
            {
                ReadyStepCount = updatedSteps.Count(static s => string.Equals(s.Disposition, "ready", StringComparison.Ordinal)),
                ManualReviewStepCount = updatedSteps.Count(static s => string.Equals(s.Disposition, "manual-review", StringComparison.Ordinal))
            },
            Steps = updatedSteps,
            ManualReviewItems = plan.ManualReviewItems
                .Concat(extraReview)
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static StepEnrichmentContext BuildStepEnrichmentContext(
        GeoServerServiceInfo serviceInfo,
        FilteredResources filteredResources,
        List<MigrationManifestReviewItem> extraReview)
        => new()
        {
            LayersById = filteredResources.Layers.ToDictionary(GetLayerId, StringComparer.Ordinal),
            StylesById = filteredResources.Styles.ToDictionary(GetStyleId, StringComparer.Ordinal),
            StyleIdsByReference = BuildStyleReferenceLookup(filteredResources.Styles),
            SecurityRules = serviceInfo.SecurityRules,
            ExtraReview = extraReview
        };

    private (MigrationApplyPlanStep Step, bool Changed) EnrichLayerStep(
        MigrationApplyPlanStep step,
        StepEnrichmentContext context)
    {
        if (!string.Equals(step.Kind, "layer", StringComparison.Ordinal) ||
            !context.LayersById.TryGetValue(step.SourceId, out var layer))
        {
            return (step, false);
        }

        var layerStyles = ResolveLayerStyles(layer, context.StylesById, context.StyleIdsByReference);
        var styleDiff = GeoServerStyleDiffBuilder.BuildForLayer(step.SourceId, layerStyles, _sldConverter);
        var security = GeoServerSecurityRuleTranslator.TranslateForLayer(layer, context.SecurityRules, step.SourceId);
        if (styleDiff.Diffs.Length == 0 && security.Dispositions.Length == 0)
        {
            return (step, false);
        }

        context.ExtraReview.AddRange(styleDiff.ReviewItems);
        context.ExtraReview.AddRange(security.ReviewItems);
        var reviewCodes = step.ReviewCodes
            .Concat(styleDiff.ReviewItems.Select(static item => item.Code))
            .Concat(security.ReviewItems.Select(static item => item.Code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();

        return (step with
        {
            Disposition = security.RequiresManualReview ? "manual-review" : step.Disposition,
            Action = security.RequiresManualReview ? "manual-review" : step.Action,
            ReviewCodes = reviewCodes,
            StyleDiffs = styleDiff.Diffs.Length > 0 ? styleDiff.Diffs : null,
            SecurityRules = security.Dispositions.Length > 0 ? security.Dispositions : null
        }, true);
    }

    private static List<(string StyleId, GeoServerStyleInfo Style)> ResolveLayerStyles(
        GeoServerLayerInfo layer,
        Dictionary<string, GeoServerStyleInfo> stylesById,
        Dictionary<StyleReferenceKey, string[]> styleIdsByReference)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<(string StyleId, GeoServerStyleInfo Style)>();

        foreach (var styleReference in layer.AlternativeStyles.Prepend(layer.DefaultStyle))
        {
            foreach (var styleId in ResolveStyleIds(styleIdsByReference, layer.WorkspaceName, styleReference))
            {
                if (seen.Add(styleId) && stylesById.TryGetValue(styleId, out var style))
                {
                    resolved.Add((styleId, style));
                }
            }
        }

        return resolved;
    }

    private static LayerPublishRequest BuildLayerPublishRequest(LayerPublishBuildContext ctx)
    {
        var temporalInfo = BuildLayerTemporalInfo(ctx.Layer);
        var security = GeoServerSecurityRuleTranslator.TranslateForLayer(ctx.Layer, ctx.SecurityRules, ctx.Step.SourceId);
        return new LayerPublishRequest
        {
            Schema = ctx.Schema,
            Table = ctx.Table,
            LayerName = ctx.Step.TargetResourceName ?? ctx.Layer.Name,
            Description = ctx.Layer.Abstract,
            Srid = ctx.Request.TargetSrid ?? ResolveSrid(ctx.Layer.SRS),
            TemporalColumn = temporalInfo?.StartTimeField,
            Temporal = temporalInfo,
            AccessPolicy = security.AccessPolicy,
            Extensions = BuildLayerDimensionExtensions(ctx.Layer),
            Fields = [],
            ServiceName = ctx.Step.TargetServiceName,
            Enabled = ctx.Request.AutoPublishLayers && ctx.Layer.Enabled
        };
    }

    private static MetadataV2ObjectTemporal? BuildLayerTemporalInfo(GeoServerLayerInfo layer)
    {
        if (layer.TimeDimension is not { Enabled: true } timeDim || string.IsNullOrWhiteSpace(timeDim.Attribute))
        {
            return null;
        }

        return new MetadataV2ObjectTemporal
        {
            StartTimeField = timeDim.Attribute.Trim(),
            EndTimeField = string.IsNullOrWhiteSpace(timeDim.EndAttribute) ? null : timeDim.EndAttribute.Trim()
        };
    }

    private static Dictionary<string, JsonElement>? BuildLayerDimensionExtensions(GeoServerLayerInfo layer)
    {
        if (layer.ElevationDimension is not { Enabled: true } elevDim)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElevationDimensionExtension(writer, elevDim);
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["geoserverDimensions"] = doc.RootElement.Clone() };
    }

    private static void WriteElevationDimensionExtension(Utf8JsonWriter writer, GeoServerDimensionInfo elevDim)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("elevation");
        if (!string.IsNullOrWhiteSpace(elevDim.Attribute))
        {
            writer.WriteString("attribute", elevDim.Attribute);
        }

        if (!string.IsNullOrWhiteSpace(elevDim.EndAttribute))
        {
            writer.WriteString("endAttribute", elevDim.EndAttribute);
        }

        writer.WriteString("units", string.IsNullOrWhiteSpace(elevDim.Units) ? "EPSG:5030" : elevDim.Units);
        if (!string.IsNullOrWhiteSpace(elevDim.UnitSymbol))
        {
            writer.WriteString("unitSymbol", elevDim.UnitSymbol);
        }

        writer.WriteBoolean("nearestMatchEnabled", elevDim.NearestMatchEnabled);
        if (!string.IsNullOrWhiteSpace(elevDim.DefaultValueReference))
        {
            writer.WriteString("defaultValue", elevDim.DefaultValueReference);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
