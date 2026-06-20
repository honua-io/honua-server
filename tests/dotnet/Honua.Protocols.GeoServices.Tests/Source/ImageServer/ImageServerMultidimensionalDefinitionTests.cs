// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Unit tests for <see cref="ImageServerMultidimensionalDefinition"/> request parsing (#1869).
/// Validates shape parsing; per-slice pixel resolution remains deferred (the handler surfaces a
/// 501 for any supplied definition).
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerMultidimensionalDefinitionTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_Empty_ReturnsNoConstraints()
    {
        ImageServerMultidimensionalDefinition.TryParse(null, out var constraints, out var error).Should().BeTrue();
        constraints.Should().BeEmpty();
        error.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_ValidArray_ParsesConstraints()
    {
        const string raw = "[{\"variableName\":\"temperature\",\"dimensionName\":\"StdZ\",\"values\":[10,20],\"isSlice\":true}]";

        ImageServerMultidimensionalDefinition.TryParse(raw, out var constraints, out var error).Should().BeTrue();
        error.Should().BeNull();
        constraints.Should().HaveCount(1);
        constraints[0].VariableName.Should().Be("temperature");
        constraints[0].DimensionName.Should().Be("StdZ");
        constraints[0].Values.Should().Equal(10d, 20d);
        constraints[0].IsSlice.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_WrappedDimensions_ParsesConstraints()
    {
        const string raw = "{\"dimensions\":[{\"dimensionName\":\"StdTime\",\"values\":[1609459200000]}]}";

        ImageServerMultidimensionalDefinition.TryParse(raw, out var constraints, out var error).Should().BeTrue();
        error.Should().BeNull();
        constraints.Should().HaveCount(1);
        constraints[0].DimensionName.Should().Be("StdTime");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_Iso8601Values_ConvertsToEpochMs()
    {
        const string raw = "[{\"dimensionName\":\"StdTime\",\"values\":[\"2021-01-01T00:00:00Z\"]}]";

        ImageServerMultidimensionalDefinition.TryParse(raw, out var constraints, out var error).Should().BeTrue();
        error.Should().BeNull();
        constraints[0].Values.Should().Equal(1609459200000d);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_MissingDimensionName_ReturnsError()
    {
        const string raw = "[{\"values\":[1]}]";

        ImageServerMultidimensionalDefinition.TryParse(raw, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_EmptyValues_ReturnsError()
    {
        const string raw = "[{\"dimensionName\":\"StdZ\",\"values\":[]}]";

        ImageServerMultidimensionalDefinition.TryParse(raw, out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void TryParse_InvalidJson_ReturnsError()
    {
        ImageServerMultidimensionalDefinition.TryParse("not json", out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }
}
