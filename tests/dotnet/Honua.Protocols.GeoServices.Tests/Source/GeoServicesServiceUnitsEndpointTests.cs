// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

[Collection("Database")]
[Protocol(TestProtocols.FeatureServer, TestProtocols.MapServer)]
public sealed class GeoServicesServiceUnitsEndpointTests
{
    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer")]
    public async Task GetServiceMetadata_GeographicAndProjectedServices_SerializesMatchingUnits()
    {
        // Expected values are CRS definitions, not values calculated by the server helper.
        (string Name, int? Srid, int ExpectedWkid, string Units)[] cases =
        [
            ("units-default", null, 4326, "esriDecimalDegrees"),
            ("units-wgs84", 4326, 4326, "esriDecimalDegrees"),
            ("units-nad83", 4269, 4269, "esriDecimalDegrees"),
            ("units-mercator", 3857, 3857, "esriMeters"),
        ];
        var graph = new MetadataV2Graph
        {
            Services = cases.Select(item => new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata { Id = item.Name, Name = item.Name },
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                Protocols = [ServiceProtocols.FeatureServer, ServiceProtocols.MapServer],
                AccessPolicy = new AccessPolicy { AllowAnonymous = true },
                SpatialReference = item.Srid.HasValue ? new MetadataV2SpatialReference { Srid = item.Srid } : null,
            }).ToArray(),
        };
        var provider = new TestMetadataV2GraphProvider(graph);
        await using var fixture = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.RemoveAll<IMetadataV2GraphStore>();
            services.AddSingleton<IMetadataV2GraphProvider>(provider);
            services.AddSingleton<IMetadataV2GraphStore>(provider);
        });
        await fixture.InitializeAsync();

        using var scope = new AssertionScope();
        foreach (var item in cases)
        {
            foreach (var protocol in new[] { "FeatureServer", "MapServer" })
            {
                using var response = await fixture.Client.GetAsync($"/rest/services/{item.Name}/{protocol}?f=json");
                response.Be200Ok();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var root = document.RootElement;
                root.GetProperty("spatialReference").GetProperty("wkid").GetInt32()
                    .Should().Be(item.ExpectedWkid, "the service {0}/{1} declares this CRS", item.Name, protocol);
                root.GetProperty("units").GetString()
                    .Should().Be(item.Units, "the service {0}/{1} uses these coordinate units", item.Name, protocol);
            }
        }
    }
}
