// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Geoprocessing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Unit tests for the default GPServer service seeder (honua-server#2349): a bare
/// instance must gain a GPServer-capable service so the facade is usable out of the
/// box, without wiping or duplicating anything on instances that already expose GP.
/// </summary>
public sealed class GeoprocessingServiceSeederTests
{
    private static GeoprocessingServiceSeeder CreateSeeder(TestMetadataV2GraphProvider store)
        => new(store, NullLogger<GeoprocessingServiceSeeder>.Instance);

    [UnitTest]
    public async Task EnsureSeededAsync_OnBareGraph_AddsGpServerEnabledService()
    {
        var store = new TestMetadataV2GraphProvider(new TestMetadataV2GraphBuilder().Build());

        await CreateSeeder(store).EnsureSeededAsync();

        var services = (await store.GetCurrentAsync()).Graph.Services;
        var gp = services.Should()
            .ContainSingle(s => s.Metadata.Name == GeoprocessingServiceSeeder.ServiceName)
            .Which;
        gp.Metadata.Id.Should().Be(GeoprocessingServiceSeeder.ServiceId);
        MetadataV2ServiceProtocols.IsProtocolEnabled(gp, MetadataV2ServiceProtocols.GPServer)
            .Should().BeTrue();
    }

    [UnitTest]
    public async Task EnsureSeededAsync_WhenAServiceAlreadyExposesGpServer_IsNoOp()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .WithRevision(7)
            .AddService("svc-existing", "existing", protocols: [MetadataV2ServiceProtocols.GPServer])
            .Build();
        var store = new TestMetadataV2GraphProvider(graph);

        await CreateSeeder(store).EnsureSeededAsync();

        var current = (await store.GetCurrentAsync()).Graph;
        current.Services.Should().ContainSingle();
        current.Revision.Should().Be(7, "the seeder must not write when GPServer is already reachable");
    }

    [UnitTest]
    public async Task EnsureSeededAsync_PreservesExistingServices_WhenSeeding()
    {
        // A service that does NOT expose GPServer (only FeatureServer) must survive
        // the seed as a merge, not be replaced.
        var graph = new TestMetadataV2GraphBuilder()
            .AddService("svc-features", "features", protocols: [MetadataV2ServiceProtocols.FeatureServer])
            .Build();
        var store = new TestMetadataV2GraphProvider(graph);

        await CreateSeeder(store).EnsureSeededAsync();

        var services = (await store.GetCurrentAsync()).Graph.Services;
        services.Should().Contain(s => s.Metadata.Name == "features");
        services.Should().Contain(s =>
            s.Metadata.Name == GeoprocessingServiceSeeder.ServiceName &&
            MetadataV2ServiceProtocols.IsProtocolEnabled(s, MetadataV2ServiceProtocols.GPServer));
    }

    [UnitTest]
    public async Task EnsureSeededAsync_IsIdempotent_AcrossRepeatedRuns()
    {
        var store = new TestMetadataV2GraphProvider(new TestMetadataV2GraphBuilder().Build());
        var seeder = CreateSeeder(store);

        await seeder.EnsureSeededAsync();
        await seeder.EnsureSeededAsync();

        (await store.GetCurrentAsync()).Graph.Services
            .Count(s => s.Metadata.Name == GeoprocessingServiceSeeder.ServiceName)
            .Should().Be(1, "the second run must be a no-op since GPServer is now reachable");
    }
}
