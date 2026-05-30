// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Caching;

namespace Honua.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Computes canonical entity tags for OGC API Features resources.
/// </summary>
internal static class OgcFeatureEntityTag
{
    private const string RepresentationTagPrefix = "of-";

    public static string Compute(Feature feature, IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(etagService);

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = feature.Id,
            ["geometry"] = feature.Geometry is { Length: > 0 }
                ? Convert.ToBase64String(feature.Geometry)
                : null,
            ["attributes"] = NormalizeForEtag(feature.Attributes)
        };

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonicalJson(writer, payload);
        }

        return etagService.ComputeETag(buffer.WrittenSpan);
    }

    public static string ComputeRepresentation(byte[] payload, string entityETag, IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(etagService);

        var entityToken = NormalizeTagToken(entityETag);
        var payloadToken = NormalizeTagToken(etagService.ComputeETag(payload));
        return $"\"{RepresentationTagPrefix}{entityToken}-{payloadToken}\"";
    }

    public static bool MatchesEntityOrRepresentation(
        string precondition,
        string entityETag,
        IETagService etagService)
    {
        ArgumentNullException.ThrowIfNull(etagService);

        if (etagService.MatchesPrecondition(precondition, entityETag))
        {
            return true;
        }

        var entityToken = NormalizeTagToken(entityETag);
        var representationPrefix = string.Concat(RepresentationTagPrefix, entityToken, "-");
        foreach (var rawTag in precondition.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = NormalizeTagToken(rawTag);
            if (tag.StartsWith(representationPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeTagToken(string etag)
    {
        var token = etag.Trim();
        if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            token = token[2..].Trim();
        }

        return token.Trim('"');
    }

    private static object? NormalizeForEtag(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, dictionaryValue) in readOnlyDictionary)
            {
                sorted[key] = NormalizeForEtag(dictionaryValue);
            }

            return sorted;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (var entry in dictionary)
            {
                sorted[entry.Key] = NormalizeForEtag(entry.Value);
            }

            return sorted;
        }

        if (value is JsonElement element)
        {
            return NormalizeJsonElement(element);
        }

        if (value is IEnumerable enumerable && value is not string && value is not byte[])
        {
            var items = new List<object?>();
            foreach (var item in enumerable)
            {
                items.Add(NormalizeForEtag(item));
            }

            return items;
        }

        return value switch
        {
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D"),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
    }

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .ToDictionary(
                    static property => property.Name,
                    static property => NormalizeJsonElement(property.Value),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(NormalizeJsonElement)
                .ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64Value) => int64Value,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;

            case string stringValue:
                writer.WriteStringValue(stringValue);
                return;

            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                return;

            case byte byteValue:
                writer.WriteNumberValue(byteValue);
                return;

            case sbyte sbyteValue:
                writer.WriteNumberValue(sbyteValue);
                return;

            case short shortValue:
                writer.WriteNumberValue(shortValue);
                return;

            case ushort ushortValue:
                writer.WriteNumberValue(ushortValue);
                return;

            case int intValue:
                writer.WriteNumberValue(intValue);
                return;

            case uint uintValue:
                writer.WriteNumberValue(uintValue);
                return;

            case long longValue:
                writer.WriteNumberValue(longValue);
                return;

            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                return;

            case float floatValue:
                writer.WriteNumberValue(floatValue);
                return;

            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                return;

            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                return;

            case DateTime dateTime:
                writer.WriteStringValue(dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;

            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;

            case Guid guid:
                writer.WriteStringValue(guid.ToString("D"));
                return;

            case byte[] bytes:
                writer.WriteStringValue(Convert.ToBase64String(bytes));
                return;

            case JsonElement element:
                WriteCanonicalJson(writer, NormalizeJsonElement(element));
                return;

            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                WriteObject(writer, readOnlyDictionary);
                return;

            case IDictionary<string, object?> dictionary:
                WriteObject(writer, dictionary);
                return;

            case IEnumerable enumerable when value is not string and not byte[]:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                return;

            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> dictionary)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in dictionary.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteCanonicalJson(writer, value);
        }

        writer.WriteEndObject();
    }
}
