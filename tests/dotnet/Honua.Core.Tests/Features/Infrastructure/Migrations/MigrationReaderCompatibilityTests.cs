// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Migrations;

/// <summary>
/// Acceptance and refusal matrix for forward-compatibility declarations (#6477): an older reader
/// accepts an unknown journal entry only when it re-derives an expand-only declaration itself.
/// </summary>
public sealed class MigrationReaderCompatibilityTests
{
    private const string Name = "Honua.Server.Migrations.140_AddFutureNote.sql";
    private const string Additive = "ALTER TABLE honua.layers ADD COLUMN IF NOT EXISTS future_note text;";
    private const int Reader = 135;

    [UnitTest]
    public void Declare_AdditiveScript_IsExpandFromAnyReader()
    {
        var declaration = MigrationReaderCompatibility.Declare(Name, Additive);

        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.Expand);
        declaration.MinReaderSchema.Should().Be(0);
        declaration.DeclarationFormat.Should().Be(MigrationReaderCompatibilityDeclaration.CurrentFormat);
        declaration.ScriptSha256.Should().Be(MigrationReaderCompatibility.ComputeScriptSha256(Additive));
        declaration.ScriptText.Should().Be(Additive);
        MigrationReaderCompatibility.Verify(declaration, Name, Reader).Accepted.Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("ALTER TABLE honua.layers DROP COLUMN legacy_name;")]
    [InlineData("-- honua:compatibility-review reason=reviewed\nALTER TABLE honua.layers RENAME COLUMN a TO b;")]
    [InlineData("DROP TYPE honua.layer_kind;")]
    [InlineData("-- honua:migration-phase contract\nALTER TABLE honua.layers ADD COLUMN x text;")]
    public void Declare_ContractScript_IsRefusedEvenWhenReviewed(string sql)
    {
        var declaration = MigrationReaderCompatibility.Declare(Name, sql);

        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.Contract);
        MigrationReaderCompatibility.Verify(declaration, Name, Reader).RefusalReason.Should().Contain("declared contract");
    }

    [UnitTheory]
    [InlineData("-- honua:reader-incompatible reason=older readers reject the new enum value\nALTER TYPE honua.k ADD VALUE 'x';")]
    [InlineData("  --honua:READER-INCOMPATIBLE\nCREATE TABLE honua.t (id int);")]
    [InlineData("CREATE TABLE honua.t (id int);\n-- honua:reader-compatible-from schema=soon")]
    [InlineData("CREATE TABLE honua.t (id int);\n-- honua:reader-compatible-from")]
    public void Declare_OptedOutOrMalformedAnnotation_IsReaderIncompatible(string sql)
    {
        var declaration = MigrationReaderCompatibility.Declare(Name, sql);

        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.ReaderIncompatible);
        MigrationReaderCompatibility.Verify(declaration, Name, Reader).Accepted.Should().BeFalse();
    }

    [UnitTest]
    public void Declare_ReaderCompatibleFrom_RaisesTheFloorToTheHighestAnnotation()
    {
        const string sql = "-- honua:reader-compatible-from schema=130\n-- honua:reader-compatible-from schema=137\n" + Additive;
        var declaration = MigrationReaderCompatibility.Declare(Name, sql);

        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.Expand);
        declaration.MinReaderSchema.Should().Be(137);
        MigrationReaderCompatibility.Verify(declaration, Name, 137).Accepted.Should().BeTrue();
        MigrationReaderCompatibility.Verify(declaration, Name, 136).RefusalReason.Should()
            .Contain("reader schema 137").And.Contain("schema 136");
    }

    [UnitTest]
    public void Declare_CrLfScript_HashesAndRecordsLfText()
    {
        var crlf = MigrationReaderCompatibility.Declare(Name, "CREATE TABLE honua.t (id int);\r\nCREATE INDEX i ON honua.t (id);");
        var lf = MigrationReaderCompatibility.Declare(Name, "CREATE TABLE honua.t (id int);\nCREATE INDEX i ON honua.t (id);");

        crlf.Should().Be(lf);
    }

    [UnitTest]
    public void Verify_ForgedExpandClaimOverContractText_IsRefusedByTheReadersOwnClassifier()
    {
        const string sql = "ALTER TABLE honua.layers RENAME COLUMN legacy_name TO display_name;";
        var forged = MigrationReaderCompatibility.Declare(Name, sql) with
        {
            Classification = MigrationReaderCompatibilityClassification.Expand,
        };

        MigrationReaderCompatibility.Verify(forged, Name, Reader).RefusalReason.Should()
            .Be("this reader classifies the recorded script as contract");
    }

    [UnitTest]
    public void Verify_ForgedExpandClaimOverOptedOutText_IsRefused()
    {
        var forged = MigrationReaderCompatibility.Declare(Name, "-- honua:reader-incompatible reason=trigger semantics\n" + Additive) with
        {
            Classification = MigrationReaderCompatibilityClassification.Expand,
        };

        MigrationReaderCompatibility.Verify(forged, Name, Reader).RefusalReason.Should()
            .Be("this reader classifies the recorded script as reader-incompatible");
    }

    [UnitTest]
    public void Verify_TextChangedAfterHashing_IsRefused()
    {
        var tampered = MigrationReaderCompatibility.Declare(Name, Additive) with
        {
            ScriptText = Additive + "\nALTER TABLE honua.layers DROP COLUMN name;",
        };

        MigrationReaderCompatibility.Verify(tampered, Name, Reader).RefusalReason.Should()
            .Be("the recorded script text does not match its recorded hash");
    }

    [UnitTest]
    public void Verify_LoweredFloorThatDisagreesWithTheRecordedAnnotation_IsRefused()
    {
        var lowered = MigrationReaderCompatibility.Declare(Name, "-- honua:reader-compatible-from schema=150\n" + Additive) with
        {
            MinReaderSchema = 0,
        };

        MigrationReaderCompatibility.Verify(lowered, Name, Reader).RefusalReason.Should()
            .Contain("disagrees with the recorded script's annotation");
    }

    [UnitTest]
    public void Verify_DeclarationForAnotherScript_IsRefused()
        => MigrationReaderCompatibility.Verify(MigrationReaderCompatibility.Declare(Name, Additive), Name + ".other", Reader)
            .RefusalReason.Should().Be("the declaration names a different script");

    [UnitTheory]
    [InlineData(0)]
    [InlineData(2)]
    public void Verify_UnsupportedDeclarationFormat_IsRefused(int format)
    {
        var declaration = MigrationReaderCompatibility.Declare(Name, Additive) with { DeclarationFormat = format };

        MigrationReaderCompatibility.Verify(declaration, Name, Reader).RefusalReason.Should().Contain("not supported");
    }

    [UnitTheory]
    [InlineData("Honua.Server.Migrations.135_RecordMigrationReaderCompatibility.sql", 135)]
    [InlineData("Honua.Postgres.Migrations.009_X.sql", 9)]
    [InlineData("src/Honua.Server/Migrations/001_CreateHonuaSchema.sql", 1)]
    [InlineData("honua_synthetic.002_expand.sql", 2)]
    public void ReadSchemaNumber_ParsesTheMigrationNumber(string name, int expected)
        => MigrationReaderCompatibility.ReadSchemaNumber(name).Should().Be(expected);

    [UnitTest]
    public void ReaderSchemaOf_IsTheHighestNumberedScript()
        => MigrationReaderCompatibility.ReaderSchemaOf(
            ["a.001_x.sql", "a.128_y.sql", "a.notes.sql", "a.009_z.sql"]).Should().Be(128);

    [UnitTest]
    public void CompatibilityException_WithoutDeclarations_KeepsTheOriginalMessage()
    {
        var withoutDeclarations = new DatabaseSchemaCompatibilityException([Name]);
        var withEmptyRefusals = new DatabaseSchemaCompatibilityException([Name], new Dictionary<string, string>());

        withoutDeclarations.Message.Should().Be(
            $"schema_reader_incompatible: this server cannot verify compatibility with applied migration(s): {Name}. " +
            "Start a compatible server or restore the database backup matching this revision. " +
            "Application rollback does not reverse database migrations.");
        withEmptyRefusals.Message.Should().Be(withoutDeclarations.Message);
        withoutDeclarations.DeclarationRefusals.Should().BeEmpty();
    }

    [UnitTest]
    public void CompatibilityException_WithRefusedDeclaration_NamesTheReason()
    {
        var exception = new DatabaseSchemaCompatibilityException(
            [Name], new Dictionary<string, string> { [Name] = "declared contract, not expand" });

        exception.Message.Should().StartWith(DatabaseSchemaCompatibilityException.ErrorCode)
            .And.EndWith($"Forward-compatibility declaration(s) refused: {Name} (declared contract, not expand).");
        exception.DeclarationRefusals.Should().ContainKey(Name);
    }
}
