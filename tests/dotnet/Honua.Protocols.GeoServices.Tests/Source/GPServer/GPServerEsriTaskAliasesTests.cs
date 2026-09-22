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

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public void EveryAliasedProcess_IsPublishedOnTheTaskListExactlyOnce()
    {
        // #4781: the alias contract, the parity alias claim that counts it, and the
        // published task list must agree. GPServer publishes only job-callable processes
        // (#4409), so an alias mapped onto a protocol-only or workflow-only process — as
        // DeleteFeatures/CalculateField once were — promises a task that is never
        // discoverable, describable or submittable. Iterate the REAL contract rather than
        // a hand-maintained copy so a future alias for an unpublished process fails here.
        var catalog = new BuiltInProcessCatalog();
        var published = GPServerEndpoints.BuildPublishedTaskNames(catalog).ToArray();

        foreach (var (processId, alias) in GPServerEsriTaskAliases.Contract)
        {
            var definition = catalog.GetProcess(processId);
            definition.Should().NotBeNull(
                "GPServerEsriTaskAliases maps '{0}' but it is not in BuiltInProcessCatalog", processId);
            GPServerExecutionPolicy.IsJobCallable(definition!).Should().BeTrue(
                "alias '{0}' maps process '{1}', which GPServer only publishes when it declares the job entry point",
                alias, processId);
            published.Count(name => string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
                .Should().Be(1, "alias '{0}' for process '{1}' must be published exactly once", alias, processId);
        }
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public void ProtocolOnlyDataManagementProcesses_CarryNoAlias()
    {
        // Regression for #4781. Both processes mutate a caller-owned layer through the
        // owning synchronous FeatureServer edit endpoint and register no job executor, so
        // GPServer must neither publish them nor claim an alias for them.
        var catalog = new BuiltInProcessCatalog();

        foreach (var processId in new[] { "data-management.delete-features", "data-management.calculate-field" })
        {
            catalog.GetProcess(processId).Should().NotBeNull("the process stays in the canonical catalog");
            GPServerExecutionPolicy.IsJobCallable(catalog.GetProcess(processId)!).Should().BeFalse();
            GPServerEsriTaskAliases.GetAlias(processId).Should().BeNull();
        }

        GPServerEsriTaskAliases.TryResolveProcessId("DeleteFeatures", out _).Should().BeFalse();
        GPServerEsriTaskAliases.TryResolveProcessId("CalculateField", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public void AliasContract_MatchesThePublishedParityAliasClaim()
    {
        // The GeoServices parity claim (docs/gis/data/geoservices-parity-judgment.json:
        // "37 unambiguous aliases ... are published alongside canonical process IDs")
        // counts this contract. Changing one without the other is the drift #4781 filed:
        // update the judgement source and rerun scripts/generate-geoservices-parity.sh.
        GPServerEsriTaskAliases.Contract.Should().HaveCount(37);
    }

    // Process IDs that carry an Esri alias, read from the real contract so this file can
    // never claim an alias set the adapter does not actually publish (#4781).
    private static readonly string[] KnownAliasedProcessIds =
        [.. GPServerEsriTaskAliases.Contract.Keys];
}
