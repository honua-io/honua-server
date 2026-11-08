// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

// The source-generated serializer assigns default for a missing member, and that value is draft, which the serving gate then hides.
internal sealed class MetadataV2StatusJsonConverter : JsonConverter<MetadataV2Status>
{
    public override bool HandleNull => true;

    public override MetadataV2Status Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new MetadataV2Status();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Metadata v2 status must be a JSON object.");
        }

        PreservedEnumValue<MetadataV2LifecycleStatus>? lifecycle = null;
        PreservedEnumValue<MetadataV2OperationalState> state = MetadataV2OperationalState.Unknown;
        var conditions = new List<MetadataV2Condition>();
        DateTimeOffset? observedAt = null;
        var additionalProperties = new Dictionary<string, JsonElement>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Metadata v2 status must be a JSON object.");
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Metadata v2 status ended before a property value.");
            }

            switch (name)
            {
                case "lifecycle":
                    lifecycle = ReadLifecycle(ref reader);
                    break;
                case "state":
                    state = ReadState(ref reader);
                    break;
                case "conditions":
                    conditions = ReadConditions(ref reader);
                    break;
                case "observedAt":
                    observedAt = ReadObservedAt(ref reader);
                    break;
                default:
                    additionalProperties[name!] = ReadAdditionalProperty(ref reader);
                    break;
            }
        }

        return new MetadataV2Status
        {
            LifecycleValue = lifecycle ?? new PreservedEnumValue<MetadataV2LifecycleStatus>(MetadataV2LifecycleStatus.Active),
            StateValue = state,
            Conditions = conditions,
            ObservedAt = observedAt,
            AdditionalProperties = additionalProperties.Count == 0 ? null : additionalProperties
        };
    }

    public override void Write(Utf8JsonWriter writer, MetadataV2Status value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        WriteEnum(writer, "lifecycle", value.LifecycleValue);
        WriteEnum(writer, "state", value.StateValue);
        writer.WritePropertyName("conditions");
        writer.WriteStartArray();
        foreach (var condition in value.Conditions)
        {
            WriteCondition(writer, condition, options);
        }

        writer.WriteEndArray();
        WriteNullableTimestamp(writer, options, "observedAt", value.ObservedAt);
        WriteAdditionalProperties(writer, value.AdditionalProperties);
        writer.WriteEndObject();
    }

    private static PreservedEnumValue<MetadataV2LifecycleStatus>? ReadLifecycle(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null ? null : ReadStatusEnum<MetadataV2LifecycleStatus>(ref reader);

    private static PreservedEnumValue<MetadataV2OperationalState> ReadState(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null
            ? new PreservedEnumValue<MetadataV2OperationalState>(MetadataV2OperationalState.Unknown)
            : ReadStatusEnum<MetadataV2OperationalState>(ref reader);

    private static PreservedEnumValue<TEnum> ReadStatusEnum<TEnum>(ref Utf8JsonReader reader)
        where TEnum : struct, Enum
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var name = reader.GetString()!.Trim();
            if (name.Length > 0 && !char.IsAsciiDigit(name[0]) &&
                name[0] is not '-' and not '+' && !name.Contains(',') &&
                Enum.TryParse<TEnum>(name, ignoreCase: true, out var value) &&
                TolerantEnumJsonConverter<TEnum>.IsRecognizedValue(value))
            {
                return new PreservedEnumValue<TEnum>(value);
            }
            return PreservedEnumValue<TEnum>.FromUnknownToken(ref reader);
        }
        return PreservedEnumJsonConverter<TEnum>.Instance.Read(ref reader, typeof(PreservedEnumValue<TEnum>), JsonSerializerOptions.Default);
    }

    private static List<MetadataV2Condition> ReadConditions(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Metadata v2 status conditions must be an array.");
        }

        var conditions = new List<MetadataV2Condition>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            conditions.Add(ReadCondition(ref reader));
        }

        return conditions;
    }

    private static MetadataV2Condition ReadCondition(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Metadata v2 status condition must be a JSON object.");
        }

        var type = string.Empty;
        var status = string.Empty;
        string? reason = null;
        string? message = null;
        DateTimeOffset? lastTransitionAt = null;
        var additionalProperties = new Dictionary<string, JsonElement>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Metadata v2 status condition must be a JSON object.");
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Metadata v2 status condition ended before a property value.");
            }

            switch (name)
            {
                case "type":
                    type = ReadString(ref reader);
                    break;
                case "status":
                    status = ReadString(ref reader);
                    break;
                case "reason":
                    reason = ReadNullableString(ref reader);
                    break;
                case "message":
                    message = ReadNullableString(ref reader);
                    break;
                case "lastTransitionAt":
                    lastTransitionAt = ReadObservedAt(ref reader);
                    break;
                default:
                    additionalProperties[name!] = ReadAdditionalProperty(ref reader);
                    break;
            }
        }

        return new MetadataV2Condition
        {
            Type = type,
            Status = status,
            Reason = reason,
            Message = message,
            LastTransitionAt = lastTransitionAt,
            AdditionalProperties = additionalProperties.Count == 0 ? null : additionalProperties
        };
    }

    private static DateTimeOffset? ReadObservedAt(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();

    private static string ReadString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null ? string.Empty : reader.GetString() ?? string.Empty;

    private static string? ReadNullableString(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetString();

    private static void WriteCondition(Utf8JsonWriter writer, MetadataV2Condition condition, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", condition.Type);
        writer.WriteString("status", condition.Status);
        WriteNullableString(writer, options, "reason", condition.Reason);
        WriteNullableString(writer, options, "message", condition.Message);
        WriteNullableTimestamp(writer, options, "lastTransitionAt", condition.LastTransitionAt);
        WriteAdditionalProperties(writer, condition.AdditionalProperties);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, JsonSerializerOptions options, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
            return;
        }

        if (!OmitNulls(options))
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNullableTimestamp(Utf8JsonWriter writer, JsonSerializerOptions options, string name, DateTimeOffset? value)
    {
        if (value is { } present)
        {
            writer.WriteString(name, present);
            return;
        }

        if (!OmitNulls(options))
        {
            writer.WriteNull(name);
        }
    }

    private static bool OmitNulls(JsonSerializerOptions options)
        => options.DefaultIgnoreCondition is JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault;

    private static void WriteEnum<TEnum>(Utf8JsonWriter writer, string name, PreservedEnumValue<TEnum> value)
        where TEnum : struct, Enum
    {
        writer.WritePropertyName(name);
        PreservedEnumJsonConverter<TEnum>.Instance.Write(writer, value, JsonSerializerOptions.Default);
    }

    private static JsonElement ReadAdditionalProperty(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    private static void WriteAdditionalProperties(Utf8JsonWriter writer, IReadOnlyDictionary<string, JsonElement>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Key);
            property.Value.WriteTo(writer);
        }
    }
}
