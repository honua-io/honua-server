// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Unit tests for the additive Esri-conventional task-name overlay
/// (<see cref="GPServerEsriTaskAliases"/>) that lets unmodified ArcGIS clients address
/// GPServer tasks by their familiar Esri GP tool name (e.g. <c>Buffer</c>) in addition
/// to the canonical internal process ID (e.g. <c>geometry.buffer</c>).
/// </summary>
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerEsriTaskAliasesTests
{
    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void GetAlias_KnownProcessId_ReturnsEsriName()
    {
        GPServerEsriTaskAliases.GetAlias("geometry.buffer").Should().Be("Buffer");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void GetAlias_ProcessWithoutEsriEquivalent_ReturnsNull()
    {
        // analytics.cluster has no single unambiguous Esri GP tool name (it spans
        // both DBSCAN and K-Means, which are two distinct Esri tools) and must keep
        // only its internal-ID name.
        GPServerEsriTaskAliases.GetAlias("analytics.cluster").Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void TryResolveProcessId_KnownAlias_ResolvesToProcessId()
    {
        GPServerEsriTaskAliases.TryResolveProcessId("Buffer", out var processId).Should().BeTrue();
        processId.Should().Be("geometry.buffer");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void TryResolveProcessId_IsCaseInsensitive()
    {
        GPServerEsriTaskAliases.TryResolveProcessId("buffer", out var lower).Should().BeTrue();
        lower.Should().Be("geometry.buffer");

        GPServerEsriTaskAliases.TryResolveProcessId("BUFFER", out var upper).Should().BeTrue();
        upper.Should().Be("geometry.buffer");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void TryResolveProcessId_UnknownAlias_ReturnsFalse()
    {
        GPServerEsriTaskAliases.TryResolveProcessId("NotARealEsriToolName", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void TryResolveProcessId_InternalProcessId_ReturnsFalse()
    {
        // Internal process IDs are resolved directly by IProcessCatalog.GetProcess, not
        // through the alias overlay; the overlay only maps the Esri-facing name.
        GPServerEsriTaskAliases.TryResolveProcessId("geometry.buffer", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void EveryAliasedProcessId_ExistsInTheBuiltInCatalog()
    {
        // Guards against typos in GPServerEsriTaskAliases drifting from real process IDs
        // as BuiltInProcessCatalog evolves.
        var catalog = new BuiltInProcessCatalog();

        foreach (var processId in KnownAliasedProcessIds)
        {
            catalog.GetProcess(processId).Should().NotBeNull(
                "GPServerEsriTaskAliases maps '{0}' but it is not in BuiltInProcessCatalog", processId);
        }
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void EveryAlias_IsUnique()
    {
        var aliases = KnownAliasedProcessIds
            .Select(GPServerEsriTaskAliases.GetAlias)
            .Where(alias => alias != null)
            .ToArray();

        aliases.Should().OnlyHaveUniqueItems("two internal processes must never publish the same Esri alias");
    }

    // Process IDs known (as of this test's authoring) to carry an Esri alias, kept in
    // sync manually with GPServerEsriTaskAliases. Used to assert catalog membership and
    // alias uniqueness without hard-coding the alias dictionary's private contents.
    private static readonly string[] KnownAliasedProcessIds =
    [
        "geometry.buffer",
        "geometry.snap",
        "overlay.clip",
        "overlay.intersect",
        "overlay.union",
        "overlay.erase",
        "overlay.merge",
        "overlay.split",
        "proximity.near",
        "proximity.near-table",
        "proximity.euclidean-distance",
        "proximity.euclidean-allocation",
        "statistics.summarize",
        "statistics.frequency",
        "surface.slope",
        "surface.aspect",
        "surface.hillshade",
        "surface.contour",
        "surface.viewshed",
        "raster.reproject",
        "raster.statistics",
        "raster.zonal-statistics",
        "raster.resample",
        "raster.interpolate-idw",
        "raster.interpolate-kriging",
        "raster.mosaic",
        "raster.reclassify",
        "conversion.feature-project",
        "conversion.polygonize",
        "conversion.rasterize",
        "data-management.copy-features",
        "data-management.append",
        "data-management.delete-features",
        "data-management.calculate-field",
        "generalization.dissolve",
        "analytics.spatial-join-managed",
        "analytics.hotspot-managed",
    ];
}
