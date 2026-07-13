// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Drift guard for the integration-test seed (honua-server#1568).
///
/// The integration-test web host runs with <c>HONUA_SKIP_MIGRATIONS=true</c> and a
/// <c>NullDatabaseMigrationRunner</c>, so production DbUp migrations never run against the
/// test database. The durable RBAC stores (<c>PostgresRoleStore</c>,
/// <c>PostgresRlsPolicyStore</c>) are still registered against the default <c>honua</c>
/// schema, and they read explicitly-qualified <c>honua.rbac_*</c> tables regardless of the
/// per-test <c>search_path</c>. Those tables therefore have to exist in the shared
/// <c>honua</c> schema that the test seed (<c>tests/seed/server.yaml</c>) hand-mirrors from
/// the production migrations.
///
/// When migration 041 added <c>honua.rbac_roles</c>/<c>rbac_role_permissions</c> the seed
/// was not updated to match, so RBAC-touching integration tests intermittently/consistently
/// failed with <c>relation "honua.rbac_roles" does not exist</c>, which then cascaded into
/// unrelated assertion failures across whatever shard ran the RBAC path (the EndpointRegistry
/// coverage test, OData paging assertions, etc.). This test fails fast and deterministically
/// at build time if a durable authorization store references a <c>honua.*</c> table the seed
/// has not mirrored, instead of surfacing later as a hard-to-attribute integration-shard flake.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class SeedRbacTableParityTests
{
    // Captures the table name from `SchemaSearchPath.QualifyTable("<table>", ...)` call sites
    // in the Postgres authorization stores. These are the tables the stores read from the
    // default `honua` schema in the migration-skipping test host.
    private static readonly Regex QualifyTableCall = new(
        "QualifyTable\\(\\s*\"(?<table>[A-Za-z_][A-Za-z0-9_]*)\"",
        RegexOptions.CultureInvariant);

    // Captures `CREATE TABLE [IF NOT EXISTS] honua.<table>` from the seed YAML.
    private static readonly Regex SeedCreateTable = new(
        "CREATE\\s+TABLE\\s+(?:IF\\s+NOT\\s+EXISTS\\s+)?honua\\.(?<table>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void ServerSeed_ShouldMirror_EveryDurableAuthorizationStoreTable()
    {
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());

        var requiredTables = EnumerateAuthorizationStoreTables(projectRoot);
        requiredTables.Should().NotBeEmpty(
            "the Postgres authorization stores must reference at least one honua.* table; " +
            "an empty result means the source scan is broken, not that there is nothing to guard.");

        var seedTables = EnumerateSeedTables(projectRoot);

        var missing = requiredTables
            .Where(table => !seedTables.Contains(table))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "the migration-skipping integration-test host registers the durable authorization stores " +
            "against the default 'honua' schema, so every honua.* table they query must be created by " +
            "tests/seed/server.yaml. Mirror the corresponding migration DDL into the seed (honua-server#1568). " +
            $"Missing: {string.Join(", ", missing)}.");
    }

    private static HashSet<string> EnumerateAuthorizationStoreTables(string projectRoot)
    {
        var authorizationDirectory = ArchitectureTestHelpers.CombinePath(
            projectRoot, "src", "Honua.Postgres", "Features", "Authorization");

        var tables = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in Directory.EnumerateFiles(authorizationDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText))
        {
            foreach (Match match in QualifyTableCall.Matches(source))
            {
                tables.Add(match.Groups["table"].Value);
            }
        }

        return tables;
    }

    private static HashSet<string> EnumerateSeedTables(string projectRoot)
    {
        var seedPath = ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "seed", "server.yaml");
        File.Exists(seedPath).Should().BeTrue($"the integration-test seed must exist at {seedPath}.");

        var seed = File.ReadAllText(seedPath);
        var tables = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in SeedCreateTable.Matches(seed))
        {
            tables.Add(match.Groups["table"].Value);
        }

        return tables;
    }

    private static string FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            if (File.Exists(ArchitectureTestHelpers.CombinePath(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Honua.sln from the current test directory.");
    }
}
