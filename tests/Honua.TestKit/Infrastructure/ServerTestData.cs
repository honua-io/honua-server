// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit.Seeding;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Helper methods for seeding server integration test data.
/// </summary>
public static class ServerTestData
{
    public static Task SeedAsync(PostgresFixture fixture, string schemaName)
    {
        return fixture.ApplySeedAsync(ResolveSeedPath(), schemaName);
    }

    private static string ResolveSeedPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new FileNotFoundException("Unable to locate repository root for seed data.");
        }

        return Path.Combine(directory.FullName, "tests", "seed", "server.yaml");
    }
}
