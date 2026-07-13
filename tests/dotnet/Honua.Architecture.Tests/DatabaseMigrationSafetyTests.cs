// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class DatabaseMigrationSafetyTests
{
    private static readonly Regex ConcurrentIndexPattern = new(
        @"\bCREATE\s+INDEX\s+CONCURRENTLY\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void MigrationScripts_ShouldRequireExplicitCompatibilityReview_ForPotentiallyBreakingSchemaChanges()
    {
        var violations = new List<string>();

        foreach (var migrationFile in EnumerateMigrationFiles())
        {
            var sql = File.ReadAllText(migrationFile);
            var matchedRules = AnalyzePotentiallyBreakingChanges(sql);

            if (matchedRules.Count == 0 || MigrationSafetyClassifier.HasCompatibilityReviewMarker(sql))
            {
                continue;
            }

            violations.Add(
                $"{Path.GetFileName(migrationFile)} contains potentially breaking schema changes ({string.Join(", ", matchedRules)}) " +
                "but does not declare an identity-bound compatibility review marker. Add a comment like " +
                $"'{MigrationSafetyClassifier.CompatibilityReviewMarkerConvention}'.");
        }

        violations.Should().BeEmpty(
            "Potentially breaking migrations must declare an explicit compatibility review so rollout safety is visible in code review and CI.");
    }

    [Theory]
    [InlineData("ALTER TABLE honua.layers DROP COLUMN legacy_name;", "drop-column")]
    [InlineData("ALTER TABLE honua.layers RENAME COLUMN legacy_name TO display_name;", "rename-column")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN metadata TYPE TEXT;", "alter-column-type")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN service_name SET NOT NULL;", "set-not-null")]
    [InlineData("DROP TABLE honua.layers;", "drop-table")]
    public void MigrationSafetyAnalyzer_ShouldDetectPotentiallyBreakingStatements(string sql, string expectedRule)
    {
        AnalyzePotentiallyBreakingChanges(sql).Should().Contain(expectedRule);
    }

    [Fact]
    public void MigrationSafetyAnalyzer_ShouldIgnoreStatementsEmbeddedInsideFunctionBodies()
    {
        const string sql = """
            CREATE OR REPLACE FUNCTION honua.create_import_table(table_name text)
            RETURNS void
            LANGUAGE plpgsql
            AS $$
            BEGIN
                EXECUTE format('DROP TABLE IF EXISTS %I', table_name);
                EXECUTE format('ALTER TABLE %I RENAME COLUMN old_name TO new_name', table_name);
            END;
            $$;
            """;

        AnalyzePotentiallyBreakingChanges(sql).Should().BeEmpty();
    }

    [ArchitectureTest]
    public void MigrationScripts_ShouldNotUseConcurrentIndexes_WithTransactionalRunner()
    {
        var violations = EnumerateMigrationFiles()
            .Where(file => ConcurrentIndexPattern.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToArray();

        violations.Should().BeEmpty(
            "DbUp executes these migrations transactionally, so CREATE INDEX CONCURRENTLY will fail during startup and integration tests.");
    }

    [ArchitectureTest]
    public void ServerSeed_ShouldMirror_OpsAutonomyTerminalOutcomeConstraint()
    {
        const string expectedConstraint =
            "CHECK (outcome IS NULL OR outcome IN (0, 1, 2, 3, 4))";
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        var migration = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "Honua.Server",
            "Migrations",
            "080_AddOpsAutonomyTerminalOutcomes.sql"));
        var seed = File.ReadAllText(Path.Combine(projectRoot, "tests", "seed", "server.yaml"));

        migration.Should().Contain(expectedConstraint,
            "migration 080 must retain every terminal autonomy outcome");
        seed.Should().Contain(expectedConstraint,
            "integration hosts skip migrations, so their canonical seed must mirror migration 080");
    }

    // Delegates to the shared runtime classifier so the architecture gate and the runtime
    // migration-safety enforcement (MigrationSafetyClassifier) share one source of truth.
    private static IReadOnlyList<string> AnalyzePotentiallyBreakingChanges(string sql)
        => MigrationSafetyClassifier.DetectBreakingRules(sql);

    private static IEnumerable<string> EnumerateMigrationFiles()
    {
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        var migrationDirectories = new[]
        {
            Path.Combine(projectRoot, "src", "Honua.Server", "Migrations"),
            Path.Combine(projectRoot, "src", "Honua.Postgres", "Migrations")
        };

        return migrationDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static string FindProjectRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Honua.sln from the current test directory.");
    }
}
