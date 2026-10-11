// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    internal readonly record struct AppendRequestOptions(
        bool RollbackOnFailure,
        bool Upsert,
        string? UpsertMatchingField);

    private static readonly string[] UnsupportedAppendControlNames =
    [
        "fieldMappings",
        "appendUploadId",
        "appendItemId",
        "skipUpdates",
        "useGlobalIds"
    ];

    internal static bool TryReadAppendOptions(
        IReadOnlyDictionary<string, StringValues> values,
        out AppendRequestOptions options,
        out string? error)
    {
        options = default;
        if (!TryParseBoolValue(values, "rollbackOnFailure", true, out var rollbackOnFailure, out error) ||
            !TryParseBoolValue(values, "upsert", false, out var upsert, out error))
        {
            return false;
        }

        var matchingField = GetValueString(values, "upsertMatchingField");
        options = new AppendRequestOptions(
            rollbackOnFailure,
            upsert,
            string.IsNullOrWhiteSpace(matchingField) ? null : matchingField);
        return true;
    }

    internal static string? FindUnsupportedAppendControl(IReadOnlyDictionary<string, StringValues> values)
    {
        foreach (var pair in values)
        {
            string? canonical = null;
            foreach (var name in UnsupportedAppendControlNames)
            {
                if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = name;
                    break;
                }
            }

            if (canonical is null || StringValues.IsNullOrEmpty(pair.Value))
            {
                continue;
            }

            var text = pair.Value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (canonical is "skipUpdates" or "useGlobalIds" && IsExplicitAppendFalse(text))
            {
                continue;
            }

            return canonical;
        }

        return null;
    }

    internal static bool IsAppendMatchField(MetadataV2Resource resource, string fieldName)
        => resource.SchemaFields.Any(field => field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

    internal static async Task<(Dictionary<string, long>? Matches, string? Error)> LoadAppendUpsertMatchesAsync(
        IFeatureReader featureReader,
        IFilterExpressionService filterService,
        MetadataV2Resource resource,
        int storageLayerId,
        IReadOnlyList<GeoServicesFeature> features,
        string matchingField,
        CancellationToken cancellationToken)
    {
        var field = resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(matchingField, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return (null, $"Field '{matchingField}' does not exist on the layer.");
        }

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (!TryGetAppendAttribute(feature.Attributes, field.Name, out var raw))
            {
                continue;
            }

            var key = NormalizeAppendMatchKey(raw, field.Type);
            if (key is null || !seen.Add(key))
            {
                continue;
            }

            keys.Add(key);
        }

        var matches = new Dictionary<string, long>(StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return (matches, null);
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        const int batchSize = 500;
        for (var offset = 0; offset < keys.Count; offset += batchSize)
        {
            var count = Math.Min(batchSize, keys.Count - offset);
            var literals = new List<FilterExpression>(count);
            for (var index = 0; index < count; index++)
            {
                if (TryCreateAppendMatchLiteral(field.Type, keys[offset + index], out var literal))
                {
                    literals.Add(literal);
                }
            }

            if (literals.Count == 0)
            {
                continue;
            }

            var translation = filterService.Translate(
                new BinaryExpression(
                    new PropertyReference(field.Name),
                    BinaryOperator.In,
                    new ValueList(literals)),
                resource);
            if (!translation.IsSuccess || translation.SqlFilter is null)
            {
                return (null, translation.ErrorMessage ?? "upsertMatchingField could not be queried.");
            }

            var query = new FeatureQuery { SqlFilter = translation.SqlFilter };
            var pageOffset = 0;
            while (true)
            {
                var page = await featureReader.QueryAsync(
                    storageLayerId,
                    pageOffset == 0 ? query : query with { Offset = pageOffset },
                    cancellationToken).ConfigureAwait(false);
                if (page.Items.IsDefaultOrEmpty)
                {
                    break;
                }

                foreach (var stored in page.Items)
                {
                    if (!TryGetAppendAttribute(stored.Attributes, field.Name, out var storedRaw))
                    {
                        continue;
                    }

                    var storedKey = NormalizeAppendMatchKey(storedRaw, field.Type);
                    if (storedKey is null)
                    {
                        continue;
                    }

                    var objectId = GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(stored, objectIdFieldName);
                    if (matches.TryGetValue(storedKey, out var existing) && existing != objectId)
                    {
                        return (null, $"upsertMatchingField '{field.Name}' matches more than one feature.");
                    }

                    matches[storedKey] = objectId;
                }

                if (!page.HasMoreResults)
                {
                    break;
                }

                var nextOffset = pageOffset + page.Items.Length;
                if (nextOffset <= pageOffset)
                {
                    break;
                }

                pageOffset = nextOffset;
            }
        }

        return (matches, null);
    }

    internal static (GeoServicesFeature[] Adds, GeoServicesFeature[] Updates) PartitionAppendFeatures(
        IReadOnlyList<GeoServicesFeature> features,
        IReadOnlyDictionary<string, long> existingObjectIdByKey,
        string matchingField,
        string objectIdFieldName,
        MetadataV2FieldType matchingFieldType = MetadataV2FieldType.String)
    {
        var adds = new List<GeoServicesFeature>();
        var updates = new List<GeoServicesFeature>();
        foreach (var feature in features)
        {
            if (!TryGetAppendAttribute(feature.Attributes, matchingField, out var raw))
            {
                adds.Add(feature);
                continue;
            }

            var key = NormalizeAppendMatchKey(raw, matchingFieldType);
            if (key is null || !existingObjectIdByKey.TryGetValue(key, out var objectId))
            {
                adds.Add(feature);
                continue;
            }

            updates.Add(StampAppendObjectId(feature, objectIdFieldName, objectId));
        }

        return (adds.ToArray(), updates.ToArray());
    }

    internal static ApplyEditsRequest CreateAppendApplyEditsRequest(
        GeoServicesFeature[] adds,
        GeoServicesFeature[] updates,
        bool rollbackOnFailure)
        => new()
        {
            Adds = updates.Length == 0 || adds.Length > 0 ? adds : null,
            Updates = updates.Length > 0 ? updates : null,
            RollbackOnFailure = rollbackOnFailure,
            RollbackOnFailureExplicitlySet = true
        };

    private static GeoServicesFeature StampAppendObjectId(
        GeoServicesFeature feature,
        string objectIdFieldName,
        long objectId)
    {
        var attributes = new Dictionary<string, object?>(feature.Attributes, StringComparer.Ordinal);
        string? existingKey = null;
        foreach (var key in attributes.Keys)
        {
            if (string.Equals(key, objectIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = key;
                break;
            }
        }

        if (existingKey is not null && !string.Equals(existingKey, objectIdFieldName, StringComparison.Ordinal))
        {
            attributes.Remove(existingKey);
        }

        attributes[objectIdFieldName] = objectId;
        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = feature.Geometry,
            Centroid = feature.Centroid,
            IncludeGeometry = feature.IncludeGeometry,
            ClearGeometry = feature.ClearGeometry,
            ReplaceAttributes = feature.ReplaceAttributes,
            InheritedNonEditableAttributes = feature.InheritedNonEditableAttributes
        };
    }

    private static bool TryGetAppendAttribute(
        IReadOnlyDictionary<string, object?> attributes,
        string fieldName,
        out object? value)
    {
        if (attributes.TryGetValue(fieldName, out value))
        {
            return true;
        }

        foreach (var pair in attributes)
        {
            if (string.Equals(pair.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? NormalizeAppendMatchKey(object? value, MetadataV2FieldType fieldType)
    {
        if (value is null)
        {
            return null;
        }

        if (fieldType is MetadataV2FieldType.DateTime or MetadataV2FieldType.Date)
        {
            return GeoServicesFieldConventions.TryConvertTemporalValue(value, fieldType, out var converted) &&
                converted is not null
                ? Convert.ToString(converted, CultureInfo.InvariantCulture)
                : null;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => element.TryGetInt64(out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : element.GetRawText(),
                _ => element.GetRawText()
            };
        }

        return value switch
        {
            string text => text,
            bool flag => flag ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long =>
                Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ulong unsigned when unsigned <= long.MaxValue =>
                unsigned.ToString(CultureInfo.InvariantCulture),
            float number when float.IsFinite(number) => number.ToString("G9", CultureInfo.InvariantCulture),
            double number when double.IsFinite(number) => number.ToString("G17", CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static bool TryCreateAppendMatchLiteral(MetadataV2FieldType type, string key, out Literal literal)
    {
        switch (type)
        {
            case MetadataV2FieldType.Integer or MetadataV2FieldType.BigInteger:
                if (!long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    literal = null!;
                    return false;
                }

                literal = new Literal(integer, LiteralType.Number);
                return true;
            case MetadataV2FieldType.Float or MetadataV2FieldType.Double:
                if (!double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number))
                {
                    literal = null!;
                    return false;
                }

                literal = new Literal(number, LiteralType.Number);
                return true;
            case MetadataV2FieldType.Boolean:
                if (!bool.TryParse(key, out var flag))
                {
                    literal = null!;
                    return false;
                }

                literal = new Literal(flag, LiteralType.Boolean);
                return true;
            case MetadataV2FieldType.DateTime:
                if (!long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
                {
                    literal = null!;
                    return false;
                }

                try
                {
                    literal = new Literal(DateTimeOffset.FromUnixTimeMilliseconds(epoch), LiteralType.DateTime);
                }
                catch (ArgumentOutOfRangeException)
                {
                    literal = null!;
                    return false;
                }

                return true;
            case MetadataV2FieldType.Date:
                if (!DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    literal = null!;
                    return false;
                }

                literal = new Literal(date, LiteralType.Date);
                return true;
            default:
                literal = new Literal(key, LiteralType.Text);
                return true;
        }
    }

    private static bool IsExplicitAppendFalse(string text)
    {
        if (bool.TryParse(text, out var flag))
        {
            return !flag;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric == 0;
    }
}
