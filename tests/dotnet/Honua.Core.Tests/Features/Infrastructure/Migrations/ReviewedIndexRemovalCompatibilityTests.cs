// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Migrations;

public sealed class ReviewedIndexRemovalCompatibilityTests
{
    private const string Name = "Honua.Server.Migrations.999_RemoveUnusedFeatureIndexes.sql";
    private const int ReaderSchema = 145;

    [UnitTheory]
    [InlineData(Name)]
    [InlineData("999_RemoveUnusedFeatureIndexes.sql")]
    [InlineData("src/Honua.Server/Migrations/999_RemoveUnusedFeatureIndexes.sql")]
    public void Verify_ReviewedRemoval_RefusesContractForOlderReader(string name)
    {
        var sql = ReadReviewedSql();
        var declaration = MigrationReaderCompatibility.Declare(name, sql);

        MigrationSafetyClassifier.Classify(name, sql).IsBreaking.Should().BeTrue();
        declaration.Classification.Should().Be(MigrationReaderCompatibilityClassification.Contract);
        MigrationReaderCompatibility.Verify(declaration, name, ReaderSchema).Accepted.Should().BeFalse();
    }

    [UnitTest]
    public void Verify_ReviewedRemovalWithCrLf_RefusesContract()
    {
        var sql = ReadReviewedSql().Replace("\n", "\r\n", StringComparison.Ordinal);
        var declaration = MigrationReaderCompatibility.Declare(Name, sql);

        MigrationReaderCompatibility.Verify(declaration, Name, ReaderSchema).Accepted.Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("Honua.Server.Migrations.999_Unreviewed.sql")]
    [InlineData("Honua.Server.Migrations.RemoveUnusedFeatureIndexes.sql")]
    [InlineData("Honua.Server.Migrations.999_RemoveUnusedFeatureIndexes.sql.bak")]
    [InlineData("Honua.Server.Migrations.999_RemoveUnusedFeatureIndexes.sql\n")]
    [InlineData("Other.Migrations.999_RemoveUnusedFeatureIndexes.sql")]
    public void Verify_ReviewedTextUnderAnotherName_RefusesForgedExpand(string name)
    {
        AssertForgedExpandIsRefused(name, ReadReviewedSql());
    }

    [UnitTheory]
    [InlineData("\nDROP TABLE honua.features;")]
    [InlineData("\n-- honua:reader-incompatible reason=changed reader contract")]
    [InlineData("\n-- honua:reader-compatible-from schema=soon")]
    [InlineData("\n-- honua:reader-compatible-from schema=999")]
    [InlineData("\n")]
    public void Verify_AppendedText_RefusesForgedExpand(string suffix)
    {
        AssertForgedExpandIsRefused(Name, ReadReviewedSql() + suffix);
    }

    [UnitTest]
    public void Verify_DifferentIndex_RefusesForgedExpand()
    {
        var sql = ReadReviewedSql().Replace("idx_features_attributes_gin", "idx_features_layer_objectid", StringComparison.Ordinal);

        AssertForgedExpandIsRefused(Name, sql);
    }

    [UnitTest]
    public void Verify_ChangedRecordedText_RefusesHashMismatch()
    {
        var declaration = MigrationReaderCompatibility.Declare(Name, ReadReviewedSql()) with
        {
            Classification = MigrationReaderCompatibilityClassification.Expand,
            ScriptText = ReadReviewedSql() + "\nDROP TABLE honua.features;",
        };

        MigrationReaderCompatibility.Verify(declaration, Name, ReaderSchema).RefusalReason.Should()
            .Be("the recorded script text does not match its recorded hash");
    }

    private static void AssertForgedExpandIsRefused(string name, string sql)
    {
        var forged = MigrationReaderCompatibility.Declare(name, sql) with
        {
            Classification = MigrationReaderCompatibilityClassification.Expand,
        };

        MigrationReaderCompatibility.Verify(forged, name, ReaderSchema).Accepted.Should().BeFalse();
    }

    private static string ReadReviewedSql()
    {
        using var stream = typeof(MigrationReaderCompatibility).Assembly
            .GetManifestResourceStream("Honua.Core.RemoveUnusedFeatureIndexes.sql")
            ?? throw new InvalidOperationException("The reviewed index-removal script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
