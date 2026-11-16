// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

public static class I3sAttributeSchemaBuilder
{
    public const string ObjectIdFieldKey = "f_0";

    public const string UserFieldKeyPrefix = "f_";

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

    internal static bool IsNumeric(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
