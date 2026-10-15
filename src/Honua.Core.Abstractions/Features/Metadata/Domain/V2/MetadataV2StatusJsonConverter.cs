// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
            WriteCondition(writer, condition);
        }

        writer.WriteEndArray();
        if (value.ObservedAt is { } observedAt)
        {
            writer.WriteString("observedAt", observedAt);
        }
        else
        {
            writer.WriteNull("observedAt");
        }

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

        return reader.GetString() switch
        {
            "draft" => MetadataV2LifecycleStatus.Draft,
            "active" => MetadataV2LifecycleStatus.Active,
            "deprecated" => MetadataV2LifecycleStatus.Deprecated,
            "retired" => MetadataV2LifecycleStatus.Retired,
            "archived" => MetadataV2LifecycleStatus.Archived,
            _ => throw new JsonException("Metadata v2 lifecycle is not a known value.")
        };
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

        return reader.GetString() switch
        {
            "unknown" => MetadataV2OperationalState.Unknown,
            "ready" => MetadataV2OperationalState.Ready,
            "pending" => MetadataV2OperationalState.Pending,
            "degraded" => MetadataV2OperationalState.Degraded,
            "failed" => MetadataV2OperationalState.Failed,
            _ => throw new JsonException("Metadata v2 operational state is not a known value.")
        };
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

    private static void WriteCondition(Utf8JsonWriter writer, MetadataV2Condition condition)
    {
        writer.WriteStartObject();
        writer.WriteString("type", condition.Type);
        writer.WriteString("status", condition.Status);
        writer.WriteString("reason", condition.Reason);
        writer.WriteString("message", condition.Message);
        if (condition.LastTransitionAt is { } lastTransitionAt)
        {
            writer.WriteString("lastTransitionAt", lastTransitionAt);
        }
        else
        {
            writer.WriteNull("lastTransitionAt");
        }

        writer.WriteEndObject();
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
