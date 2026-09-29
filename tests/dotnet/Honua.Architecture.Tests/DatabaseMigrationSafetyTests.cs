// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class DatabaseMigrationSafetyTests
{
    [ArchitectureTest]
    public void MigrationScripts_AfterReaderBaseline_MustRemainExpandOnly()
    {
        var root = FindProjectRoot(Directory.GetCurrentDirectory());
        var baselineJson = File.ReadAllText(Path.Combine(root, "certification", "schema-reader-baseline.json"));
        MigrationHash(baselineJson).Should().Be("4534DC4F439A70DCE9A50F2DA0C82EDBB1F0A551BC9BFE1E4AE44EF6F2B8A33B",
            "the reader baseline is frozen at 977a1c630; new migrations belong in the ongoing ledger, not this review record");
        var baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(baselineJson)!;
        var scripts = EnumerateMigrationFiles().ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText);
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "certification", "schema-migration-hashes.json")))!;
        RollingUpgradeViolations(scripts, baseline, hashes).Should().BeEmpty(
            "the supported rolling update retains the previous reader's schema; a review annotation does not permit a contraction");
    }

    [Theory]
    [InlineData("ALTER TABLE honua.layers DROP COLUMN name;")]
    [InlineData("-- honua:compatibility-review reason=reviewed\nALTER TABLE honua.layers DROP COLUMN name;")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN name SET NOT NULL;")]
    [InlineData("DROP VIEW honua.layer_summary;")]
    [InlineData("DROP TYPE honua.layer_kind;")]
    [InlineData("DROP MATERIALIZED VIEW honua.cached_layers;")]
    [InlineData("DROP FUNCTION honua.read_layer(integer);")]
    [InlineData("DROP TRIGGER track_changes ON honua.features;")]
    [InlineData("DROP POLICY tenant_scope ON honua.features;")]
    [InlineData("ALTER VIEW honua.layer_summary RENAME TO renamed;")]
    [InlineData("DROP INDEX honua.layer_name_idx;")]
    [InlineData("DROP EXTENSION postgis CASCADE;")]
    [InlineData("ALTER TABLE honua.features DROP legacy_name;")]
    [InlineData("TRUNCATE honua.features;")]
    public void ExpandOnlyGate_SeededContractMigration_IsRejectedEvenWhenReviewed(string sql)
    {
        RollingUpgradeViolations(new Dictionary<string, string> { ["new.sql"] = sql }, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["new.sql"] = MigrationHash(sql) })
            .Should().ContainSingle().Which.Should().Contain("new.sql");
    }

    [Fact]
    public void ExpandOnlyGate_AdditiveMigration_IsAccepted()
    {
        const string sql = "ALTER TABLE honua.layers ADD COLUMN note text;";
        RollingUpgradeViolations(new Dictionary<string, string> { ["new.sql"] = sql },
            new Dictionary<string, string>(), new Dictionary<string, string> { ["new.sql"] = MigrationHash(sql) })
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandOnlyGate_ChangedOrDeletedBaselineMigration_IsRejected(bool deleted)
    {
        const string original = "CREATE TABLE fixture (id integer);";
        var baseline = new Dictionary<string, string>
        {
            ["001.sql"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original)))
        };
        var proposed = new Dictionary<string, string>();
        if (!deleted)
        {
            proposed["001.sql"] = "CREATE TABLE fixture (id text);";
        }

        RollingUpgradeViolations(proposed, baseline).Should().ContainSingle()
            .Which.Should().Contain("001.sql");
    }

    [Fact]
    public void ExpandOnlyGate_UnpinnedAdditiveMigration_IsRejected()
    {
        RollingUpgradeViolations(new Dictionary<string, string> { ["new.sql"] = "CREATE TABLE future (id integer);" },
            new Dictionary<string, string>()).Should().ContainSingle().Which.Should().Contain("hash-pinned");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpandOnlyGate_MigrationAddedAfterReaderBaseline_CannotBeChangedOrDeleted(bool deleted)
    {
        const string original = "CREATE TABLE future (id integer);";
        var hashes = new Dictionary<string, string> { ["new.sql"] = MigrationHash(original) };
        var scripts = new Dictionary<string, string>();
        if (!deleted)
        {
            scripts["new.sql"] = "CREATE TABLE future (id text);";
        }
        RollingUpgradeViolations(scripts, new Dictionary<string, string>(), hashes)
            .Should().ContainSingle().Which.Should().Contain("new.sql");
    }

    private static string MigrationHash(string sql)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    [Fact]
    public void MigrationClassifier_HistoricalTriggerReplacement_RequiresExactFrozenNameAndHash()
    {
        var root = FindProjectRoot(Directory.GetCurrentDirectory());
        const string name = "003_CreateRelationshipsTable.sql";
        var sql = File.ReadAllText(Path.Combine(root, "src", "Honua.Server", "Migrations", name));
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

    private static IEnumerable<string> RollingUpgradeViolations(
        Dictionary<string, string> scripts, Dictionary<string, string> baseline,
        Dictionary<string, string>? hashes = null)
    {
        hashes ??= baseline;
        foreach (var (name, hash) in baseline)
        {
            if (!scripts.TryGetValue(name, out var sql) || MigrationHash(sql) != hash)
            {
                yield return $"{name}: an existing reader-baseline migration was changed or deleted";
            }
        }

        foreach (var (name, hash) in hashes)
        {
            if (baseline.TryGetValue(name, out var baselineHash))
            {
                if (hash != baselineHash)
                {
                    yield return $"{name}: the frozen reader-baseline hash cannot change";
                }
            }
            else if (!scripts.TryGetValue(name, out var sql) || MigrationHash(sql) != hash)
            {
                yield return $"{name}: a hash-pinned migration was changed or deleted";
            }
        }

        foreach (var (name, sql) in scripts)
        {
            if (!hashes.ContainsKey(name))
            {
                yield return $"{name}: every migration must be hash-pinned in schema-migration-hashes.json";
            }
            if (!baseline.ContainsKey(name) && MigrationSafetyClassifier.Classify(name, sql).IsBreaking)
            {
                yield return $"{name}: contracting migrations cannot ship in a rolling update";
            }
        }
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
        // provider migrations from running (honua-server#4889).
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
