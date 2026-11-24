// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.DataEnrichment.Models;
using Honua.Server.Features.Protocols.Terrain;
using Honua.Server.Features.Protocols.Terrain.QuantizedMesh;
using Honua.Server.Features.Protocols.Tiles;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Startup;

public sealed class JsonContextRegistrationTests
{
    private static Microsoft.AspNetCore.Http.Json.JsonOptions BuildConfiguredHttpJsonOptions()
    {
        var services = new ServiceCollection();
        services.AddHonuaJsonContexts();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;
    }

    [UnitTest]
    public void AddHonuaJsonContexts_ResolvesEnrichmentDatasetAdminRequestBodies()
    {
        // Arrange: the registration the application actually wires via Program.cs.
        var options = BuildConfiguredHttpJsonOptions();
        var serializerOptions = options.SerializerOptions;

        // Act + Assert: the [FromBody] DTOs for the enrichment admin endpoints must be
        // resolvable, otherwise the RegisterEnrichmentDataset / UpdateEnrichmentDataset MapPost/
        // MapPut endpoints fail to build and the host aborts at startup.
        serializerOptions.GetTypeInfo<RegisterEnrichmentDatasetRequest>()
            .Should().NotBeNull("the enrichment register-dataset [FromBody] type must be in the configured HTTP JSON resolver");
        serializerOptions.GetTypeInfo<UpdateEnrichmentDatasetRequest>()
            .Should().NotBeNull("the enrichment update-dataset [FromBody] type must be in the configured HTTP JSON resolver");
        serializerOptions.GetTypeInfo<EnrichmentDatasetMetadata>()
            .Should().NotBeNull("the enrichment dataset response body must be serializable through the configured HTTP JSON resolver");
    }

    [UnitTest]
    public void AddHonuaJsonContexts_ResolvesLocalProcessRequestBodies()
    {
        var serializerOptions = BuildConfiguredHttpJsonOptions().SerializerOptions;

        serializerOptions.GetTypeInfo<LocalProcessBindRequest>().Should().NotBeNull();
        serializerOptions.GetTypeInfo<LocalProcessSourceWrite>().Should().NotBeNull();
        serializerOptions.GetTypeInfo<LocalProcessSnapshotRequest>().Should().NotBeNull();
        serializerOptions.GetTypeInfo<LocalProcessRunRequest>().Should().NotBeNull();
    }

    [UnitTest]
    public void AddHonuaJsonContexts_ResolvesTileAndTerrainMetadataResponses()
    {
        // ETagEndpointFilter hashes the response through these options; an unresolvable
        // type makes a WithETag route silently send no ETag.
        var serializerOptions = BuildConfiguredHttpJsonOptions().SerializerOptions;

        serializerOptions.TryGetTypeInfo(typeof(TileJsonResponse), out _).Should().BeTrue();
        serializerOptions.TryGetTypeInfo(typeof(TerrainMetadataResponse), out _).Should().BeTrue();
        serializerOptions.TryGetTypeInfo(typeof(QuantizedMeshLayerMetadataResponse), out _).Should().BeTrue();
    }

    [UnitTest]
    public void EnrichmentJsonContext_GeneratesMetadataForRequestBodies()
    {
        // Sanity: the source generator itself emits metadata for the request DTOs. This isolates
        // a missing [JsonSerializable] attribute from a missing resolver registration.
        EnrichmentJsonContext.Default.GetTypeInfo(typeof(RegisterEnrichmentDatasetRequest))
            .Should().NotBeNull();
        EnrichmentJsonContext.Default.GetTypeInfo(typeof(UpdateEnrichmentDatasetRequest))
            .Should().NotBeNull();
    }
}
