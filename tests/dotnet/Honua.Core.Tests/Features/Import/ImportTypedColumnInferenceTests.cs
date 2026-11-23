// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Import;

[Protocol(ProtocolNames.TestQuality)]
public sealed class ImportTypedColumnInferenceTests
{
    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_Integers_PromotesBigint()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("population", 2);
        inference.Observe("population", 10L);

        inference.Resolve().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Name = "population", PostgresType = "bigint" });
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_IntegralDecimalWithScale_PromotesBigint()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("count", 2.00m);
        inference.Resolve().Single().PostgresType.Should().Be("bigint");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_IntegerThenFraction_PromotesDouble()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("measure", 2);
        inference.Observe("measure", 2.5);

        inference.Resolve().Single().PostgresType.Should().Be("double precision");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_BoolThenText_PromotesText()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("flag", true);
        inference.Observe("flag", "sometimes");

        inference.Resolve().Single().PostgresType.Should().Be("text");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_StringScalars_UsesTheNarrowestMatchingType()
    {
        var dates = new ImportTypedColumnInference();
        dates.Observe("founded", "2020-01-15");
        dates.Resolve().Single().PostgresType.Should().Be("date");

        var timestamps = new ImportTypedColumnInference();
        timestamps.Observe("observed", "2020-01-15T12:00:00Z");
        timestamps.Resolve().Single().PostgresType.Should().Be("timestamp with time zone");

        var numbers = new ImportTypedColumnInference();
        numbers.Observe("population", "10");
        numbers.Resolve().Single().PostgresType.Should().Be("bigint");

        var mixed = new ImportTypedColumnInference();
        mixed.Observe("label", "10");
        mixed.Observe("label", "ten");
        mixed.Resolve().Single().PostgresType.Should().Be("text");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_ExplicitStringScalars_PreservesTextAndRecognizesDates()
    {
        var inference = new ImportTypedColumnInference(preserveStringScalars: true);
        inference.Observe("source_id", "001");
        inference.Observe("label", "true");
        inference.Observe("opened", "2020-01-15");

        inference.Resolve().Select(column => (column.Name, column.PostgresType)).Should().BeEquivalentTo(
        [
            ("source_id", "text"),
            ("label", "text"),
            ("opened", "date")
        ]);
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_TypedFormatTextThatLooksLikeDate_PreservesText()
    {
        var inference = new ImportTypedColumnInference(preserveStringScalars: true, inferTemporalStrings: false);
        inference.Observe("label", "2020-01-15");
        inference.Resolve().Single().PostgresType.Should().Be("text");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_CaseCollidingNames_KeepsBothOutOfTypedColumns()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("label", "first");
        inference.Observe("LABEL", "second");
        inference.Observe("label", "third");

        inference.Resolve().Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_NestedUnsafeAndReservedNames_StayUnpromoted()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("id", 100);
        inference.Observe("xmin", 7);
        inference.Observe("ctid", "kept");
        inference.Observe("tableoid", 1);
        inference.Observe("a b", "kept");
        inference.Observe("notes", JsonElement.Parse("{\"kind\":\"park\"}"));
        inference.Observe("name", "Alpha");

        inference.Resolve().Select(column => column.Name).Should().Equal("name");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_NullThenValue_UsesTheValueType()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("population", null);
        inference.Observe("population", "");
        inference.Observe("population", 4);

        inference.Resolve().Single().PostgresType.Should().Be("bigint");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_Guid_PromotesText()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("token", Guid.Parse("11111111-1111-1111-1111-111111111111"));

        inference.Resolve().Single().PostgresType.Should().Be("text");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_PastTheColumnCap_KeepsTheFirstSeenNames()
    {
        var inference = new ImportTypedColumnInference();
        for (var index = 0; index < ImportTypedColumnInference.MaxColumns + 1; index++)
        {
            inference.Observe($"c{index}", index);
        }

        var names = inference.Resolve().Select(column => column.Name).ToArray();
        names.Should().HaveCount(ImportTypedColumnInference.MaxColumns);
        names.Should().Contain("c0");
        names.Should().NotContain($"c{ImportTypedColumnInference.MaxColumns}");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_UnsafeNames_ReportsEachDistinctNameOnce()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("名前", "a");
        inference.Observe("名前", "b");
        inference.Observe("a b", "kept");
        inference.Observe("xmin", 7);
        inference.Observe("name", "Alpha");

        var warnings = inference.SkippedNameWarnings();

        warnings.Should().HaveCount(3);
        warnings[0].Should().Contain("'名前'").And.Contain("hidden in published features");
        warnings[1].Should().Contain("'a b'");
        warnings[2].Should().Contain("'xmin'").And.Contain("system column");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_LongNameWithSurrogatePair_KeepsWholeCharacters()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe(new string('a', 79) + "😀tail", "v");

        var warning = inference.SkippedNameWarnings().Should().ContainSingle().Subject;

        warning.Should().Contain("'" + new string('a', 79) + "…'");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_ManyUnsafeNames_ListsTheFirstAndSummarisesTheRest()
    {
        var inference = new ImportTypedColumnInference();
        for (var index = 0; index < ImportTypedColumnInference.MaxReportedNames + 3; index++)
        {
            inference.Observe($"é{index}", index);
        }

        var warnings = inference.SkippedNameWarnings();

        warnings.Should().HaveCount(ImportTypedColumnInference.MaxReportedNames + 1);
        warnings[^1].Should().StartWith("More source properties whose names cannot be column names: 3.");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_PastTheColumnCap_ReportsTheOverflow()
    {
        var inference = new ImportTypedColumnInference();
        for (var index = 0; index < ImportTypedColumnInference.MaxColumns + 2; index++)
        {
            inference.Observe($"c{index}", index);
            inference.Observe($"c{index}", index);
        }

        inference.SkippedNameWarnings().Should().ContainSingle()
            .Which.Should().StartWith("Source properties not published as columns (past the 128-column limit or refused by the existing table): 2,")
            .And.Contain("'c128', 'c129'");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_SafeNamesOnly_ReturnsNone()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("id", 1);
        inference.Observe("geometry", "kept");
        inference.Observe("name", "Alpha");

        inference.SkippedNameWarnings().Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void Resolve_NameWithTrailingLineFeed_StaysUnpromotedAndIsReported()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("abc\n", "kept");

        inference.Resolve().Should().BeEmpty();
        inference.SkippedNameWarnings().Should().ContainSingle()
            .Which.Should().Contain("column names use ASCII letters");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_CaseCollision_ReportsEveryVariantOnce()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("Name", "a");
        inference.Observe("name", "b");
        inference.Observe("NAME", "c");
        inference.Observe("name", "d");

        inference.SkippedNameWarnings().Should().ContainSingle()
            .Which.Should().StartWith("Source properties 'Name' and 'name' and 'NAME' differ only in letter case");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_NestedValue_ReportsTheKey()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("notes", "plain");
        inference.Observe("notes", JsonElement.Parse("{\"kind\":\"park\"}"));
        inference.Observe("name", "Alpha");

        inference.SkippedNameWarnings().Should().ContainSingle()
            .Which.Should().StartWith("Source property 'notes' holds values that cannot be stored in a single typed column");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_ManyCaseVariants_ListsAFewSpellings()
    {
        var inference = new ImportTypedColumnInference();
        foreach (var spelling in new[] { "abc", "Abc", "aBc", "abC", "ABc", "AbC", "aBC", "ABC" })
        {
            inference.Observe(spelling, "v");
        }

        inference.SkippedNameWarnings().Should().ContainSingle()
            .Which.Should().StartWith("Source properties 'abc' and 'Abc' and 'aBc' and 'abC' and 'ABc' and other spellings differ");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    public void SkippedNameWarnings_WithPromotedColumns_ReportsOnlyTheColumnsTheTableDoesNotPublish()
    {
        var inference = new ImportTypedColumnInference();
        for (var index = 0; index < ImportTypedColumnInference.MaxColumns + 1; index++)
        {
            inference.Observe($"c{index}", index);
        }

        // The table already has c128; it refused c5, and c3 clashes by case with an existing C3.
        var promoted = Enumerable.Range(0, ImportTypedColumnInference.MaxColumns + 1)
            .Where(index => index != 5)
            .Select(index => new ImportTypedColumn { Name = index == 3 ? "C3" : $"c{index}", PostgresType = "bigint" })
            .ToArray();

        inference.SkippedNameWarnings(promoted).Should().ContainSingle()
            .Which.Should().Contain("existing table): 2, including 'c3', 'c5'.");
    }
}
