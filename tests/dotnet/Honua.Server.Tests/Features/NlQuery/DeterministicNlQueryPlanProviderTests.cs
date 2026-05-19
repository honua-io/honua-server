// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.NlQuery.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.AiBuilder.Fixtures;
using Honua.Server.Features.NlQuery;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.NlQuery;

[Protocol(TestProtocols.TestQuality)]
public sealed class DeterministicNlQueryPlanProviderTests
{
    private static readonly LayerDefinition TestLayer = new(
        Id: 1,
        Name: "critical_facilities",
        Description: "Fixture critical facilities layer",
        GeometryType: GeometryType.Point,
        SpatialReference: SpatialReference.WGS84,
        Fields:
        [
            new FieldDefinition("facility_type", FieldType.String, Length: 50),
            new FieldDefinition("status", FieldType.String, Length: 20),
            new FieldDefinition("capacity", FieldType.Integer)
        ]);

    private static DeterministicNlQueryPlanProvider CreateProvider()
    {
        var catalog = new AiBuilderFixtureCatalog();
        var logger = NullLogger<DeterministicNlQueryPlanProvider>.Instance;
        return new DeterministicNlQueryPlanProvider(catalog, logger);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_SuccessFixturePrompt_ReturnsCannedFilterPlan()
    {
        var provider = CreateProvider();
        var request = new NlQueryPlanRequest(
            Query: "Show open hospitals within 1 km of flood zones as a linked map, table, and chart.",
            Layer: TestLayer,
            CollectionId: "critical_facilities");

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.Combinator.Should().Be(FilterPlanCombinator.And);
        result.Plan.Clauses.Should().HaveCount(3);
        result.Plan.Clauses.Should().Contain(clause =>
            clause.Type == "spatial"
            && clause.Spatial != null
            && clause.Spatial.Operator == "dwithin"
            && clause.Spatial.Distance == 1000);
        result.Plan.Clauses.Should().Contain(clause =>
            clause.Type == "comparison"
            && clause.Comparison != null
            && clause.Comparison.Property == "facility_type"
            && clause.Comparison.Operator == "eq");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_PromptMatchedCaseInsensitively_ReturnsPlan()
    {
        var provider = CreateProvider();
        var request = new NlQueryPlanRequest(
            Query: "  SHOW OPEN HOSPITALS WITHIN 1 KM OF FLOOD ZONES AS A LINKED MAP, TABLE, AND CHART.  ",
            Layer: TestLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Plan.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_AmbiguityFixturePrompt_FailsWithCaseReason()
    {
        var provider = CreateProvider();
        var request = new NlQueryPlanRequest(
            Query: "Find shelters near flood zones.",
            Layer: TestLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ambiguity");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_UnsupportedFixturePrompt_FailsWithCaseReason()
    {
        var provider = CreateProvider();
        var request = new NlQueryPlanRequest(
            Query: "Join every road segment to every flood polygon it crosses and summarize by route.",
            Layer: TestLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unsupported");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_UnknownPrompt_ReturnsFailure()
    {
        var provider = CreateProvider();
        var request = new NlQueryPlanRequest(
            Query: "totally unrelated utterance that no fixture covers",
            Layer: TestLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public async Task GeneratePlanAsync_OperationsDashboardSuccessPrompt_ReturnsClarificationReasonBecauseNoFilterPlan()
    {
        // The operations-dashboard success scenario produces a CanonicalSpec
        // draft rather than a FilterPlan; from the NL-filter planner's perspective
        // it is a non-filter outcome and should surface as a structured failure.
        var provider = CreateProvider();
        var request = new NlQueryPlanRequest(
            Query: "Build an operations dashboard for this saved map showing a map, incident list, incident count, incidents by type chart, and district filter.",
            Layer: TestLayer);

        var result = await provider.GeneratePlanAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("success");
    }
}
