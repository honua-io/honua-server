// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Projects a stored attribute value into the shape the GeoServices wire format allows.
/// </summary>
/// <remarks>
/// <para>
/// GeoServices has no array or object field type. A column whose values are neither -
/// a PostgreSQL <c>text[]</c> or <c>int[]</c>, say - is published as
/// <c>esriFieldTypeString</c>, because that is the only declaration available. The value
/// has to agree with that declaration: emitting <c>["red","blue"]</c> for a field the
/// layer describes as a string is a contract violation, and clients are entitled to
/// reject it.
/// </para>
/// <para>
/// ArcGIS Pro's reaction is the reason this matters. An <c>arcpy.da</c> cursor that
/// selects such a field returns <b>zero rows and raises nothing</b> - the layer looks
/// empty rather than malformed, and a cursor over any other field returns every row. See
/// honua-server#5171.
/// </para>
/// <para>
/// This deliberately stays inside the GeoServices protocol. OGC API Features publishes
/// the same columns as real JSON arrays and must keep doing so - the r-sf certification
/// lane asserts that arrays are <i>not</i> stringified there, while asserting that
/// WFS/GML does stringify them. So the coercion belongs to the protocols that cannot
/// express an array, not to the shared reader.
/// </para>
/// </remarks>
internal static class GeoServicesAttributeProjection
{
    /// <summary>
    /// Normalizes <paramref name="value"/> for the GeoServices wire format: booleans
    /// become small integers, and arrays and objects become their raw JSON text.
    /// </summary>
    /// <remarks>
    /// The raw text is used rather than a joined or flattened rendering because it is
    /// lossless and round-trippable, and because it is already what a WMS GetFeatureInfo
    /// response produces for the same value through <c>FormatFeatureInfoValue</c>. The
    /// two protocols therefore agree on what the field contains.
    /// </remarks>
    public static object? ToEsriValue(object? value)
        => FeatureAttributeValueNormalizer.Normalize(value) switch
        {
            JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object } element
                => element.GetRawText(),
            var normalized => normalized,
        };
}
