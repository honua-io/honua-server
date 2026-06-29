// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.DataEnrichment.Models;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Startup;

/// <summary>
/// Regression coverage for the minimal-API JSON resolver wiring (<see cref="JsonContextRegistration"/>).
/// At startup ASP.NET's RequestDelegate source generator eagerly resolves a
/// <c>JsonTypeInfo</c> for every <c>[FromBody]</c> parameter against the configured HTTP JSON
/// options; if a feature's source-generated context is not part of the registered resolver the
/// host throws a fatal <see cref="System.NotSupportedException"/> while building the endpoints,
/// taking the whole application down. These tests exercise the exact registration path the app
/// uses (no Docker / no test host) so a missing context is caught as a fast unit failure rather
/// than a startup crash in every integration shard.
/// </summary>
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

        // Act + Assert: the [FromBody] DTOs for the enrichment admin endpoints (#2280) must be
        // resolvable, otherwise the RegisterEnrichmentDataset / UpdateEnrichmentDataset MapPost/
        // MapPut endpoints fail to build and the host aborts at startup.
        serializerOptions.GetTypeInfo(typeof(RegisterEnrichmentDatasetRequest))
            .Should().NotBeNull("the enrichment register-dataset [FromBody] type must be in the configured HTTP JSON resolver");
        serializerOptions.GetTypeInfo(typeof(UpdateEnrichmentDatasetRequest))
            .Should().NotBeNull("the enrichment update-dataset [FromBody] type must be in the configured HTTP JSON resolver");
        serializerOptions.GetTypeInfo(typeof(EnrichmentDatasetMetadata))
            .Should().NotBeNull("the enrichment dataset response body must be serializable through the configured HTTP JSON resolver");
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
