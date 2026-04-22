// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class DatabaseMigrationSafetyTests
{
    private static readonly Regex CompatibilityReviewMarker = new(
        @"^\s*--\s*honua:compatibility-review\b.*\breason\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex ConcurrentIndexPattern = new(
        @"\bCREATE\s+INDEX\s+CONCURRENTLY\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (string RuleName, Regex Pattern)[] PotentiallyBreakingPatterns =
    [
        CreatePattern("drop-column", @"\bALTER\s+TABLE\b[\s\S]*?\bDROP\s+COLUMN\b"),
        CreatePattern("rename-column", @"\bALTER\s+TABLE\b[\s\S]*?\bRENAME\s+COLUMN\b"),
        CreatePattern("alter-column-type", @"\bALTER\s+TABLE\b[\s\S]*?\bALTER\s+COLUMN\b[\s\S]*?\bTYPE\b"),
        CreatePattern("set-not-null", @"\bALTER\s+TABLE\b[\s\S]*?\bALTER\s+COLUMN\b[\s\S]*?\bSET\s+NOT\s+NULL\b"),
        CreatePattern("drop-table", @"\bDROP\s+TABLE\b"),
        CreatePattern("drop-schema", @"\bDROP\s+SCHEMA\b"),
        CreatePattern("drop-sequence", @"\bDROP\s+SEQUENCE\b")
    ];

    [ArchitectureTest]
    public void MigrationScripts_ShouldRequireExplicitCompatibilityReview_ForPotentiallyBreakingSchemaChanges()
    {
        var violations = new List<string>();

        foreach (var migrationFile in EnumerateMigrationFiles())
        {
            var sql = File.ReadAllText(migrationFile);
            var matchedRules = AnalyzePotentiallyBreakingChanges(sql);

            if (matchedRules.Count == 0 || CompatibilityReviewMarker.IsMatch(sql))
            {
                continue;
            }

            violations.Add(
                $"{Path.GetFileName(migrationFile)} contains potentially breaking schema changes ({string.Join(", ", matchedRules)}) " +
                "but does not declare an explicit compatibility review marker. Add a comment like " +
                "'-- honua:compatibility-review reason=<why this migration is rollout-safe>'.");
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

    private static List<string> AnalyzePotentiallyBreakingChanges(string sql)
    {
        var normalized = StripCommentsAndQuotedBodies(sql);
        var matchedRules = new List<string>();

        foreach (var (ruleName, pattern) in PotentiallyBreakingPatterns)
        {
            if (pattern.IsMatch(normalized))
            {
                matchedRules.Add(ruleName);
            }
        }

        return matchedRules;
    }

    private static string StripCommentsAndQuotedBodies(string sql)
    {
        var sanitized = Regex.Replace(sql, @"/\*[\s\S]*?\*/", " ", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"\$(?<tag>[A-Za-z_][A-Za-z0-9_]*)?\$[\s\S]*?\$\k<tag>\$",
            " ",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"'([^']|'')*'",
            "''",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(
            sanitized,
            @"--.*?$",
            string.Empty,
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        return sanitized;
    }

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

    private static (string RuleName, Regex Pattern) CreatePattern(string ruleName, string pattern) =>
        (ruleName, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
