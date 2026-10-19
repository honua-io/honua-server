// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.SensorThings.Abstractions;
using Honua.Db.Postgres.Features.SensorThings;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.SensorThings;

/// <summary>Store-side <c>@iot.id</c> parsing rejects values that are not positive integers.</summary>
public sealed class SensorThingsEntityIdTests
{
    [UnitTest]
    public void RequirePositiveEntityId_Integer_ReturnsTheId()
    {
        PostgresObservationStore.RequirePositiveEntityId(JsonNode.Parse("4")!).Should().Be(4);
    }

    [UnitTheory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("""{"value":1}""")]
    [InlineData("[1]")]
    public void RequirePositiveEntityId_NonPositiveInteger_ThrowsValidation(string json)
    {
        var act = () => PostgresObservationStore.RequirePositiveEntityId(JsonNode.Parse(json)!);

        act.Should().Throw<SensorThingsValidationException>()
            .WithMessage("@iot.id must be a positive integer.");
    }
}
