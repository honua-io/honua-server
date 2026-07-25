// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Verifies that feature-catalog code locations point to real implementation
/// entrypoints when the proof ledger carries that evidence.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ProofLedgerProjectionTests
{
    [Theory]
    [InlineData(
        "DELETE",
        "/mcp",
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpEndpointExtensions.cs")]
    [InlineData(
        "GET",
        "/.well-known/oauth-protected-resource/mcp",
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpProtectedResourceMetadataEndpointExtensions.cs")]
    [InlineData(
        "GET",
        "/api/enrich/datasets",
        "src/Honua.Server/Features/DataEnrichment/DataEnrichmentEndpoints.cs")]
    [InlineData(
        "POST",
        "/api/enrich/datasets",
        "src/Honua.Server/Features/DataEnrichment/EnrichmentDatasetAdminEndpoints.cs")]
    [InlineData(
        "POST",
        "/api/mobile/exceptions",
        "src/Honua.Server/Features/Mobile/Diagnostics/MobileExceptionIngestionEndpoints.cs")]
    [InlineData(
        "POST",
        "/api/v1/saved-maps/{mapId}/collaboration/feature-locks/claim",
        "src/Honua.Server/Features/Collaboration/FeatureLocks/FeatureLockEndpoints.cs")]
    [InlineData(
        "GET",
        "/api/v1/saved-maps/{mapId}/collaboration/operations",
        "src/Honua.Server/Features/Collaboration/Operations/SavedMapOperationEndpoints.cs")]
    [InlineData(
        "POST",
        "/api/v1/saved-maps/{mapId}/collaboration/sessions/join",
        "src/Honua.Server/Features/Collaboration/Sessions/CollaborationSessionEndpoints.cs")]
    [InlineData(
        "GET",
        "/rest/services/{serviceId}/VectorTileServer/resources/styles",
        "src/Honua.Protocols.GeoServices/VectorTileServer/VectorTileServerEndpoints.Resources.cs")]
    [InlineData(
        "GET",
        "/rest/services/{serviceId}/VectorTileServer/tilemap",
        "src/Honua.Protocols.GeoServices/VectorTileServer/VectorTileServerEndpoints.TileMap.cs")]
    [InlineData(
        "POST",
        "/sharing/rest/community/createGroup",
        "src/Honua.Protocols.GeoServices/Sharing/SharingCommunityEndpoints.cs")]
    [InlineData(
        "POST",
        "/sharing/rest/oauth2/token",
        "src/Honua.Protocols.GeoServices/Sharing/SharingOAuth2Endpoints.cs")]
    [InlineData(
        "GET",
        "/sharing/rest/portals/self",
        "src/Honua.Protocols.GeoServices/Sharing/SharingRestEndpoints.cs")]
    public void Generate_MultiImplementationSurface_UsesRouteSpecificEntryPoint(
        string method,
        string route,
        string expectedCodeLocation)
    {
        var entry = FeatureCatalogGenerator.Generate().Entries
            .Single(candidate =>
                string.Equals(candidate.Method, method, StringComparison.Ordinal) &&
                string.Equals(candidate.Route, route, StringComparison.Ordinal));

        entry.CodeLocation.Should().Be(expectedCodeLocation);
    }

    [ArchitectureTest]
    public void ResolveSurface_ScimRoute_PrefersEndpointImplementation()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("GET", "/scim/v2/Users");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be(
            "src/Honua.Server/Features/Identity/Scim/ScimEndpoints.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_DatacubeRoute_PrefersEndpointImplementation()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface(
            "GET",
            "/api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be(
            "src/Honua.Server/Features/Protocols/Zarr/ZarrEndpoints.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_WithoutImplementationEvidence_FallsBackToRegistry()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("GET", "/metrics");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be("src/Honua.Server/EndpointRegistry.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_WithOnlyNonImplementationSourceEvidence_FallsBackToRegistry()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("GET", "/ogc/features/collections");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be("src/Honua.Server/EndpointRegistry.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_WithSpecificRegistryEvidence_PreservesRegistryLocation()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("GET", "/api/v1/operate/status");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be("src/Honua.Server/EndpointRegistry.Operate.cs");
    }
}
