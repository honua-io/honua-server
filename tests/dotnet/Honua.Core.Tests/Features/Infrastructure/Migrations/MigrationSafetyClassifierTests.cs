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
    public void Classify_BreakingWithIdentityBoundMarker_IsContractAnnotated()
    {
        const string sql = """
            -- honua:compatibility-review reviewer=jane.doe ticket=honua-server#2812 reason=legacy_name unused since v2
            ALTER TABLE honua.layers DROP COLUMN legacy_name;
            """;

        var result = MigrationSafetyClassifier.Classify("052_drop_legacy.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractAnnotated);
        result.IsBreaking.Should().BeTrue();
        result.BreakingRules.Should().Contain("drop-column");
    }

    [Fact]
    public void Classify_BreakingWithFreeTextReasonOnlyMarker_IsContractUnannotated()
    {
        // AC (b) #2812: a self-service free-text reason= is no longer sufficient — the marker must be
        // bound to a reviewer AND ticket identity, so an unaccountable self-review fails closed.
        const string sql = """
            -- honua:compatibility-review reason=legacy_name unused since v2; dropped after contract phase
            ALTER TABLE honua.layers DROP COLUMN legacy_name;
            """;

        var result = MigrationSafetyClassifier.Classify("052_drop_legacy.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractUnannotated);
        result.IsBreaking.Should().BeTrue();
    }

    [Fact]
    public void TryParseReviewIdentity_ExtractsReviewerAndTicket()
    {
        const string sql =
            "-- honua:compatibility-review reviewer=jane.doe ticket=honua-server#2812 reason=safe after v2\n" +
            "DROP TABLE honua.layers;";

        MigrationSafetyClassifier.TryParseReviewIdentity(sql, out var identity).Should().BeTrue();
        identity!.Reviewer.Should().Be("jane.doe");
        identity.Ticket.Should().Be("honua-server#2812");
    }

    [Fact]
    public void TryParseReviewIdentity_AcceptsIssueAlias()
    {
        const string sql = "-- honua:compatibility-review reviewer=ops issue=#42 reason=safe";

        MigrationSafetyClassifier.TryParseReviewIdentity(sql, out var identity).Should().BeTrue();
        identity!.Ticket.Should().Be("#42");
    }

    [Theory]
    [InlineData("-- honua:compatibility-review reviewer=jane ticket=#7 reason=safe", true)]
    [InlineData("--   honua:compatibility-review   reviewer = jane   ticket = #7   reason = safe", true)]
    [InlineData("-- HONUA:COMPATIBILITY-REVIEW REVIEWER=jane ISSUE=#7", true)]
    [InlineData("-- honua:compatibility-review reason=safe", false)]
    [InlineData("-- honua:compatibility-review reviewer=jane reason=missing ticket", false)]
    [InlineData("-- honua:compatibility-review ticket=#7 reason=missing reviewer", false)]
    [InlineData("-- honua:compatibility-review (no fields)", false)]
    [InlineData("-- some other comment", false)]
    public void HasCompatibilityReviewMarker_RequiresReviewerAndTicketIdentity(string sql, bool expected)
    {
        MigrationSafetyClassifier.HasCompatibilityReviewMarker(sql).Should().Be(expected);
    }
}
