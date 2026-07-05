// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Core.Tests.Features.Infrastructure.Migrations;

public sealed class MigrationSafetyClassifierTests
{
    [Theory]
    [InlineData("ALTER TABLE honua.layers DROP COLUMN legacy_name;", "drop-column")]
    [InlineData("ALTER TABLE honua.layers RENAME COLUMN legacy_name TO display_name;", "rename-column")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN metadata TYPE TEXT;", "alter-column-type")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN service_name SET NOT NULL;", "set-not-null")]
    [InlineData("DROP TABLE honua.layers;", "drop-table")]
    [InlineData("DROP SCHEMA honua CASCADE;", "drop-schema")]
    [InlineData("DROP SEQUENCE honua.layers_id_seq;", "drop-sequence")]
    public void DetectBreakingRules_FlagsEachBreakingPattern(string sql, string expectedRule)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain(expectedRule);
    }

    [Fact]
    public void DetectBreakingRules_IgnoresAdditiveChanges()
    {
        const string sql = """
            ALTER TABLE honua.layers ADD COLUMN display_name TEXT;
            CREATE INDEX ix_layers_name ON honua.layers (display_name);
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
    }

    [Fact]
    public void DetectBreakingRules_IgnoresStatementsInsideDollarQuotedFunctionBodies()
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

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
    }

    [Fact]
    public void DetectBreakingRules_IgnoresBreakingStatementsInComments()
    {
        const string sql = """
            -- DROP TABLE honua.layers;  (documented, not executed)
            /* ALTER TABLE honua.layers DROP COLUMN legacy; */
            ALTER TABLE honua.layers ADD COLUMN note TEXT;
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
    }

    [Fact]
    public void Classify_AdditiveScript_IsExpand()
    {
        const string sql = "ALTER TABLE honua.layers ADD COLUMN display_name TEXT;";

        var result = MigrationSafetyClassifier.Classify("050_add_display_name.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.Expand);
        result.IsBreaking.Should().BeFalse();
        result.BreakingRules.Should().BeEmpty();
        result.ScriptName.Should().Be("050_add_display_name.sql");
    }

    [Fact]
    public void Classify_BreakingWithoutMarker_IsContractUnannotated()
    {
        const string sql = "ALTER TABLE honua.layers DROP COLUMN legacy_name;";

        var result = MigrationSafetyClassifier.Classify("051_drop_legacy.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractUnannotated);
        result.IsBreaking.Should().BeTrue();
        result.BreakingRules.Should().Contain("drop-column");
    }

    [Fact]
    public void Classify_BreakingWithMarker_IsContractAnnotated()
    {
        const string sql = """
            -- honua:compatibility-review reason=legacy_name unused since v2; dropped after contract phase
            ALTER TABLE honua.layers DROP COLUMN legacy_name;
            """;

        var result = MigrationSafetyClassifier.Classify("052_drop_legacy.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractAnnotated);
        result.IsBreaking.Should().BeTrue();
        result.BreakingRules.Should().Contain("drop-column");
    }

    [Theory]
    [InlineData("-- honua:compatibility-review reason=safe", true)]
    [InlineData("--   honua:compatibility-review    reason = safe", true)]
    [InlineData("-- HONUA:COMPATIBILITY-REVIEW reason=safe", true)]
    [InlineData("-- honua:compatibility-review (no reason)", false)]
    [InlineData("-- some other comment", false)]
    public void HasCompatibilityReviewMarker_MatchesMarkerConvention(string sql, bool expected)
    {
        MigrationSafetyClassifier.HasCompatibilityReviewMarker(sql).Should().Be(expected);
    }
}
