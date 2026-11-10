// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Honua.Core.Features.FeatureStore.Domain;

// Writers re-validate the expectation inside the write transaction so a concurrent writer cannot land between the check and the write.
public readonly record struct FeatureEditPrecondition
{
    public required long ObjectId { get; init; }

    public string? ExpectedStateToken { get; init; }

    // An absent row is a successful idempotent delete, while a row inserted under the same id is a precondition failure.
    public bool ExpectedRowAbsent { get; init; }

    public ImmutableArray<string> MaskedFields { get; init; }
}

// Both the adapter and the writer must hash the same provider read, or identical stored state can yield different tokens.
public static class FeatureStateToken
{
    // Use the token from before field security removes attributes; the writer hashes the complete locked row.
    public static string FromReadSnapshot(Feature feature)
        => feature.ReadStateToken ?? Compute(feature);

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
