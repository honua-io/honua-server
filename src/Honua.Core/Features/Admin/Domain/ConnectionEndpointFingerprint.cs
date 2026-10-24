// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Immutable fingerprint of the database endpoint a sink actually wrote.
/// Host, port, and database only — never the user, password, or raw connection string.
/// </summary>
public static class ConnectionEndpointFingerprint
{
    /// <summary>Artifact and operation parameter that carries <see cref="TryCompute"/>.</summary>
    public const string MetadataKey = "connectionEndpointFingerprint";

    /// <summary>
    /// Returns the lowercase SHA-256 hex of <c>host|port|database</c>, or null when the
    /// connection string does not name those endpoint parts.
    /// </summary>
    /// <param name="connectionString">Connection string used for the committed write.</param>
    /// <returns>A 64-character hex fingerprint, or null.</returns>
    public static string? TryCompute(string? connectionString)
    {
        if (!TryCanonical(connectionString, out var canonical))
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Returns whether <paramref name="fingerprint"/> is 64 hexadecimal characters.
    /// </summary>
    /// <param name="fingerprint">Candidate fingerprint.</param>
    /// <returns><see langword="true"/> when the value is a 64-character hex string.</returns>
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

    /// <summary>
    /// Returns whether <paramref name="connectionString"/> still names the endpoint that
    /// produced <paramref name="fingerprint"/>. A malformed string or fingerprint returns false.
    /// </summary>
    /// <param name="connectionString">Connection string resolved at publication time.</param>
    /// <param name="fingerprint">Fingerprint recorded when the sink committed.</param>
    /// <returns><see langword="true"/> when both describe the same host, port, and database.</returns>
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
