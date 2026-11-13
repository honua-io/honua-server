// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Migrations;

namespace Honua.Core.Tests.Features.Infrastructure.Migrations;

[Trait("Tier", "Fast")]
public sealed class MigrationSafetyClassifierTests
{
    [Theory]
    [InlineData("'", "DROP/**/TABLE honua.evidence")]
    [InlineData("$sql$", "DROP/**/TABLE honua.evidence")]
    [InlineData("'", "DROP/* outer /* nested */ comment */TABLE honua.evidence")]
    [InlineData("$sql$", "DROP-- comment\nTABLE honua.evidence")]
    [InlineData("$sql$", "SELECT 1 AS \"; DROP/**/TABLE honua.evidence; SELECT 1 AS \"")]
    [InlineData("'", "SELECT 1 AS \"--\"; DROP/**/TABLE honua.evidence")]
    [InlineData("'", "SELECT 1 AS \"/*\"; DROP/**/TABLE honua.evidence")]
    [InlineData("$sql$", "SELECT 1 AS \"--\"; DROP/**/TABLE honua.evidence")]
    [InlineData("$sql$", "SELECT 1 AS \"/*\"; DROP/**/TABLE honua.evidence")]
    public void DetectBreakingRules_ComputedDynamicComments_DetectsDrop(string delimiter, string fragment)
    {
        var sql = $$"""
            DO $body$
            BEGIN
                EXECUTE {{delimiter}}{{fragment}}{{delimiter}} || '';
            END;
            $body$;
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("drop-table");
    }

    [Theory]
    [InlineData("'")]
    [InlineData("$sql$")]
    public void DetectBreakingRules_ComputedDynamicQuotes_DoNotHideTruncate(string delimiter)
    {
        var sql = $$"""
            DO $body$
            BEGIN
                EXECUTE {{delimiter}}SELECT 1 AS {{delimiter}} || chr(34) ||
                    {{delimiter}}CREATE TRIGGER "; TRUNCATE honua.evidence; SELECT 1 AS " BEFORE INSERT ON{{delimiter}} ||
                    chr(34);
            END;
            $body$;
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Theory]
    [InlineData("'")]
    [InlineData("$sql$")]
    public void DetectBreakingRules_DynamicTriggerFragmentsInOneExecute_DoNotHideTruncate(string delimiter)
    {
        var sql = $$"""
            DO $body$
            BEGIN
                EXECUTE {{delimiter}}CREATE TRIGGER "first{{delimiter}} || chr(34) ||
                    {{delimiter}} BEFORE INSERT ON honua.evidence EXECUTE FUNCTION reject();
                      TRUNCATE honua.evidence;
                      CREATE TRIGGER {{delimiter}} || chr(34) ||
                    {{delimiter}}second" BEFORE INSERT ON honua.evidence EXECUTE FUNCTION reject(){{delimiter}};
            END;
            $body$;
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Fact]
    public void DetectBreakingRules_IncompleteDynamicTriggerIdentifiers_DoNotHideTruncate()
    {
        const string sql = """
            DO $$
            BEGIN
                EXECUTE 'CREATE TRIGGER "first' || chr(34) ||
                    ' BEFORE INSERT ON honua.evidence EXECUTE FUNCTION reject()';
                TRUNCATE honua.evidence;
                EXECUTE 'CREATE TRIGGER ' || chr(34) ||
                    'second" BEFORE INSERT ON honua.evidence EXECUTE FUNCTION reject()';
            END;
            $$;
            """;

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Theory]
    [InlineData("CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence FOR EACH STATEMENT EXECUTE FUNCTION reject();")]
    [InlineData("CREATE OR REPLACE TRIGGER guard AFTER INSERT OR TRUNCATE OR DELETE ON honua.evidence EXECUTE FUNCTION reject();")]
    [InlineData("CREATE TRIGGER \"evidence guard\" BEFORE UPDATE OF \"display name\", status OR TRUNCATE ON honua.evidence EXECUTE FUNCTION reject();")]
    [InlineData("CREATE TRIGGER guard BEFORE /* event */ TRUNCATE ON honua.evidence EXECUTE FUNCTION reject();")]
    [InlineData("DO $$ BEGIN CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject(); END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE 'CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject()'; END $$;")]
    public void DetectBreakingRules_TruncateTriggerEvent_IsExpand(string sql)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
    }

    [Theory]
    [InlineData("TRUNCATE TABLE honua.evidence RESTART IDENTITY CASCADE;")]
    [InlineData("CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject(); TRUNCATE honua.evidence;")]
    [InlineData("TRUNCATE honua.evidence; CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject();")]
    [InlineData("CREATE FUNCTION reject() RETURNS trigger AS $$ BEGIN TRUNCATE honua.evidence; RETURN NULL; END $$ LANGUAGE plpgsql; CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject();")]
    [InlineData("DO $$ BEGIN EXECUTE 'CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject(); TRUNCATE honua.evidence'; END $$;")]
    public void DetectBreakingRules_TruncateBesideTriggerDeclaration_IsContract(string sql)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Theory]
    [InlineData("CREATE TABLE \"CREATE TRIGGER \" (id int); TRUNCATE honua.evidence; CREATE TABLE \" BEFORE TRUNCATE ON\" (id int);")]
    [InlineData("CREATE TABLE \"prefix\"\"CREATE TRIGGER \" (id int); TRUNCATE honua.evidence; CREATE TABLE \" BEFORE TRUNCATE ON\" (id int);")]
    [InlineData("CREATE TABLE \"/*\" (id int);\n-- */\nCREATE TABLE \"CREATE TRIGGER \" (id int); TRUNCATE honua.evidence; CREATE TABLE \" BEFORE INSERT ON\" (id int);")]
    [InlineData("CREATE TABLE \"--\" (id int); CREATE TABLE \"CREATE TRIGGER \" (id int);\nTRUNCATE honua.evidence; CREATE TABLE \" BEFORE INSERT ON\" (id int);")]
    [InlineData("CREATE TABLE \"'\" (id int); CREATE TABLE \"CREATE TRIGGER \" (id int); TRUNCATE honua.evidence; CREATE TABLE \" BEFORE INSERT ON\" (id int); -- '")]
    public void DetectBreakingRules_TriggerTextInsideQuotedIdentifiers_DoesNotHideTruncate(string sql)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Theory]
    [InlineData("CREATE DOMAIN ©e AS text; SELECT ©e'\\';\nTRUNCATE honua.evidence;\n-- '")]
    [InlineData("SELECT E'\\'\"--\"';\nTRUNCATE honua.evidence;\n-- '")]
    [InlineData("SELECT e'\\'\"/*\"';\nTRUNCATE honua.evidence;\n-- '")]
    [InlineData("DO E'BEGIN EXECUTE \\'TRUNCATE honua.evidence\\'; END';")]
    public void DetectBreakingRules_EscapeStrings_DoNotHideTruncate(string sql)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Theory]
    [InlineData("SELECT E''\n'\\' \"--\"';\nTRUNCATE honua.evidence;\n-- '")]
    [InlineData("SELECT E'' -- continuation\n'\\' \"--\"';\nTRUNCATE honua.evidence;\n-- '")]
    [InlineData("SELECT E'' /* outer /* nested */\n */ '\\' \"--\"';\nTRUNCATE honua.evidence;\n-- '")]
    [InlineData("DO $$ BEGIN EXECUTE E'TRUNCA\\x54E honua.evidence'; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE E'TRUNCA\\124E honua.evidence'; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE E'TRUNCA\\u0054E honua.evidence'; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE E'TRUNCA\\U00000054E honua.evidence'; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE E'TRUNCA'\n'\\x54E honua.evidence'; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE 'TRUNCA'\n'TE honua.evidence'; END $$;")]
    public void DetectBreakingRules_ContinuedAndNumericEscapeStrings_DoNotHideTruncate(string sql)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain("truncate-table");
    }

    [Fact]
    public void DetectBreakingRules_EscapedInertString_IsExpand()
    {
        const string sql = "SELECT E'\\'\"--\" TRUNCATE honua.evidence';";

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
    }

    [Fact]
    public void DetectBreakingRules_TriggerDeclaration_DoesNotHideOtherDestructiveRules()
    {
        const string sql = "CREATE TRIGGER guard BEFORE TRUNCATE ON honua.evidence EXECUTE FUNCTION reject(); DROP TABLE honua.evidence;";

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().ContainSingle().Which.Should().Be("drop-table");
    }

    [Fact]
    public void DetectBreakingRules_DoesNotTreatDbUpVariableAsDollarQuotedBody()
    {
        const string sql = "SELECT $HonuaSchema$; ALTER TABLE honua.alert_dispatch DROP COLUMN legacy_value;";

        MigrationSafetyClassifier.DetectBreakingRules(sql)
            .Should()
            .Contain("drop-column");
    }
    [Theory]
    [InlineData("ALTER TABLE honua.layers DROP COLUMN legacy_name;", "drop-column")]
    [InlineData("ALTER TABLE honua.layers RENAME COLUMN legacy_name TO display_name;", "rename-column")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN metadata TYPE TEXT;", "alter-column-type")]
    [InlineData("ALTER TABLE honua.layers ALTER COLUMN service_name SET NOT NULL;", "set-not-null")]
    [InlineData("DROP TABLE honua.layers;", "drop-table")]
    [InlineData("ALTER FOREIGN TABLE honua.remote_layers DROP COLUMN name;", "drop-column")]
    [InlineData("DROP VIEW honua.layer_summary;", "drop-view")]
    [InlineData("DROP MATERIALIZED VIEW honua.cached_layers;", "drop-view")]
    [InlineData("DROP TYPE honua.layer_kind;", "drop-type")]
    [InlineData("DROP DOMAIN honua.layer_id;", "drop-type")]
    [InlineData("DROP FUNCTION honua.read_layer(integer);", "drop-routine")]
    [InlineData("DROP PROCEDURE honua.update_layer(integer);", "drop-routine")]
    [InlineData("DROP TRIGGER changes ON honua.features;", "drop-trigger")]
    [InlineData("DROP POLICY tenant_scope ON honua.features;", "drop-policy")]
    [InlineData("DROP INDEX honua.layer_name_idx;", "drop-index")]
    [InlineData("DROP EXTENSION postgis CASCADE;", "drop-other-object")]
    [InlineData("ALTER VIEW honua.layers RENAME TO renamed;", "rename-object")]
    [InlineData("ALTER TABLE honua.layers SET SCHEMA archived;", "move-object-schema")]
    [InlineData("TRUNCATE honua.features;", "truncate-table")]
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
    public void DetectBreakingRules_FlagsStatementsInsideDollarQuotedFunctionBodies()
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

        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEquivalentTo(
            ["rename-column", "drop-table"],
            options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("DO $$ BEGIN DROP TABLE honua.layers; END $$;", "drop-table")]
    [InlineData("CREATE PROCEDURE honua.cleanup() LANGUAGE plpgsql AS $body$ BEGIN ALTER TABLE honua.layers RENAME TO archived_layers; END $body$;", "rename-table")]
    [InlineData("CREATE FUNCTION honua.cleanup() RETURNS void LANGUAGE plpgsql AS 'BEGIN EXECUTE ''DROP TABLE honua.layers''; END';", "drop-table")]
    [InlineData("DO $body$ BEGIN EXECUTE $sql$ALTER TABLE honua.layers DROP COLUMN legacy_name$sql$; END $body$;", "drop-column")]
    [InlineData("DO $$ BEGIN EXECUTE 'ALTER ' || 'TABLE honua.layers RENAME TO archived_layers'; END $$;", "rename-table")]
    public void DetectBreakingRules_FlagsExecutableRoutineAndDynamicSqlBodies(string sql, string expectedRule)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().Contain(expectedRule);
    }

    [Theory]
    [InlineData("SELECT 'DROP TABLE honua.layers';")]
    [InlineData("SELECT $$ALTER TABLE honua.layers DROP COLUMN legacy_name$$;")]
    [InlineData("DO $$ BEGIN message := 'DROP TABLE honua.layers'; END $$;")]
    [InlineData("DO $$ BEGIN PERFORM format('DROP TABLE honua.layers'); END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE format('SELECT %L', 'DROP TABLE honua.layers'); END $$;")]
    [InlineData("DO $$ BEGIN -- DROP TABLE honua.layers;\n NULL; END $$;")]
    [InlineData("DO $$ BEGIN /* ALTER TABLE honua.layers DROP COLUMN legacy_name; */ NULL; END $$;")]
    [InlineData("DO $$ BEGIN /* outer /* DROP TABLE honua.layers; */ still outer */ NULL; END $$;")]
    public void DetectBreakingRules_IgnoresCommentsAndInertLiterals(string sql)
    {
        MigrationSafetyClassifier.DetectBreakingRules(sql).Should().BeEmpty();
    }

    [Fact]
    public void Classify_DeclaredContractPhaseAroundDynamicDdl_IsContractAnnotated()
    {
        const string sql = """
            -- honua:migration-phase contract
            -- honua:compatibility-review reason=#3899 requires old nodes drained before schema adoption
            DO $$
            BEGIN
                EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', 'sta_thing', 'custom');
            END
            $$;
            """;

        var result = MigrationSafetyClassifier.Classify("109_adopt_schema.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractAnnotated);
        result.IsBreaking.Should().BeTrue();
        result.BreakingRules.Should().BeEquivalentTo(["declared-contract-phase", "move-object-schema"]);
    }

    [Fact]
    public void Classify_DeclaredContractPhaseWithoutReviewReason_IsContractUnannotated()
    {
        const string sql = """
            -- honua:migration-phase contract
            DO $$
            BEGIN
                EXECUTE format('ALTER TABLE %I.%I SET SCHEMA %I', 'honua', 'sta_thing', 'custom');
            END
            $$;
            """;

        var result = MigrationSafetyClassifier.Classify("109_adopt_schema.sql", sql);

        result.Classification.Should().Be(MigrationSafetyClassification.ContractUnannotated);
        result.BreakingRules.Should().BeEquivalentTo(["declared-contract-phase", "move-object-schema"]);
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
