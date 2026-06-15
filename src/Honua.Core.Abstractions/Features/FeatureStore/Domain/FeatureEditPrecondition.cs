// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Optimistic-concurrency precondition for a single feature targeted by an edit batch.
/// Protocol adapters compute <see cref="ExpectedStateToken"/> from the feature snapshot
/// they validated the client precondition (If-Match / ETag) against, and feature writers
/// re-validate the stored row's token inside the write transaction so a concurrent writer
/// cannot slip between the protocol-level check and the write (TOCTOU).
/// </summary>
public readonly record struct FeatureEditPrecondition
{
    /// <summary>
    /// Object ID of the feature the precondition applies to. Update and delete
    /// operations in the same batch that target this object ID must only be applied
    /// when the stored row still matches <see cref="ExpectedStateToken"/>.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Canonical state token computed via <see cref="FeatureStateToken.Compute"/> from
    /// the snapshot the caller validated its protocol precondition against.
    /// </summary>
    public required string ExpectedStateToken { get; init; }
}

/// <summary>
/// Computes a deterministic, provider-neutral state token for a stored feature.
/// The token is a SHA-256 hash over a canonical serialization of the feature's
/// object ID, geometry (WKB), and attributes (ordinal-sorted keys, normalized
/// values). Both the protocol adapter (over its read snapshot) and the feature
/// writer (over the row re-read inside the write transaction) must observe the
/// feature through the same provider read path so identical stored state yields
/// an identical token.
/// </summary>
public static class FeatureStateToken
{
    /// <summary>
    /// Computes the canonical state token for the supplied feature.
    /// </summary>
    /// <param name="feature">Feature snapshot to compute the token for.</param>
    /// <returns>Lowercase hex SHA-256 token over the canonical feature state.</returns>
    public static string Compute(Feature feature)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", feature.Id);
            if (feature.Geometry is { Length: > 0 })
            {
                writer.WriteString("geometry", Convert.ToBase64String(feature.Geometry));
            }
            else
            {
                writer.WriteNull("geometry");
            }

            writer.WritePropertyName("attributes");
            WriteCanonicalValue(writer, feature.Attributes);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void WriteCanonicalValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;

            case JsonElement element:
                WriteCanonicalJsonElement(writer, element);
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

            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                WriteCanonicalObject(writer, readOnlyDictionary);
                return;

            case IDictionary<string, object?> dictionary:
                WriteCanonicalObject(writer, dictionary);
                return;

            case IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteCanonicalValue(writer, item);
                }

                writer.WriteEndArray();
                return;

            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }
    }

    private static void WriteCanonicalObject(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object?>> entries)
    {
        writer.WriteStartObject();
        foreach (var (key, entryValue) in entries.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteCanonicalValue(writer, entryValue);
        }

        writer.WriteEndObject();
    }

    private static void WriteCanonicalJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJsonElement(writer, property.Value);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJsonElement(writer, item);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;

            case JsonValueKind.Number when element.TryGetInt64(out var int64Value):
                writer.WriteNumberValue(int64Value);
                return;

            case JsonValueKind.Number when element.TryGetDecimal(out var decimalValue):
                writer.WriteNumberValue(decimalValue);
                return;

            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetDouble());
                return;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;

            default:
                writer.WriteNullValue();
                return;
        }
    }
}
