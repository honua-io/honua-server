// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Grounding.Spec;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Tests.Features.Grounding.Spec;

// Clause words may be separated by any whitespace run, but quoted literals keep their spacing.
[global::Xunit.Trait("Tier", "Fast")]
public sealed class SpecGroundingWhitespaceTests
{
    [Theory]
    [InlineData("filters parcels where name = 'North  Fork'", "name = 'North  Fork'")]
    [InlineData("filters \t parcels   where  name = 'Two\tTabs  here'", "name = 'Two\tTabs  here'")]
    [InlineData("filters parcels where name = 'it''s  ok'", "name = 'it''s  ok'")]
    public async Task Mutate_ScopeClause_CollapsesWordGapsButKeepsQuotedLiterals(string turn, string expectedCql2)
    {
        using var harness = new SpecGroundingHarness(SpecGroundingTestSupport.CreateLayer(
            1,
            "Parcels",
            fields:
            [
                SpecGroundingTestSupport.Field("objectid", MetadataV2FieldType.Integer, nullable: false),
                SpecGroundingTestSupport.Field("name", MetadataV2FieldType.String, length: 64)
            ]));
        var currentSpec = harness.Parse(
            """
            grammar "v1.0"
            source parcels { type = "layer", ref = "catalog:layer:1" }
            """);

        var result = await harness.Service.MutateAsync(
            currentSpec,
            turn,
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().BeNull(result.ErrorMessage);
        using var payload = JsonDocument.Parse(result.Mutation!.NextSpecCanonicalJson);
        payload.RootElement.GetProperty("scope")[0]
            .GetProperty("where")
            .GetProperty("cql2")
            .GetString()
            .Should().Be(expectedCql2);
    }
}
