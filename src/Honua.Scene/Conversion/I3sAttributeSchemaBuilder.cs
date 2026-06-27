// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

/// <summary>
/// Derives the I3S layer <c>attributeStorageInfo</c> field schema from the
/// projected attributes of a scene's features (#1811). This is the inverse of
/// the value encoder <see cref="I3sAttributeBufferBuilder"/>: it advertises the
/// fields an ArcGIS SceneLayer client can identify, with the per-field binary
/// layout (key, ordering, header, value type) the matching
/// <c>nodes/{id}/attributes/{key}/0</c> file conforms to.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic <c>OBJECTID</c> field (<c>f_0</c>, <c>Oid32</c>) every served
/// 3D Object node carries is always emitted first, matching the merged identify
/// slice. Each distinct user attribute key projected onto the features
/// (<see cref="SceneFeature.Attributes"/> — the same bag the 3D Tiles
/// <c>EXT_structural_metadata</c> property tables are baked from) is then
/// assigned a stable <c>f_{n}</c> key in deterministic ordinal-sorted key order
/// so the schema is byte-stable across runs for identical input.
/// </para>
/// <para>
/// A field's value type is inferred from the projected values: a key whose
/// non-null values are all numeric is advertised as <c>Float64</c>
/// (fixed-width); any other key is advertised as a UTF-8 <c>String</c>
/// (variable-length, with an <c>attributeByteCounts</c> buffer). This mirrors
/// the two value layouts <see cref="I3sAttributeBufferBuilder"/> emits, so a
/// descriptor field and its served attribute file always agree.
/// </para>
/// </remarks>
public static class I3sAttributeSchemaBuilder
{
    /// <summary>Stable key for the synthetic <c>OBJECTID</c> field (always first).</summary>
    public const string ObjectIdFieldKey = "f_0";

    /// <summary>Prefix used for user-attribute field keys (<c>f_1</c>, <c>f_2</c>, …).</summary>
    public const string UserFieldKeyPrefix = "f_";

    /// <summary>
    /// Builds the ordered <c>attributeStorageInfo</c> field list for a feature
    /// set: the synthetic <c>OBJECTID</c> field followed by one typed field per
    /// distinct projected attribute key.
    /// </summary>
    /// <param name="features">
    /// The scene features whose projected <see cref="SceneFeature.Attributes"/>
    /// define the user field set. An empty set yields the <c>OBJECTID</c> field
    /// only.
    /// </param>
    /// <returns>The ordered attribute-storage descriptors.</returns>
    public static IReadOnlyList<I3sAttributeStorageInfo> Build(IReadOnlyList<SceneFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var fields = new List<I3sAttributeStorageInfo> { BuildObjectIdField() };

        // Collect the distinct attribute keys and whether every observed value
        // for each key is numeric, in a single deterministic pass.
        var numericByKey = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            foreach (var (key, value) in feature.Attributes)
            {
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var valueIsNumeric = value is null || IsNumeric(value);
                numericByKey[key] = numericByKey.TryGetValue(key, out var allNumeric)
                    ? allNumeric && valueIsNumeric
                    : valueIsNumeric;
            }
        }

        var index = 1;
        foreach (var (name, allNumeric) in numericByKey)
        {
            var key = UserFieldKeyPrefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields.Add(allNumeric ? BuildNumericField(key, name) : BuildStringField(key, name));
            index++;
        }

        return fields;
    }

    /// <summary>The synthetic <c>OBJECTID</c> (<c>f_0</c>, <c>Oid32</c>) field descriptor.</summary>
    public static I3sAttributeStorageInfo BuildObjectIdField() => new()
    {
        Key = ObjectIdFieldKey,
        Name = "OBJECTID",
        Ordering = ["attributeValues"],
        Header = [new I3sAttributeHeader { Property = "count", ValueType = "UInt32" }],
        AttributeValues = new I3sAttributeValues
        {
            ValueType = I3sAttributeBufferBuilder.Oid32ValueType,
            ValuesPerElement = 1,
        },
    };

    private static I3sAttributeStorageInfo BuildNumericField(string key, string name) => new()
    {
        Key = key,
        Name = name,
        Ordering = ["attributeValues"],
        Header = [new I3sAttributeHeader { Property = "count", ValueType = "UInt32" }],
        AttributeValues = new I3sAttributeValues
        {
            ValueType = I3sAttributeBufferBuilder.Float64ValueType,
            ValuesPerElement = 1,
        },
    };

    private static I3sAttributeStorageInfo BuildStringField(string key, string name) => new()
    {
        Key = key,
        Name = name,
        Ordering = ["attributeByteCounts", "attributeValues"],
        Header =
        [
            new I3sAttributeHeader { Property = "count", ValueType = "UInt32" },
            new I3sAttributeHeader { Property = "attributeValuesByteCount", ValueType = "UInt32" },
        ],
        AttributeByteCounts = new I3sAttributeValues
        {
            ValueType = "UInt32",
            ValuesPerElement = 1,
        },
        AttributeValues = new I3sAttributeValues
        {
            ValueType = I3sAttributeBufferBuilder.StringValueType,
            Encoding = "UTF-8",
            ValuesPerElement = 1,
        },
    };

    /// <summary>
    /// Whether a projected attribute value is one of the numeric CLR types the
    /// feature projection emits (the I3S <c>Float64</c> path narrows them all to
    /// double).
    /// </summary>
    internal static bool IsNumeric(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
