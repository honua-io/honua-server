// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit;

internal static class PostgresFixtureMigrations
{
    private const string MarkerPrefix = "Honua.TestKit.Postgres.SharedMigrations.";

    internal static string[] Discover()
    {
        var assembly = typeof(PostgresFixtureMigrations).Assembly;
        var resources = assembly.GetManifestResourceNames();
        var migrations = resources
            .Where(name => name.StartsWith(MarkerPrefix, StringComparison.Ordinal)
                && name.EndsWith(".fixture", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(marker =>
            {
                using var stream = assembly.GetManifestResourceStream(marker)!;
                using var reader = new StreamReader(stream);
                var migration = reader.ReadToEnd().Trim();
                if (!resources.Contains(migration, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Fixture marker '{marker}' names missing migration '{migration}'.");
                }

                return migration;
            })
            .ToArray();

        if (migrations.Length == 0 || migrations.Distinct(StringComparer.Ordinal).Count() != migrations.Length)
        {
            throw new InvalidOperationException("Shared fixture migrations must be nonempty and unique.");
        }

        return migrations;
    }
}
