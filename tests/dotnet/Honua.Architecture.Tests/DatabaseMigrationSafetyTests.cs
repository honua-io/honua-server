// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class DatabaseMigrationSafetyTests
{
    private static Dictionary<string, string> ReadMigrationHashes(string root)
    {
        var directory = Path.Join(root, "eng", "schema", "migration-hashes");
        return Directory.EnumerateFiles(directory, "*.sha256", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(directory, path)[..^".sha256".Length].Replace('\\', '/'),
                path => File.ReadAllText(path).Trim(), StringComparer.Ordinal);
    }

    [ArchitectureTest]
    public void MigrationScripts_MatchImmutableHashLedger()
    {
        var root = FindProjectRoot(Directory.GetCurrentDirectory());
        var scripts = EnumerateMigrationFiles().ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText);
        var hashes = ReadMigrationHashes(root);
        hashes.Keys.Should().BeEquivalentTo(scripts.Keys);
        foreach (var (path, sql) in scripts)
        {
            hashes[path].Should().Be(MigrationHash(sql), $"{path}: journaled migrations remain immutable");
        }
    }

    private static string MigrationHash(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    [Fact]
    public void MigrationClassifier_HistoricalTriggerReplacement_RequiresExactFrozenNameAndHash()
    {
        var root = FindProjectRoot(Directory.GetCurrentDirectory());
        const string name = "003_CreateRelationshipsTable.sql";
        var sql = File.ReadAllText(Path.Join(root, "src", "Honua.Server", "Migrations", name));
        MigrationSafetyClassifier.Classify(name, sql).Classification.Should().Be(MigrationSafetyClassification.ContractAnnotated);
        MigrationSafetyClassifier.Classify("Honua.Server.Migrations." + name, sql).Classification.Should()
            .Be(MigrationSafetyClassification.ContractAnnotated);
        MigrationSafetyClassifier.Classify(name, sql + "\n-- changed").Classification.Should()
            .Be(MigrationSafetyClassification.ContractUnannotated);
        MigrationSafetyClassifier.Classify("future.sql", sql).Classification.Should()
            .Be(MigrationSafetyClassification.ContractUnannotated);
    }

    [Fact]
    public void MigrationClassifier_DropNotNull_IsStillExpand()
        => MigrationSafetyClassifier.Classify("new.sql", "ALTER TABLE honua.features ALTER COLUMN note DROP  NOT NULL;")
            .Classification.Should().Be(MigrationSafetyClassification.Expand);

    [Theory]
    [InlineData("129_ProtectImportTableOwnership.sql", "")]
    [InlineData("129_ProtectImportTableOwnership.sql", "Honua.Server.Migrations.")]
    [InlineData("129_ProtectImportTableOwnership.sql", "src/Honua.Server/Migrations/")]
    [InlineData("145_ProtectImportDependencies.sql", "")]
    [InlineData("145_ProtectImportDependencies.sql", "Honua.Server.Migrations.")]
    [InlineData("145_ProtectImportDependencies.sql", "src/Honua.Server/Migrations/")]
    public void MigrationClassifier_ReviewedImportGuardDefinitions_RequireExactNameAndBytes(string name, string prefix)
    {
        var root = FindProjectRoot(Directory.GetCurrentDirectory());
        var sql = File.ReadAllText(Path.Join(root, "src", "Honua.Server", "Migrations", name));
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("drop-table",
            "routine body inspection must remain enabled; only the exact reviewed installation is additive");
        MigrationSafetyClassifier.Classify(prefix + name, sql).Classification.Should()
            .Be(MigrationSafetyClassification.Expand);
        MigrationSafetyClassifier.Classify(prefix + "future.sql", sql).IsBreaking.Should().BeTrue();
        if (name == "145_ProtectImportDependencies.sql")
        {
            MigrationSafetyClassifier.Classify(prefix + name, sql.Replace(" RESTRICT", " CASCADE", StringComparison.Ordinal))
                .IsBreaking.Should().BeTrue("restoring the destructive dependency behavior is outside this review");
        }
        foreach (var suffix in new[]
        {
            "\n-- changed",
            "\nDROP TABLE honua.features;",
            "\nDO $$ BEGIN EXECUTE 'DROP TABLE honua.features'; END $$;"
        })
        {
            MigrationSafetyClassifier.Classify(prefix + name, sql + suffix).IsBreaking.Should().BeTrue();
        }
    }

    private static readonly Regex ReaderCompatibilityAnnotation = new(
        @"^[ \t]*--[ \t]*honua:reader-(?:incompatible|compatible-from)\b[^\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex WellFormedReaderCompatibilityAnnotation = new(
        @"^[ \t]*--[ \t]*honua:(?:reader-incompatible\b.*\breason[ \t]*=[ \t]*\S.*|reader-compatible-from[ \t]+schema[ \t]*=[ \t]*\d{1,9}[ \t]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void MigrationScripts_ReaderCompatibilityAnnotations_AreWellFormed()
    {
        // An opt-out must say what an older reader would violate, and a raised floor must parse.
        // A malformed annotation already fails safe at runtime (reader-incompatible); this keeps it
        // from shipping by accident.
        var violations = EnumerateMigrationFiles()
            .SelectMany(file => ReaderCompatibilityAnnotation.Matches(File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal))
                .Where(match => !WellFormedReaderCompatibilityAnnotation.IsMatch(match.Value))
                .Select(match => $"{Path.GetFileName(file)}: '{match.Value.Trim()}'"))
            .ToArray();

        violations.Should().BeEmpty(
            $"use '{MigrationReaderCompatibility.ReaderIncompatibleMarkerConvention}' or " +
            $"'{MigrationReaderCompatibility.ReaderCompatibleFromMarkerConvention}'");
    }

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
            var classification = MigrationSafetyClassifier.Classify(Path.GetFileName(migrationFile), sql);

            if (classification.Classification != MigrationSafetyClassification.ContractUnannotated)
            {
                continue;
            }

            violations.Add(
                $"{Path.GetFileName(migrationFile)} contains potentially breaking schema changes ({string.Join(", ", classification.BreakingRules)}) " +
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
    public void MigrationSafetyAnalyzer_ShouldDetectStatementsEmbeddedInsideFunctionBodies()
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

        AnalyzePotentiallyBreakingChanges(sql).Should().BeEquivalentTo(
            ["rename-column", "drop-table"],
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void MigrationSafetyAnalyzer_ShouldClassifyMigration108WithExecutableBodyRules()
    {
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        var migration = ArchitectureTestHelpers.CombinePath(
            projectRoot,
            "src",
            "Honua.Server",
            "Migrations",
            "108_FixLongImportIndexNames.sql");

        var classification = MigrationSafetyClassifier.Classify(
            Path.GetFileName(migration),
            File.ReadAllText(migration));

        classification.Classification.Should().Be(MigrationSafetyClassification.ContractAnnotated);
        classification.BreakingRules.Should().BeEquivalentTo(
            ["rename-table", "rename-index", "drop-table"],
            options => options.WithStrictOrdering());
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
        var migration = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            projectRoot,
            "src",
            "Honua.Server",
            "Migrations",
            "080_AddOpsAutonomyTerminalOutcomes.sql"));
        var seed = File.ReadAllText(ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "seed", "server.yaml"));

        migration.Should().Contain(expectedConstraint,
            "migration 080 must retain every terminal autonomy outcome");
        seed.Should().Contain(expectedConstraint,
            "integration hosts skip migrations, so their canonical seed must mirror migration 080");
    }

    // Raster payload columns whose EXTERNAL storage is a migration-owned effect that the
    // core schema guard verifies on every boot (migrations 055 and 063).
    private static readonly (string Table, string Column)[] ExternalStorageRasterColumns =
    [
        ("raster_data", "raster"),
        ("raster_tiles", "tile_data"),
        ("raster_overviews", "raster"),
    ];

    [ArchitectureTest]
    public void TestSeeds_ThatCreateRasterTables_ShouldSetMigrationOwnedExternalStorage()
    {
        // A seed that creates these tables with default storage after a first boot has
        // journaled 055/063 as no-ops leaves the database in a state the schema guard
        // rejects on the next boot (JournalClaimsMissingSchema), which stops the raster
        // provider migrations from running.
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        var seedFiles = Directory
            .EnumerateFiles(ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "seed"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .Append(ArchitectureTestHelpers.CombinePath(projectRoot, "tests", "python", "shared", "postgis.py"))
            .ToArray();

        var checkedCreates = 0;
        var violations = new List<string>();
        foreach (var seedFile in seedFiles)
        {
            var text = File.ReadAllText(seedFile);
            foreach (var (table, column) in ExternalStorageRasterColumns)
            {
                var creates = new Regex(
                    $@"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?honua\.{table}\s*\(",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!creates.IsMatch(text))
                {
                    continue;
                }

                checkedCreates++;
                var setsExternal = new Regex(
                    $@"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?honua\.{table}\s+ALTER\s+COLUMN\s+{column}\s+SET\s+STORAGE\s+EXTERNAL\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!setsExternal.IsMatch(text))
                {
                    violations.Add($"{Path.GetRelativePath(projectRoot, seedFile)}: honua.{table}.{column}");
                }
            }
        }

        checkedCreates.Should().BePositive(
            "the test seeds create raster tables; zero matches means the scan is broken, not that there is nothing to guard");
        violations.Should().BeEmpty(
            "a seed that creates a raster table must also apply the migration-owned EXTERNAL storage policy " +
            "(migrations 055 and 063), or the core schema guard fails the next boot. Missing: " +
            string.Join("; ", violations));
    }

    // Delegates to the shared runtime classifier so the architecture gate and the runtime
    // migration-safety enforcement (MigrationSafetyClassifier) share one source of truth.
    private static IReadOnlyList<string> AnalyzePotentiallyBreakingChanges(string sql)
        => MigrationSafetyClassifier.DetectBreakingRules(sql);

    private static IEnumerable<string> EnumerateMigrationFiles()
    {
        var migrationDirectories = GetMigrationDirectories();
        migrationDirectories.Should().OnlyContain(
            directory => Directory.Exists(directory),
            "both numbered migration roots are part of the safety denominator and neither may disappear silently");

        return migrationDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static string[] GetMigrationDirectories()
    {
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        return
        [
            ArchitectureTestHelpers.CombinePath(projectRoot, "src", "Honua.Server", "Migrations"),
            ArchitectureTestHelpers.CombinePath(projectRoot, "src", "Honua.Db", "Postgres", "Migrations")
        ];
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
