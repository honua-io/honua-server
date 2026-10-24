// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Reads Metadata v2 status without turning an omitted lifecycle into
/// <see cref="MetadataV2LifecycleStatus.Draft"/>. The source-generated
/// serializer assigns <c>default(T)</c> for missing members, and that value is
/// draft, which the serving gate then hides. Explicit <c>draft</c> is preserved.
/// </summary>
internal sealed class MetadataV2StatusJsonConverter : JsonConverter<MetadataV2Status>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
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

        MetadataV2LifecycleStatus? lifecycle = null;
        var state = MetadataV2OperationalState.Unknown;
        var conditions = new List<MetadataV2Condition>();
        DateTimeOffset? observedAt = null;

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
                    reader.Skip();
                    break;
            }
        }

        return new MetadataV2Status
        {
            Lifecycle = lifecycle ?? MetadataV2LifecycleStatus.Active,
            State = state,
            Conditions = conditions,
            ObservedAt = observedAt
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, MetadataV2Status value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("lifecycle", LifecycleWireName(value.Lifecycle));
        writer.WriteString("state", StateWireName(value.State));
        writer.WritePropertyName("conditions");
        writer.WriteStartArray();
        foreach (var condition in value.Conditions)
        {
            WriteCondition(writer, condition, options);
        }

        writer.WriteEndArray();
        WriteNullableTimestamp(writer, options, "observedAt", value.ObservedAt);
        writer.WriteEndObject();
    }

    private static MetadataV2LifecycleStatus? ReadLifecycle(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out var number))
            {
                throw new JsonException("Metadata v2 lifecycle must be a string.");
            }

            return number switch
            {
                0 => MetadataV2LifecycleStatus.Draft,
                1 => MetadataV2LifecycleStatus.Active,
                2 => MetadataV2LifecycleStatus.Deprecated,
                3 => MetadataV2LifecycleStatus.Retired,
                4 => MetadataV2LifecycleStatus.Archived,
                _ => throw new JsonException("Metadata v2 lifecycle is not a known value.")
            };
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Metadata v2 lifecycle must be a string.");
        }

        if (!TryParseNamed(reader.GetString(), out MetadataV2LifecycleStatus lifecycle))
        {
            throw new JsonException("Metadata v2 lifecycle is not a known value.");
        }

        return lifecycle;
    }

    private static MetadataV2OperationalState ReadState(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return MetadataV2OperationalState.Unknown;
        }

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (!reader.TryGetInt32(out var number))
            {
                throw new JsonException("Metadata v2 operational state must be a string.");
            }

            return number switch
            {
                0 => MetadataV2OperationalState.Unknown,
                1 => MetadataV2OperationalState.Ready,
                2 => MetadataV2OperationalState.Pending,
                3 => MetadataV2OperationalState.Degraded,
                4 => MetadataV2OperationalState.Failed,
                _ => throw new JsonException("Metadata v2 operational state is not a known value.")
            };
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Metadata v2 operational state must be a string.");
        }

        if (!TryParseNamed(reader.GetString(), out MetadataV2OperationalState state))
        {
            throw new JsonException("Metadata v2 operational state is not a known value.");
        }

        return state;
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
                    reader.Skip();
                    break;
            }
        }

        return new MetadataV2Condition
        {
            Type = type,
            Status = status,
            Reason = reason,
            Message = message,
            LastTransitionAt = lastTransitionAt
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

    private static bool TryParseNamed<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(text) || char.IsAsciiDigit(text[0]) || text[0] is '-' or '+')
        {
            return false;
        }

        return Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static string LifecycleWireName(MetadataV2LifecycleStatus lifecycle)
        => lifecycle switch
        {
            MetadataV2LifecycleStatus.Draft => "draft",
            MetadataV2LifecycleStatus.Active => "active",
            MetadataV2LifecycleStatus.Deprecated => "deprecated",
            MetadataV2LifecycleStatus.Retired => "retired",
            MetadataV2LifecycleStatus.Archived => "archived",
            _ => throw new JsonException("Metadata v2 lifecycle is not a known value.")
        };

    private static string StateWireName(MetadataV2OperationalState state)
        => state switch
        {
            MetadataV2OperationalState.Unknown => "unknown",
            MetadataV2OperationalState.Ready => "ready",
            MetadataV2OperationalState.Pending => "pending",
            MetadataV2OperationalState.Degraded => "degraded",
            MetadataV2OperationalState.Failed => "failed",
            _ => throw new JsonException("Metadata v2 operational state is not a known value.")
        };
}
