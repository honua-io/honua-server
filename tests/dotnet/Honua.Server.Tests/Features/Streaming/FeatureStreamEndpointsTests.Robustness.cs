// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class FeatureStreamEndpointsTests
{
    [IntegrationTheory]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    [InlineData("{\"type\":\"future-connection\"}", 0)]
    [InlineData("{\"type\":\"managed\",\"status\":{\"lifecycle\":\"future-lifecycle\"}}", 0)]
    [InlineData("{\"type\":\"managed\"}", 1)]
    public async Task Capabilities_ConnectionKindOrLifecycleUnrecognized_OmitsUnsupportedBinding(string json, int expectedLayers)
    {
        var provider = ServiceOnlyFilterStreamLayerCatalog.BuildMetadataProvider();
        var graph = (await provider.GetCurrentAsync()).Graph;
        var connection = JsonSerializer.Deserialize(json, MetadataV2JsonContext.Default.MetadataV2Connection)! with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "stream-connection" }
        };
        provider.SetGraph(graph with
        {
            Connections = [connection],
            StorageBindings = graph.StorageBindings.Select(binding => binding with { ConnectionId = connection.Metadata.Id }).ToArray()
        });
        await using var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ReplaceService<IMetadataV2GraphProvider>(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/streaming/features/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("layers").GetArrayLength().Should().Be(expectedLayers);
    }
}
