// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FileImport.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Type inference for promoting import attributes to typed columns.
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class ImportTypedColumnInferenceTests
{
    [UnitTest]
    [Operation(Operations.Import)]
    [Endpoint("ImportTypedColumnInference.Resolve")]
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
    [Endpoint("ImportTypedColumnInference.Resolve")]
    public void Resolve_IntegralDecimalWithScale_PromotesBigint()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("count", 2.00m);
        inference.Resolve().Single().PostgresType.Should().Be("bigint");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    [Endpoint("ImportTypedColumnInference.Resolve")]
    public void Resolve_IntegerThenFraction_PromotesDouble()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("measure", 2);
        inference.Observe("measure", 2.5);

        inference.Resolve().Single().PostgresType.Should().Be("double precision");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    [Endpoint("ImportTypedColumnInference.Resolve")]
    public void Resolve_BoolThenText_PromotesText()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("flag", true);
        inference.Observe("flag", "sometimes");

        inference.Resolve().Single().PostgresType.Should().Be("text");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    [Endpoint("ImportTypedColumnInference.Resolve")]
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
    [Endpoint("ImportTypedColumnInference.Resolve")]
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
    [Endpoint("ImportTypedColumnInference.Resolve")]
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
    [Endpoint("ImportTypedColumnInference.Resolve")]
    public void Resolve_NestedUnsafeAndReservedNames_StayUnpromoted()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("id", 100);
        inference.Observe("xmin", 7);
        inference.Observe("ctid", "kept");
        inference.Observe("tableoid", 1);
        inference.Observe("a b", "kept");
        inference.Observe("notes", JsonDocument.Parse("{\"kind\":\"park\"}").RootElement);
        inference.Observe("name", "Alpha");

        inference.Resolve().Select(column => column.Name).Should().Equal("name");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    [Endpoint("ImportTypedColumnInference.Resolve")]
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
    [Endpoint("ImportTypedColumnInference.Resolve")]
    public void Resolve_Guid_PromotesText()
    {
        var inference = new ImportTypedColumnInference();
        inference.Observe("token", Guid.Parse("11111111-1111-1111-1111-111111111111"));

        inference.Resolve().Single().PostgresType.Should().Be("text");
    }

    [UnitTest]
    [Operation(Operations.Import)]
    [Endpoint("ImportTypedColumnInference.Resolve")]
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
}
