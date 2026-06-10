// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Npgsql;

namespace Honua.Postgres.Features.Security.ConnectionSecretResolvers;

internal static class SecretValueExtractor
{
    public static string ExtractValue(string rawSecret)
    {
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            throw new InvalidOperationException("Secret value is empty.");
        }

        var trimmed = rawSecret.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return rawSecret;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            if (TryGetStringProperty(root, "connectionString", out var connectionString))
            {
                return connectionString!;
            }

            if (TryGetStringProperty(root, "ConnectionString", out connectionString))
            {
                return connectionString!;
            }

            if (TryBuildConnectionString(root, out connectionString))
            {
                return connectionString!;
            }

            return rawSecret;
        }
        catch (JsonException)
        {
            return rawSecret;
        }
    }

    private static bool TryGetStringProperty(JsonElement root, string propertyName, out string? value)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static bool TryBuildConnectionString(JsonElement root, out string? connectionString)
    {
        connectionString = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetStringProperty(root, "username", out var username) ||
            !TryGetStringProperty(root, "password", out var password) ||
            !TryGetStringProperty(root, "host", out var host))
        {
            return false;
        }

        if (!TryGetStringProperty(root, "dbname", out var database) &&
            !TryGetStringProperty(root, "database", out database))
        {
            return false;
        }

        var port = TryGetIntProperty(root, "port") ?? 5432;

        // Build via NpgsqlConnectionStringBuilder so values containing ';' or '='
        // (Secrets Manager generated passwords can include both) are quoted/escaped
        // instead of corrupting the string or injecting extra connection parameters.
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password,
            Database = database
        };

        connectionString = builder.ConnectionString;
        return true;
    }

    private static int? TryGetIntProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }
}
