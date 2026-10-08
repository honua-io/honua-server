// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// Verifies publisher-hidden fields are omitted while supported protocol behavior is preserved.
/// </summary>
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcHiddenFieldConversionTests
{
    [UnitTest]
    public void ProviderReturnsHiddenAttributes_OmitsTheirValuesCaseInsensitively()
    {
        var feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", "visible-5614")
            .Add("CATEGORY", "hidden-5614"));
        var hiddenFields = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "category");

        var response = GrpcConversionHelpers.ToProtoFeature(feature, hiddenFields: hiddenFields);

        response.Id.Should().Be(42);
        response.Attributes["name"].StringValue.Should().Be("visible-5614");
        response.Attributes.Keys.Should().NotContain("CATEGORY");
    }
}
