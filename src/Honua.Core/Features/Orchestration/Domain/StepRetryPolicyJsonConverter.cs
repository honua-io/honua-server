// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Orchestration.Domain;

// .NET 10 source generation supplies zero for omitted init-only properties instead of preserving their initializers.
public sealed class StepRetryPolicyJsonConverter : JsonConverter<StepRetryPolicy>
{
    public override StepRetryPolicy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Retry policy must be an object.");
        }

        var comparison = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var properties = new Dictionary<string, JsonElement>(comparison);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }
        var maxAttempts = PropertyName(nameof(StepRetryPolicy.MaxAttempts), options);
        var initialDelay = PropertyName(nameof(StepRetryPolicy.InitialDelaySeconds), options);
        if (!properties.TryGetValue(maxAttempts, out var attempts) || !properties.TryGetValue(initialDelay, out var delay))
        {
            throw new JsonException($"Retry policy requires '{maxAttempts}' and '{initialDelay}'.");
        }
        var policy = new StepRetryPolicy
        {
            MaxAttempts = ReadNumber<int>(attempts, options),
            InitialDelaySeconds = ReadNumber<int>(delay, options)
        };
        if (properties.TryGetValue(PropertyName(nameof(StepRetryPolicy.BackoffMultiplier), options), out var multiplier))
        {
            policy = policy with { BackoffMultiplier = ReadNumber<double>(multiplier, options) };
        }
        if (properties.TryGetValue(PropertyName(nameof(StepRetryPolicy.MaxDelaySeconds), options), out var maximum))
        {
            policy = policy with { MaxDelaySeconds = ReadNumber<int>(maximum, options) };
        }
        return policy;
    }

    public override void Write(Utf8JsonWriter writer, StepRetryPolicy value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (options.NumberHandling.HasFlag(JsonNumberHandling.WriteAsString))
        {
            writer.WriteString(PropertyName(nameof(value.MaxAttempts), options), value.MaxAttempts.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(PropertyName(nameof(value.InitialDelaySeconds), options), value.InitialDelaySeconds.ToString(CultureInfo.InvariantCulture));
            writer.WriteString(PropertyName(nameof(value.BackoffMultiplier), options), value.BackoffMultiplier.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteString(PropertyName(nameof(value.MaxDelaySeconds), options), value.MaxDelaySeconds.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNumber(PropertyName(nameof(value.MaxAttempts), options), value.MaxAttempts);
            writer.WriteNumber(PropertyName(nameof(value.InitialDelaySeconds), options), value.InitialDelaySeconds);
            writer.WriteNumber(PropertyName(nameof(value.BackoffMultiplier), options), value.BackoffMultiplier);
            writer.WriteNumber(PropertyName(nameof(value.MaxDelaySeconds), options), value.MaxDelaySeconds);
        }
        writer.WriteEndObject();
    }

    private static string PropertyName(string name, JsonSerializerOptions options)
        => options.PropertyNamingPolicy?.ConvertName(name) ?? name;

    private static T ReadNumber<T>(JsonElement value, JsonSerializerOptions options) where T : INumberBase<T>
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String when options.NumberHandling.HasFlag(JsonNumberHandling.AllowReadingFromString) => value.GetString(),
            _ => throw new JsonException("Retry policy values must be numbers.")
        };
        return T.TryParse(text, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new JsonException("Retry policy number is outside the supported format or range.");
    }
}
