// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal sealed class OverlaySplitExecutor(
    IOptionsMonitor<GeoprocessingExecutorOptions> options)
    : FeatureCollectionTransformExecutor(options)
{
    internal const string HandledProcessId = "overlay.split";

    private const string SplitTargetAttribute = "SPLIT_TARGET";

    protected override string ProcessId => HandledProcessId;

    protected override List<IFeature> Apply(
        FeatureCollection source,
        StepInputReader inputs,
        CancellationToken cancellationToken)
    {
        var splitField = inputs.GetOrDefault("splitField", string.Empty);
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;

        if (inputs.TryGet("split", out _))
        {
            var splitLayer = OverlayExecutorSupport.ReadLayer(inputs, "split", maxBytes);
            return SplitByFeatures(source, splitLayer, splitField, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(splitField))
        {
            throw new TransformInputException(
                "overlay.split requires either a 'split' layer or a 'splitField' attribute");
        }

        return SplitByField(source, splitField, cancellationToken);
    }

    private static List<IFeature> SplitByFeatures(
        FeatureCollection source,
        FeatureCollection splitLayer,
        string splitField,
        CancellationToken cancellationToken)
    {
        var output = new List<IFeature>();
        for (var zoneIndex = 0; zoneIndex < splitLayer.Count; zoneIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zone = splitLayer[zoneIndex];
            var zoneGeometry = zone.Geometry;
            if (zoneGeometry is null || zoneGeometry.IsEmpty)
            {
                continue;
            }

            var zoneKey = ResolveZoneKey(zone, splitField, zoneIndex);
            foreach (var feature in source)
            {
                var geometry = feature.Geometry;
                if (geometry is null || geometry.IsEmpty || !geometry.Intersects(zoneGeometry))
                {
                    continue;
                }

                var clipped = geometry.Intersection(zoneGeometry);
                if (clipped is null || clipped.IsEmpty)
                {
                    continue;
                }

                var attributes = OverlayExecutorSupport.CopyAttributes(feature);
                OverlayExecutorSupport.Upsert(attributes, SplitTargetAttribute, zoneKey);
                output.Add(new Feature(clipped, attributes));
            }
        }

        return output;
    }

    private static List<IFeature> SplitByField(
        FeatureCollection source,
        string splitField,
        CancellationToken cancellationToken)
    {
        var output = new List<IFeature>(source.Count);
        foreach (var feature in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = feature.Attributes is not null && feature.Attributes.Exists(splitField)
                ? feature.Attributes.GetOptionalValue(splitField)
                : null;

            var attributes = OverlayExecutorSupport.CopyAttributes(feature);
            OverlayExecutorSupport.Upsert(attributes, SplitTargetAttribute, ToKey(value));
            output.Add(new Feature(feature.Geometry, attributes));
        }

        return output;
    }

    private static string ResolveZoneKey(IFeature zone, string splitField, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(splitField)
            && zone.Attributes is not null
            && zone.Attributes.Exists(splitField))
        {
            return ToKey(zone.Attributes.GetOptionalValue(splitField));
        }

        return ordinal.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToKey(object? value)
        => value is null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}
