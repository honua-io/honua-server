// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.GeoServices;

internal static class GeoServicesObjectIdFieldResolver
{
    public static string ResolveObjectIdFieldName(LayerDefinition layer)
        => ResolveObjectIdField(layer)?.Name ?? FieldNames.ObjectId;

    public static string ResolveServiceObjectIdFieldName(ServiceDefinition service)
    {
        var candidates = service.Layers
            .Select(ResolveObjectIdFieldName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : FieldNames.ObjectId;
    }

    public static string? ResolveObjectIdAttributeName(LayerDefinition layer)
        => ResolveObjectIdField(layer)?.Name;

    public static FieldDefinition? ResolveObjectIdField(LayerDefinition layer)
        => FindNumericField(layer, FieldNames.ObjectId)
           ?? FindNumericField(layer, "id")
           ?? FindNumericField(layer, "fid")
           ?? layer.Fields.FirstOrDefault(IsObjectIdCompatible);

    public static long ResolveObjectIdValue(Feature feature, string objectIdFieldName)
    {
        if (!string.IsNullOrWhiteSpace(objectIdFieldName) &&
            feature.Attributes.TryGetValue(objectIdFieldName, out var value) &&
            TryNormalizeObjectId(value, out var objectId))
        {
            return objectId;
        }

        return feature.Id;
    }

    private static FieldDefinition? FindNumericField(LayerDefinition layer, string fieldName)
        => layer.Fields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            IsObjectIdCompatible(field));

    private static bool IsObjectIdCompatible(FieldDefinition field)
        => !field.IsGeometry && field.Type is FieldType.Integer or FieldType.BigInteger;

    private static bool TryNormalizeObjectId(object? value, out long objectId)
    {
        objectId = 0;
        return value switch
        {
            long number => SetObjectId(number, out objectId),
            int number => SetObjectId(number, out objectId),
            short number => SetObjectId(number, out objectId),
            byte number => SetObjectId(number, out objectId),
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var number) =>
                SetObjectId(number, out objectId),
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) =>
                SetObjectId(number, out objectId),
            _ => false
        };
    }

    private static bool SetObjectId(long value, out long objectId)
    {
        objectId = value;
        return true;
    }
}
