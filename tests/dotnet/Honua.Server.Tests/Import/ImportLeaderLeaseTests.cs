// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Migration;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class ImportLeaderLeaseUnitTests
{
    [UnitTest]
    public void IsClusterDurable_InProductionWithoutALeaseStore_IsFalse()
    {
        var manager = ImportManagers.Geoservices("Production", new ImportCoordinationStores(new OutageRedis().Multiplexer));

        manager.IsClusterDurable.Should().BeFalse();
        manager.CanAcceptNewJobs.Should().BeFalse();
    }

    [UnitTest]
    public void IsClusterDurable_InProductionWithALeaseStore_IsTrue()
    {
        var manager = ImportManagers.Geoservices(
            "Production",
            new ImportCoordinationStores(new OutageRedis().Multiplexer, new InMemoryLeaseStore()));

        manager.IsClusterDurable.Should().BeTrue();
        manager.CanAcceptNewJobs.Should().BeTrue();
    }

    [UnitTest]
    public async Task TryAcquireLeadershipAsync_InProductionWithoutALeaseStore_NeverLeads()
    {
        var manager = ImportManagers.Geoservices("Production", new ImportCoordinationStores());

        (await manager.LeaderElection.TryAcquireLeadershipAsync()).Should().BeFalse();
    }

    [UnitTest]
    public async Task TryAcquireLeadershipAsync_InDevelopmentWithoutALeaseStore_LeadsItsOwnNode()
    {
        var manager = ImportManagers.Geoservices("Development", new ImportCoordinationStores());

        (await manager.LeaderElection.TryAcquireLeadershipAsync()).Should().BeTrue();
    }
}

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class ImportLeaderLeaseTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task TryAcquireLeadershipAsync_TwoGeoservicesNodesOverOnePostgresLeaseStore_ExactlyOneLeads()
    {
        await using var harness = await CreateHarnessAsync();
        var nodes = new[] { GeoservicesNode(harness), GeoservicesNode(harness) };

        var outcomes = await Task.WhenAll(nodes.Select(node => node.TryAcquireLeadershipAsync()));

        outcomes.Count(leads => leads).Should().Be(1);
        nodes.Count(node => node.IsLeader).Should().Be(1);
    }

    [IntegrationTest]
    public async Task TryAcquireLeadershipAsync_GeoServerAndGeoservicesNodes_EachElectTheirOwnLeader()
    {
        await using var harness = await CreateHarnessAsync();
        var geoserver = new[] { GeoServerNode(harness), GeoServerNode(harness) };

        (await GeoservicesNode(harness).TryAcquireLeadershipAsync()).Should().BeTrue();
        var outcomes = await Task.WhenAll(geoserver.Select(node => node.TryAcquireLeadershipAsync()));

        outcomes.Count(leads => leads).Should().Be(1);
    }

    [IntegrationTest]
    public async Task TryAcquireLeadershipAsync_AfterTheLeaderStopsRenewing_TheOtherNodeLeads()
    {
        await using var harness = await CreateHarnessAsync();
        var stopped = GeoservicesNode(harness);
        var successor = GeoservicesNode(harness);
        (await stopped.TryAcquireLeadershipAsync()).Should().BeTrue();
        (await successor.TryAcquireLeadershipAsync()).Should().BeFalse();

        await ExpireAsync(harness, "geoservices:import:leader");

        (await successor.TryAcquireLeadershipAsync()).Should().BeTrue();
        (await stopped.HeartbeatAsync()).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task TryAcquireLeadershipAsync_AfterTheLeaderReleases_TheOtherNodeLeads()
    {
        await using var harness = await CreateHarnessAsync();
        var leader = GeoservicesNode(harness);
        var successor = GeoservicesNode(harness);
        (await leader.TryAcquireLeadershipAsync()).Should().BeTrue();
        (await successor.TryAcquireLeadershipAsync()).Should().BeFalse();

        await leader.ReleaseLeadershipAsync();

        (await successor.TryAcquireLeadershipAsync()).Should().BeTrue();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(ImportLeaderLeaseTests));

    private static IDistributedLeaderElection GeoservicesNode(ControlPlaneStateHarness harness)
        => ImportManagers.Geoservices("Production", new ImportCoordinationStores(Leases: harness.Leases)).LeaderElection;

    private static IDistributedLeaderElection GeoServerNode(ControlPlaneStateHarness harness)
        => new GeoServerImportJobManager(
            new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance),
            null,
            NullLogger<GeoServerImportJobManager>.Instance,
            new StaticHostEnvironment("Production"),
            new ImportCoordinationStores(Leases: harness.Leases)).LeaderElection;

    private static Task ExpireAsync(ControlPlaneStateHarness harness, string leaseKey)
        => harness.ExecuteAsync(
            $"UPDATE {harness.Table("leases")} SET expires_at = now() - interval '1 second' WHERE lease_key = @lease_key",
            new NpgsqlParameter("lease_key", leaseKey));
}

internal static class ImportManagers
{
    public static RedisImportJobManager Geoservices(string environmentName, ImportCoordinationStores stores)
    {
        var cache = new OutageDistributedCache();
        return new(
            new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance),
            cache,
            NullLogger<RedisImportJobManager>.Instance,
            new StaticHostEnvironment(environmentName),
            stores);
    }
}
