// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Admin.Domain;

// The fingerprint is host, port, and database only, never the user, password, or raw connection string.
public static class ConnectionEndpointFingerprint
{
    public const string MetadataKey = "connectionEndpointFingerprint";

    public static string? TryCompute(string? connectionString)
    {
        if (!TryCanonical(connectionString, out var canonical))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static bool IsWellFormed(string? fingerprint)
    {
        if (fingerprint is not { Length: 64 })
        {
            return false;
        }

        foreach (var character in fingerprint)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    public static bool Matches(string? connectionString, string? fingerprint)
    {
        if (!IsWellFormed(fingerprint))
        {
            return false;
        }

        var actual = TryCompute(connectionString);
        return actual is not null && string.Equals(actual, fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCanonical(string? connectionString, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        DbConnectionStringBuilder builder;
        try
        {
            builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!TryRead(builder, out var host, "Host", "Server") ||
            string.IsNullOrWhiteSpace(host) ||
            !TryRead(builder, out var database, "Database", "Initial Catalog") ||
            string.IsNullOrWhiteSpace(database))
        {
            return false;
        }

        var port = 5432;
        if (TryRead(builder, out var portText, "Port") &&
            !int.TryParse(portText, out port))
        {
            return false;
        }

        if (port is < 1 or > 65535)
        {
            return false;
        }

        canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{host.Trim().ToLowerInvariant()}|{port}|{database.Trim()}");
        return true;
    }

    private static bool TryRead(DbConnectionStringBuilder builder, out string? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var raw) && raw is not null)
            {
                value = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = null;
        return false;
    }
}
