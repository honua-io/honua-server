// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FeatureStore.Domain;

/// <summary>
/// Unit coverage for <see cref="FeatureStateToken"/>, the canonical optimistic-concurrency
/// token used by <see cref="FeatureEditPrecondition"/>. Protocol adapters compute the token
/// from their read snapshot and feature writers recompute it from the row re-read inside the
/// write transaction, so the computation must be deterministic and representation-neutral.
/// </summary>
public sealed class FeatureStateTokenTests
{
    private static byte[] SampleWkb(byte marker) => [0x01, 0x01, 0x00, 0x00, marker];

    [UnitTest]
    public void Compute_SameFeature_ProducesSameToken()
    {
        var feature = Feature.Create(7, SampleWkb(1), ImmutableDictionary<string, object?>.Empty
            .Add("name", "alpha")
            .Add("count", 5L));

        FeatureStateToken.Compute(feature).Should().Be(FeatureStateToken.Compute(feature));
    }

    [UnitTest]
    public void Compute_IsIndependentOfAttributeInsertionOrder()
    {
        var first = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("a", "x")
            .Add("b", 2L));
        var second = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("b", 2L)
            .Add("a", "x"));

        FeatureStateToken.Compute(first).Should().Be(FeatureStateToken.Compute(second));
    }

    [UnitTest]
    public void Compute_NormalizesJsonElementAndPrimitiveNumbers()
    {
        using var document = JsonDocument.Parse("""{"count": 5}""");
        var jsonValue = document.RootElement.GetProperty("count").Clone();

        var withJsonElement = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("count", jsonValue));
        var withPrimitive = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("count", 5L));

        FeatureStateToken.Compute(withJsonElement).Should().Be(FeatureStateToken.Compute(withPrimitive));
    }

    [UnitTest]
    public void Compute_AttributeChange_ChangesToken()
    {
        var original = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("status", "open"));
        var modified = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("status", "closed"));

        FeatureStateToken.Compute(original).Should().NotBe(FeatureStateToken.Compute(modified));
    }

    [UnitTest]
    public void Compute_GeometryChange_ChangesToken()
    {
        var attributes = ImmutableDictionary<string, object?>.Empty.Add("name", "alpha");
        var original = Feature.Create(1, SampleWkb(1), attributes);
        var moved = Feature.Create(1, SampleWkb(2), attributes);
        var cleared = Feature.Create(1, null, attributes);

        var originalToken = FeatureStateToken.Compute(original);
        originalToken.Should().NotBe(FeatureStateToken.Compute(moved));
        originalToken.Should().NotBe(FeatureStateToken.Compute(cleared));
    }

    [UnitTest]
    public void Compute_NullValuedAttribute_IsDistinctFromMissingAttribute()
    {
        var withNull = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("status", null));
        var without = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty);

        FeatureStateToken.Compute(withNull).Should().NotBe(FeatureStateToken.Compute(without));
    }
}
