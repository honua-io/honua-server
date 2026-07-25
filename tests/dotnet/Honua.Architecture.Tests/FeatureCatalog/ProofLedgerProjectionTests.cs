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
    [ArchitectureTest]
    public void ResolveSurface_ScimRoute_PrefersEndpointImplementation()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("/scim/v2/Users");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be(
            "src/Honua.Server/Features/Identity/Scim/ScimEndpoints.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_DatacubeRoute_PrefersEndpointImplementation()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface(
            "/api/v1/datacubes/{layerId}/tiles/{tileMatrixSetId}/{z}/{x}/{y}");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be(
            "src/Honua.Server/Features/Protocols/Zarr/ZarrEndpoints.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_WithoutImplementationEvidence_FallsBackToRegistry()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("/metrics");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be("src/Honua.Server/EndpointRegistry.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_WithOnlyNonImplementationSourceEvidence_FallsBackToRegistry()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("/ogc/features/collections");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be("src/Honua.Server/EndpointRegistry.cs");
    }

    [ArchitectureTest]
    public void ResolveSurface_WithSpecificRegistryEvidence_PreservesRegistryLocation()
    {
        var surface = ProofLedgerProjection.Load().ResolveSurface("/api/v1/operate/status");

        surface.Should().NotBeNull();
        surface!.CodeLocation.Should().Be("src/Honua.Server/EndpointRegistry.Operate.cs");
    }
}
