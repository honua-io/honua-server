// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.Grounding.Spec;

namespace Honua.Server.Tests.Features.Grounding.Spec;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class SpecGroundingRemoveMissingTests
{
    [Theory]
    [InlineData("remove source nope")]
    [InlineData("remove compute nope")]
    [InlineData("show rivers on the map; remove source nope")]
    public async Task Mutate_RemovingMissingId_ReturnsInvalidMutationWithoutNextSpec(string turn)
    {
        using var harness = new SpecGroundingHarness(SpecGroundingTestSupport.CreateLayer(1, "Rivers"));
        var currentSpec = harness.Parse(
            """
            grammar "v1.0"
            source rivers { type = "layer", ref = "catalog:layer:1" }
            """);

        var result = await harness.Service.MutateAsync(
            currentSpec,
            turn,
            context: null,
            clarificationAnswer: null,
            principal: null,
            CancellationToken.None);

        result.ErrorKind.Should().Be(SpecGroundingErrorKind.InvalidMutation);
        result.Mutation.Should().BeNull();
        result.Clarification.Should().BeNull();
    }
}
