// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

internal static class SceneExtrusionResolver
{
    public static double ResolveTopHeightMeters(SceneFeature feature, MetadataV2ExtrusionInfo extrusion)
    {
        var raw = LookupNumericAttribute(feature, extrusion.HeightField) ?? extrusion.DefaultHeight ?? 0.0;
        return ConvertVerticalToMeters(raw, extrusion.Unit);
    }

    public static double ResolveBaseHeightMeters(SceneFeature feature, MetadataV2ExtrusionInfo extrusion)
    {
        if (string.IsNullOrEmpty(extrusion.BaseHeightField))
        {
            return 0.0;
        }

        var raw = LookupNumericAttribute(feature, extrusion.BaseHeightField) ?? 0.0;
        return ConvertVerticalToMeters(raw, extrusion.Unit);
    }

    public static double? LookupNumericAttribute(SceneFeature feature, string fieldName)
    {
        if (!feature.Attributes.TryGetValue(fieldName, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
            _ => null
        };
    }

    public static double ConvertVerticalToMeters(double value, string? unit)
    {
        MetadataV2VerticalUnits.TryNormalize(unit, out var normalized);
        return normalized switch
        {
            MetadataV2VerticalUnits.Feet => value * 0.3048,
            MetadataV2VerticalUnits.UsSurveyFeet => value * (1200.0 / 3937.0),
            _ => value
        };
    }
}
