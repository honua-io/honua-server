// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class GeoServicesQueryParameterAdapterTests
{
    [Fact]
    public async Task ConvertAsync_WithoutResultRecordCount_UsesAdvertisedMaxRecordCount()
    {
        var adapter = new GeoServicesQueryParameterAdapter(NullLogger<GeoServicesQueryParameterAdapter>.Instance);
        var request = new GeoServicesQueryRequest
        {
            Parameters = new QueryParameters { F = "json" },
            QueryLimits = new QueryLimits
            {
                MaxRecordCount = 10000,
                DefaultRecordCount = 1000
            }
        };
        var resource = new MetadataV2Resource
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ]
        };

        var result = await adapter.ConvertAsync(request, resource);

        result.IsSuccess.Should().BeTrue();
        result.Query.Should().NotBeNull();
        var query = result.Query!.Value;
        query.Limit.Should().Be(10000,
            "the page must match the layer's advertised maxRecordCount when the caller omits resultRecordCount");
    }
}
