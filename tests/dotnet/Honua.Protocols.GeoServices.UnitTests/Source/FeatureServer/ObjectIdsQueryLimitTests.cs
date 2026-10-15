// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ObjectIdsQueryLimitTests
{
    [Fact]
    public void TryParseQueryParameters_OneHundredThousandObjectIds_RejectsWithObjectIdsExceeds()
    {
        var ids = string.Join(',', Enumerable.Range(1, 100_001));
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = ids
        };

        var parsed = FeatureServerEndpoints.TryParseQueryParameters(values, out _, out var error);

        parsed.Should().BeFalse();
        error.Should().Contain("objectIds exceeds");
    }

    [Fact]
    public void TryParseQueryParameters_DuplicateObjectIds_KeepsFirstOccurrence()
    {
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = "4,4,9,4"
        };

        var parsed = FeatureServerEndpoints.TryParseQueryParameters(values, out var parameters, out var error);

        parsed.Should().BeTrue(error);
        parameters.ObjectIds.Should().Equal(4L, 9L);
    }
}
