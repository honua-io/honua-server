// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices;

internal static class GeoServicesObjectIdFieldResolver
{
    /// <summary>
    /// Resolves the object-id field name from a Metadata v2 canonical resource. Prefers
    /// fields tagged with the <c>id.primary</c> semantic role, then falls back to the
    /// first integer/big-integer field named <c>objectid</c>, <c>id</c>, or <c>fid</c>
    /// (case-insensitive), and finally to any integer field. When none match, the default
    /// <see cref="FieldNames.ObjectId"/> constant is returned.
    /// </summary>
    public static string ResolveObjectIdFieldName(MetadataV2Resource resource)
        => ResolveObjectIdField(resource)?.Name ?? FieldNames.ObjectId;

    public static MetadataV2Field? ResolveObjectIdField(MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var candidate = FindNumericV2Field(resource, FieldNames.ObjectId)
                        ?? FindNumericV2Field(resource, "id")
                        ?? FindNumericV2Field(resource, "fid");
        if (candidate is not null)
        {
            return candidate;
        }

        var primary = resource.FindPrimaryIdField();
        if (primary is not null && IsObjectIdCompatible(primary))
        {
            return primary;
        }

        return resource.SchemaFields.FirstOrDefault(IsObjectIdCompatible);
    }

    private static MetadataV2Field? FindNumericV2Field(MetadataV2Resource resource, string fieldName)
        => resource.SchemaFields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            IsObjectIdCompatible(field));

    private static bool IsObjectIdCompatible(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger;

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
