// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerCalculateExpressionTests
{
    [UnitTest]
    public void TryInspectCalcExpression_FieldReference_ReportsSourceIdentifier()
    {
        var parsed = FeatureServerEndpoints.TryInspectCalcExpression(
            "secret || name",
            out var sources,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        sources.Should().BeEquivalentTo(["secret", "name"]);
    }

    [UnitTest]
    public void TryInspectCalcExpression_FunctionAndKeywords_SkipsNonFields()
    {
        var parsed = FeatureServerEndpoints.TryInspectCalcExpression(
            "UPPER(secret) || NULL",
            out var sources,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        sources.Should().Equal("secret");
    }
}
