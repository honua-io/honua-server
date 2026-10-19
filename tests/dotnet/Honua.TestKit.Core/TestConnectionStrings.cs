// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;

namespace Honua.TestKit;

public static class TestConnectionStrings
{
    // In-memory hosts still validate configuration in Production. Use process-local
    // material rather than a shipped placeholder; real databases use HONUA_TEST_DB_URL.
    private static readonly string _fallbackPostgresConnectionString =
        $"Host=localhost;Database=test;Username=test;Password={Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";

    public static string DefaultPostgresConnectionString =>
        Environment.GetEnvironmentVariable("HONUA_TEST_DB_URL") switch
        {
            { Length: > 0 } connectionString => connectionString,
            _ => _fallbackPostgresConnectionString
        };
}
