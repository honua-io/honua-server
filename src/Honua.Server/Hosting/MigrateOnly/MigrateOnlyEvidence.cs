// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Npgsql;

namespace Honua.Server.Hosting.MigrateOnly;

/// <summary>Correlation evidence for the canonical migrate-only summary.</summary>
internal static class MigrateOnlyEvidence
{
    internal const string RunIdKey = "HONUA_MIGRATION_RUN_ID";
    /// <summary>Hashes a connection locator, not credentials or proof of physical database identity.</summary>
    internal static string ComputeTargetLocatorHash(string connectionString, string schema)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Host.Contains(',')
            || string.IsNullOrWhiteSpace(connection.Database))
        {
            throw new InvalidOperationException("Migration requires an explicit single database locator.");
        }
        // JSON provides unambiguous component boundaries; passwords/users/options are excluded.
        var components = new[]
        {
            "honua.database-locator/v1", connection.Host.Trim().TrimEnd('.').ToLowerInvariant(),
            connection.Port.ToString(CultureInfo.InvariantCulture), connection.Database, schema,
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(components, MigrateOnlyEvidenceJsonContext.Default.StringArray)));
    }

    internal static string ComputePackagedManifestHash(Assembly migrationsAssembly)
    {
        var assemblies = new[] { migrationsAssembly, typeof(PostgresDatabaseMigrationRunner).Assembly }.Distinct();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in assemblies.SelectMany(assembly => assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Assembly: assembly, Name: name))).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            using var stream = entry.Assembly.GetManifestResourceStream(entry.Name)!;
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Name + "\n" + Convert.ToHexStringLower(SHA256.HashData(stream)) + "\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class MigrateOnlyEvidenceJsonContext : JsonSerializerContext;
