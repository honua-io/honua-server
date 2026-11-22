// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class GPServerSpatialReferenceGuardTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void ResolveWorkingSrid_PrefersOutputSridKeysOverInputSrid()
    {
        var withToSrid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["srid"] = "4326",
            ["toSrid"] = "3857",
        };
        GPServerEndpoints.ResolveWorkingSrid(withToSrid, null).Should().Be(3857);

        var withTargetSrid = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["srid"] = "4326",
            ["targetSrid"] = "32610",
        };
        GPServerEndpoints.ResolveWorkingSrid(withTargetSrid, null).Should().Be(32610);
    }

    [Theory]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("{\"wkid\":0}")]
    [InlineData("{\"wkid\":-4326}")]
    [InlineData("{\"latestWkid\":0}")]
    public void TryParseSpatialReferenceValue_RejectsNonPositiveWkids(string raw)
    {
        GPServerEndpoints.TryParseSpatialReferenceValue(raw, out _).Should().BeFalse();
    }

    [Theory]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("4326", 4326)]
    [InlineData("{\"wkid\":3857}", 3857)]
    [InlineData("{\"latestWkid\":32610}", 32610)]
    public void TryParseSpatialReferenceValue_AcceptsPositiveWkids(string raw, int expected)
    {
        GPServerEndpoints.TryParseSpatialReferenceValue(raw, out var srid).Should().BeTrue();
        srid.Should().Be(expected);
    }
}
