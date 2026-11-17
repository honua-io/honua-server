// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

public static class MetadataV2StorageBindingOptions
{
    public static (string? SchemaName, string? TableName) ResolveRelationalTable(this MetadataV2StorageBinding binding)
    {
        var schemaName = binding.ReadStringOption("schemaName") ?? binding.ReadStringOption("schema");
        var tableName = binding.ReadStringOption("tableName") ?? binding.ReadStringOption("table");
        if (string.IsNullOrWhiteSpace(tableName))
        {
            var parsed = ParseRelationalLocator(binding.Locator);
            schemaName ??= parsed.SchemaName;
            tableName = parsed.TableName;
        }

        return (schemaName, tableName);
    }

    public static string? ReadStringOption(this MetadataV2StorageBinding binding, string key)
    {
        if (!binding.Options.TryGetValue(key, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    public static int? ReadIntOption(this MetadataV2StorageBinding binding, string key)
    {
        if (!binding.Options.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static (string? SchemaName, string? TableName) ParseRelationalLocator(string? locator)
    {
        var trimmed = locator?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal))
        {
            return (null, null);
        }

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => (null, null),
            1 => (null, UnquoteIdentifier(parts[0])),
            _ => (UnquoteIdentifier(parts[^2]), UnquoteIdentifier(parts[^1]))
        };
    }

    private static string UnquoteIdentifier(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : trimmed;
    }
}
