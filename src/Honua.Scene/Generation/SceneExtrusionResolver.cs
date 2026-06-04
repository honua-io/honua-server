// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Single source of truth for resolving extrusion base/top heights from a
/// <see cref="SceneFeature"/> and its <see cref="MetadataV2ExtrusionInfo"/>
/// (#1200/#1207). Both the GLB writer (which extrudes prism vertices) and the
/// publish executor (which derives each tile's bounding-region height extent)
/// MUST resolve heights through this helper so the rendered prism Z and the
/// advertised bounding volume can never drift — a divergence would let CesiumJS
/// cull tiles whose geometry pokes outside the advertised region.
/// </summary>
/// <remarks>
/// Numeric coercion accepts the same attribute value types as the metadata
/// schema producers emit (CLR numerics plus invariant-culture numeric strings,
/// matching the JSONB <c>-&gt;&gt;</c> text projection of the Postgres scene
/// source). Unit conversion normalizes feet / US survey feet to meters via
/// <see cref="MetadataV2VerticalUnits"/>. All parsing/formatting is
/// culture-invariant so output stays byte-stable across locales.
/// </remarks>
internal static class SceneExtrusionResolver
{
    /// <summary>
    /// Resolves the extrusion top height (in meters) for a feature: the
    /// numeric height-field value (or the configured default when absent /
    /// non-numeric), converted from the configured vertical unit to meters.
    /// </summary>
    public static double ResolveTopHeightMeters(SceneFeature feature, MetadataV2ExtrusionInfo extrusion)
    {
        var raw = LookupNumericAttribute(feature, extrusion.HeightField) ?? extrusion.DefaultHeight ?? 0.0;
        return ConvertVerticalToMeters(raw, extrusion.Unit);
    }

    /// <summary>
    /// Resolves the extrusion base height (in meters) for a feature: zero when
    /// no base-height field is configured or the value is absent / non-numeric,
    /// otherwise the numeric base value converted to meters.
    /// </summary>
    public static double ResolveBaseHeightMeters(SceneFeature feature, MetadataV2ExtrusionInfo extrusion)
    {
        if (string.IsNullOrEmpty(extrusion.BaseHeightField))
        {
            return 0.0;
        }

        var raw = LookupNumericAttribute(feature, extrusion.BaseHeightField) ?? 0.0;
        return ConvertVerticalToMeters(raw, extrusion.Unit);
    }

    /// <summary>
    /// Coerces an attribute value to a double, returning null when the
    /// attribute is absent, null, or not a recognized numeric form.
    /// </summary>
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

    /// <summary>
    /// Converts a vertical measurement to meters using the canonical Metadata v2
    /// vertical-unit tokens (null/whitespace/meters are pass-through).
    /// </summary>
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
