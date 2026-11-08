// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Orchestration.Domain;

// .NET 10 source generation supplies zero for omitted init-only properties instead of preserving their initializers.
public sealed class StepRetryPolicyJsonConverter : JsonConverter<StepRetryPolicy>
{
    public override StepRetryPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Retry policy must be an object.");
        }

        return new StepRetryPolicy
        {
            MaxAttempts = ReadInteger(root, "maxAttempts"),
            InitialDelaySeconds = ReadInteger(root, "initialDelaySeconds"),
            BackoffMultiplier = ReadMultiplier(root),
            MaxDelaySeconds = ReadInteger(root, "maxDelaySeconds", 300)
        };
    }

    public override void Write(Utf8JsonWriter writer, StepRetryPolicy value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("maxAttempts", value.MaxAttempts);
        writer.WriteNumber("initialDelaySeconds", value.InitialDelaySeconds);
        writer.WriteNumber("backoffMultiplier", value.BackoffMultiplier);
        writer.WriteNumber("maxDelaySeconds", value.MaxDelaySeconds);
        writer.WriteEndObject();
    }

    private static int ReadInteger(JsonElement root, string name, int? defaultValue = null)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return defaultValue ?? throw new JsonException($"Retry policy requires '{name}'.");
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new JsonException($"Retry policy '{name}' must be a whole number.");
    }

    private static double ReadMultiplier(JsonElement root)
    {
        if (!root.TryGetProperty("backoffMultiplier", out var value))
        {
            return 2.0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : throw new JsonException("Retry backoff multiplier must be a number.");
    }
}
