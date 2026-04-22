// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests;

internal static class TestConnectionStrings
{
    private const string FallbackPostgresConnectionString = "Host=localhost;Database=test;Username=test;Password=test";

    public static string DefaultPostgresConnectionString =>
        Environment.GetEnvironmentVariable("HONUA_TEST_DB_URL") switch
        {
            { Length: > 0 } connectionString => connectionString,
            _ => FallbackPostgresConnectionString
        };
}
