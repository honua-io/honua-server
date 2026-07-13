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
    [InlineData("ALTER TABLE honua.layers RENAME TO layers_v2;", "rename-table")]
    public void DetectBreakingRules_FlagsEachBreakingPattern(string sql, string expectedRule)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain(expectedRule);
    }

    [Fact]
    public void DetectBreakingRules_TableRename_DoesNotFalselyMatchRenameColumn()
    {
        const string sql = "ALTER TABLE honua.layers RENAME COLUMN legacy_name TO display_name;";

        var rules = MigrationSafetyClassifier.DetectBreakingRules(sql);

        rules.Should().Contain("rename-column");
        rules.Should().NotContain("rename-table");
    }

    [Fact]
    public void DetectBreakingRules_ApostropheInsideLineComment_DoesNotHideRealDrop()
    {
        // A prior regex-order bug stripped '...'-quoted spans before -- line comments, so the
        // apostrophes in these two comments opened a quote span that swallowed the DROP TABLE
        // between them — misclassifying a contract migration as an additive expand change.
        const string sql = """
            -- cleanup, don't ship this without review
            DROP TABLE honua.layers;
            -- the legacy column isn't used anymore
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("drop-table");
    }

    [Fact]
    public void Classify_ApostropheInCommentHidingDrop_IsContractUnannotated()
    {
        const string sql = """
            -- cleanup, don't ship this without review
            DROP TABLE honua.layers;
            -- the legacy column isn't used anymore
            """;

        var result = MigrationSafetyClassifier.Classify("060_drop_layers.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractUnannotated);
        result.IsBreaking.Should().BeTrue();
        result.BreakingRules.Should().Contain("drop-table");
    }

    [Fact]
    public void Classify_TableRenameWithoutMarker_IsContractUnannotated()
    {
        const string sql = "ALTER TABLE honua.layers RENAME TO layers_v2;";

        var result = MigrationSafetyClassifier.Classify("061_rename_layers.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractUnannotated);
        result.BreakingRules.Should().Contain("rename-table");
    }

    [Fact]
    public void DetectBreakingRules_ApostropheInStringLiteral_StillIgnoresCommentedDrop()
    {
        // The lexer must keep string literals and comments independent: a real string literal that
        // contains an apostrophe must not leak, and a DROP TABLE that only appears inside a comment
        // must stay hidden.
        const string sql = """
            INSERT INTO honua.notes (body) VALUES ('it''s fine');
            -- DROP TABLE honua.layers;
            ALTER TABLE honua.layers ADD COLUMN note TEXT;
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
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
    // An empty (or whitespace-only) reason is self-service theater: the marker must bind to a non-empty
    // reviewer/ticket identity, so these do NOT count as annotated (honua-server#2812).
    [InlineData("-- honua:compatibility-review reason=", false)]
    [InlineData("-- honua:compatibility-review reason=   ", false)]
    [InlineData("-- honua:compatibility-review reason= ticket-123", true)]
    public void HasCompatibilityReviewMarker_MatchesMarkerConvention(string sql, bool expected)
    {
        MigrationSafetyClassifier.HasCompatibilityReviewMarker(sql).Should().Be(expected);
    }

    [Fact]
    public void Classify_BreakingWithEmptyReasonMarker_IsContractUnannotated()
    {
        // A DROP COLUMN whose only marker carries an empty reason must fall through to unannotated so the
        // runner fails it closed rather than treating an unreviewed schema-narrowing change as safe.
        const string sql = """
            -- honua:compatibility-review reason=
            ALTER TABLE honua.layers DROP COLUMN legacy_name;
            """;

        var result = MigrationSafetyClassifier.Classify("053_drop_legacy.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractUnannotated);
        result.IsBreaking.Should().BeTrue();
    }

    [Fact]
    public void ComputeContractApprovalNonce_IsStableAndOrderIndependent()
    {
        var a = MigrationSafetyClassifier.ComputeContractApprovalNonce(new[] { "002_drop.sql", "005_rename.sql" });
        var reordered = MigrationSafetyClassifier.ComputeContractApprovalNonce(new[] { "005_rename.sql", "002_drop.sql" });

        a.Should().NotBeNullOrEmpty();
        a.Should().HaveLength(16);
        reordered.Should().Be(a, "the nonce is bound to the set of scripts, not their enumeration order");
    }

    [Fact]
    public void ComputeContractApprovalNonce_DiffersPerScriptSet_AndIsEmptyForNoScripts()
    {
        var first = MigrationSafetyClassifier.ComputeContractApprovalNonce(new[] { "002_drop.sql" });
        var second = MigrationSafetyClassifier.ComputeContractApprovalNonce(new[] { "003_drop.sql" });

        // A stale token minted for one contract migration cannot match a later, different one (one-shot).
        second.Should().NotBe(first);
        MigrationSafetyClassifier.ComputeContractApprovalNonce(Array.Empty<string>()).Should().BeEmpty();
    }
}
