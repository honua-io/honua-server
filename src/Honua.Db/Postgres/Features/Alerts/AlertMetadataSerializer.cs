// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;

namespace Honua.Postgres.Features.Alerts;

internal static class AlertMetadataSerializer
{
    public static ImmutableDictionary<string, string?> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ImmutableDictionary<string, string?>.Empty;
            }

            var builder = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                builder[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : property.Value.ToString();
            }

            return builder.ToImmutable();
        }
        catch (JsonException)
        {
            return ImmutableDictionary<string, string?>.Empty;
        }
    }

    public static string Serialize(ImmutableDictionary<string, string?> metadata)
    {
        if (metadata.IsEmpty)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(
            new Dictionary<string, string?>(metadata, StringComparer.OrdinalIgnoreCase),
            AlertMetadataJsonContext.Default.MetadataDictionary);
    }
}
