// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Domain;

namespace Honua.Core.Tests.Features.Geocoding;

public sealed class GeocodeQueryLimitsTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void DistinctTokens_RepeatedWords_KeepsFirstSeenOrder()
    {
        var tokens = GeocodeQueryLimits.DistinctTokens("Main, main ST main", "singleLine");

        Assert.Equal(["main", "st"], tokens);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DistinctTokens_OverLength_ThrowsInvalidRequest()
    {
        var query = new string('e', GeocodeQueryLimits.MaxQueryLength + 1);

        var exception = Assert.Throws<GeocodeRequestException>(() =>
            GeocodeQueryLimits.DistinctTokens(query, "singleLine"));

        Assert.Contains("Invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal("singleLine", exception.ParameterName);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void DistinctTokens_TooManyDistinctTokens_ThrowsInvalidRequest()
    {
        var query = string.Join(' ', Enumerable.Range(0, GeocodeQueryLimits.MaxDistinctTokens + 1).Select(i => $"t{i}"));

        var exception = Assert.Throws<GeocodeRequestException>(() =>
            GeocodeQueryLimits.EnsureBounded(query, "records"));

        Assert.Contains("Invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal("records", exception.ParameterName);
    }
}
