// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec.Domain;
using Honua.Server.Features.Grounding.Spec;

namespace Honua.Server.Tests.Features.Grounding.Spec;

public sealed class SpecGroundingServiceTests
{
    [Fact]
    public async Task Mutate_WithAmbiguousDataset_ReturnsPickDatasetClarification()
    {
        using var harness = CreateHarness(
            SpecGroundingTestSupport.CreateLayer(1, "Hospitals North"),
            SpecGroundingTestSupport.CreateLayer(2, "Hospitals South"));

        var result = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            "use hospitals as hospitals",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().Be(SpecGroundingErrorKind.Ambiguous);
        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.AmbiguousDataset);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("dataset.selection");
        result.Clarification.CandidatesByQuestionId["dataset.selection"]
            .Should().OnlyContain(candidate => candidate.CandidateType == "dataset");
    }

    [Fact]
    public async Task Mutate_WithAmbiguousColumn_ReturnsPickColumnClarification()
    {
        using var harness = CreateHarness(
            SpecGroundingTestSupport.CreateLayer(
                1,
                "Parcels",
                fields:
                [
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("status_code", FieldType.String, Length: 32),
                    new FieldDefinition("status_label", FieldType.String, Length: 64)
                ]));

        var result = await harness.Service.MutateAsync(
            harness.Parse(
                """
                grammar "v1.0"
                source parcels { type = "layer", ref = "catalog:layer:1" }
                """),
            "only status",
            new SpecGroundingContext(TargetId: "parcels"),
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.AmbiguousColumn);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("column.selection");
        result.Clarification.CandidatesByQuestionId["column.selection"]
            .Should().OnlyContain(candidate => candidate.CandidateType == "column");
    }

    [Fact]
    public async Task Mutate_WithAmbiguousFilterValue_ReturnsPickValueClarification()
    {
        using var harness = CreateHarness(
            SpecGroundingTestSupport.CreateLayer(
                1,
                "Parcels",
                fields:
                [
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("zone", FieldType.String, Length: 16)
                ]));

        var result = await harness.Service.MutateAsync(
            harness.Parse(
                """
                grammar "v1.0"
                source parcels { type = "layer", ref = "catalog:layer:1" }
                """),
            "only AE or VE",
            new SpecGroundingContext(TargetId: "parcels"),
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.AmbiguousFilterValue);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("value.selection");
        result.Clarification.CandidatesByQuestionId["value.selection"]
            .Should().OnlyContain(candidate => candidate.CandidateType == "value");
    }

    [Fact]
    public async Task Mutate_WithMissingUnit_ReturnsSpecifyUnitClarification()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Zones"));

        var result = await harness.Service.MutateAsync(
            harness.Parse(
                """
                grammar "v1.0"
                source zones { type = "layer", ref = "catalog:layer:1" }
                """),
            "buffer zones by 500 in EPSG:3857 as zone_buffer",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.AmbiguousUnit);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("unit.selection");
        result.Clarification.CandidatesByQuestionId["unit.selection"]
            .Should().OnlyContain(candidate => candidate.CandidateType == "unit");
    }

    [Fact]
    public async Task Mutate_WithMissingCrs_ReturnsSpecifyCrsClarification()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Zones"));

        var result = await harness.Service.MutateAsync(
            harness.Parse(
                """
                grammar "v1.0"
                source zones { type = "layer", ref = "catalog:layer:1" }
                """),
            "buffer zones by 500.m as zone_buffer",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.AmbiguousCrs);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("crs.selection");
        result.Clarification.CandidatesByQuestionId["crs.selection"]
            .Should().OnlyContain(candidate => candidate.CandidateType == "crs");
    }

    [Fact]
    public async Task Mutate_WithNearPhrase_ReturnsChooseOpClarification()
    {
        using var harness = CreateHarness();

        var result = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            "schools near hospitals",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.AmbiguousProcess);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("operator.selection");
        result.Clarification.CandidatesByQuestionId["operator.selection"]
            .Should().OnlyContain(candidate => candidate.CandidateType == "operator");
    }

    [Fact]
    public async Task Mutate_WithHeavyOperation_ReturnsConfirmHeavyOpClarification()
    {
        using var harness = CreateHarness();

        var result = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            "run zonal stats",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.Clarification.Should().NotBeNull();
        result.Clarification!.Request.ReasonCodes.Should().Contain(ClarificationReasonCode.HeavyOperationConfirmation);
        result.Clarification.Request.Questions[0].QuestionId.Should().Be("heavy.confirm");
        result.Clarification.Request.Questions[0].Kind.Should().Be(ClarificationQuestionKind.Confirmation);
    }

    [Fact]
    public async Task Mutate_ComputeOnlyChange_PreservesUntouchedCanonicalSectionsByteForByte()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Rivers"));
        var currentSpec = harness.Parse(
            """
            grammar "v1.0"
            source rivers { type = "layer", ref = "catalog:layer:1" }
            scope {
              target = @rivers
              where  = cql2("state = 'HI'")
            }
            map {
              layers = ["rivers"]
            }
            output rivers_out { expr = @rivers }
            """);
        var currentJson = harness.ToCanonicalJson(currentSpec);

        var result = await harness.Service.MutateAsync(
            currentSpec,
            "buffer rivers by 500.m in EPSG:3857 as river_buffer",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().BeNull();
        result.Mutation.Should().NotBeNull();
        result.Mutation!.SectionsTouched.Should().Equal("compute");
        result.Mutation.SectionsPreserved.Should().Contain(["sources", "scope", "map", "outputs"]);

        using var before = JsonDocument.Parse(currentJson);
        using var after = JsonDocument.Parse(result.Mutation.NextSpecCanonicalJson);

        foreach (var propertyName in new[] { "sources", "scope", "map", "outputs" })
        {
            after.RootElement.GetProperty(propertyName).GetRawText()
                .Should().Be(before.RootElement.GetProperty(propertyName).GetRawText());
        }
    }

    [Fact]
    public async Task Mutate_RenameReference_TracksTouchedSectionsFromActualChangedSections()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Rivers"));
        var currentSpec = harness.Parse(
            """
            grammar "v1.0"
            source rivers { type = "layer", ref = "catalog:layer:1" }
            scope {
              target = @rivers
              where  = cql2("state = 'HI'")
            }
            compute river_filter {
              op = filter
              inputs = { input = @rivers }
              params = { where = "category = 'mainstem'" }
            }
            map {
              layers = ["rivers"]
            }
            output rivers_out { expr = @rivers }
            """);

        var result = await harness.Service.MutateAsync(
            currentSpec,
            "rename rivers to streams",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().BeNull();
        result.Mutation.Should().NotBeNull();
        result.Mutation!.SectionsTouched.Should().Equal("sources", "scope", "compute", "map", "outputs");
        result.Mutation.SectionsPreserved.Should().BeEmpty();

        using var payload = JsonDocument.Parse(result.Mutation.NextSpecCanonicalJson);
        payload.RootElement.GetProperty("sources")[0].GetProperty("id").GetString().Should().Be("streams");
        payload.RootElement.GetProperty("scope")[0].GetProperty("target").GetString().Should().Be("@streams");
        payload.RootElement.GetProperty("compute")[0].GetProperty("inputs").GetProperty("input").GetString().Should().Be("@streams");
        payload.RootElement.GetProperty("map").GetProperty("layers")[0].GetString().Should().Be("streams");
        payload.RootElement.GetProperty("outputs")[0].GetProperty("expr").GetString().Should().Be("@streams");
    }

    [Fact]
    public async Task Mutate_RenameReference_DoesNotRewriteNonReferenceStringLiterals()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Rivers"));
        var currentSpec = harness.Parse(
            """
            grammar "v1.0"
            source rivers { type = "layer", ref = "catalog:layer:1" }
            compute river_filter {
              op = filter
              inputs = { input = @rivers }
              params = { where = "rivers" }
            }
            map {
              layers = ["rivers"]
              legend = { title = "rivers" }
            }
            output label { expr = "rivers" }
            output renamed { expr = @rivers }
            """);

        var result = await harness.Service.MutateAsync(
            currentSpec,
            "rename rivers to streams",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().BeNull();
        result.Mutation.Should().NotBeNull();

        using var payload = JsonDocument.Parse(result.Mutation!.NextSpecCanonicalJson);
        var compute = payload.RootElement.GetProperty("compute")[0];
        compute.GetProperty("inputs").GetProperty("input").GetString().Should().Be("@streams");
        compute.GetProperty("params").GetProperty("where").GetString().Should().Be("rivers");

        var map = payload.RootElement.GetProperty("map");
        map.GetProperty("layers")[0].GetString().Should().Be("streams");
        map.GetProperty("legend").GetProperty("title").GetString().Should().Be("rivers");

        var outputs = payload.RootElement.GetProperty("outputs").EnumerateArray().ToArray();
        outputs.Single(output => output.GetProperty("id").GetString() == "label")
            .GetProperty("expr").GetString().Should().Be("rivers");
        outputs.Single(output => output.GetProperty("id").GetString() == "renamed")
            .GetProperty("expr").GetString().Should().Be("@streams");
    }

    [Fact]
    public async Task Mutate_Summarize_Mutate_RoundTripsToSemanticallyEquivalentSpec()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Rivers"));

        var firstResult = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            "use rivers as river. buffer river by 500.m in EPSG:3857 as river_buffer. output river_buffer_out returns @river_buffer",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        firstResult.Mutation.Should().NotBeNull();
        var firstSummary = harness.Service.Summarize(firstResult.Mutation!.NextSpec);

        var secondResult = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            SpecGroundingTestSupport.BuildRoundTripTurn(firstSummary),
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        secondResult.Mutation.Should().NotBeNull();
        var secondSummary = harness.Service.Summarize(secondResult.Mutation!.NextSpec);

        secondSummary.TitleSummary.Should().Be(firstSummary.TitleSummary);
        secondSummary.Sections.Select(section => (section.SectionId, section.Text))
            .Should().Equal(firstSummary.Sections.Select(section => (section.SectionId, section.Text)));
    }

    [Fact]
    public async Task Mutate_WhenPlannedOutputReferencesUnknownId_ReturnsInvalidMutation()
    {
        using var harness = CreateHarness(SpecGroundingTestSupport.CreateLayer(1, "Zones"));

        var result = await harness.Service.MutateAsync(
            harness.Parse(
                """
                grammar "v1.0"
                source zones { type = "layer", ref = "catalog:layer:1" }
                """),
            "buffer zones by 500.m in EPSG:3857 as zones",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().Be(SpecGroundingErrorKind.InvalidMutation);
        result.Mutation.Should().BeNull();
        result.Warnings.Should().Contain(diagnostic => diagnostic.Severity == SpecDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Mutate_OutOfScopeTurn_ReturnsStructuredOutOfScopeError()
    {
        using var harness = CreateHarness();

        var result = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            "publish this dashboard",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().Be(SpecGroundingErrorKind.OutOfScope);
        result.ErrorMessage.Should().Contain("supported spec-grounding scope");
        result.Warnings.Should().Contain(diagnostic => diagnostic.Code == SpecDiagnosticCode.UnknownOperator);
    }

    [Fact]
    public async Task Mutate_UnknownTurn_ReturnsStructuredUnresolvableError()
    {
        using var harness = CreateHarness();

        var result = await harness.Service.MutateAsync(
            SpecGroundingTestSupport.CreateEmptySpecDocument(),
            "dance around the moon",
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().Be(SpecGroundingErrorKind.Unresolvable);
        result.Mutation.Should().BeNull();
        result.Clarification.Should().BeNull();
    }

    [Fact]
    public void SpecMutationKind_RemainsClosedToAnalysisShapeMutations()
    {
        Enum.GetNames<SpecMutationKind>().Should().Equal(
        [
            nameof(SpecMutationKind.AddSource),
            nameof(SpecMutationKind.RemoveSource),
            nameof(SpecMutationKind.AddScopeClause),
            nameof(SpecMutationKind.AddCompute),
            nameof(SpecMutationKind.RemoveCompute),
            nameof(SpecMutationKind.SetMapLayer),
            nameof(SpecMutationKind.SetViewport),
            nameof(SpecMutationKind.SetOutput),
            nameof(SpecMutationKind.RenameReference)
        ]);
    }

    private static SpecGroundingHarness CreateHarness(params LayerDefinition[] layers)
        => new(layers);
}
