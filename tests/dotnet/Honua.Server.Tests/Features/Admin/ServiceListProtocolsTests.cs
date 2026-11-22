// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

// The admin service list leaves raster-only protocols out of "all" on a feature-only install.
public sealed class ServiceListProtocolsTests
{
    [UnitTest]
    public async Task WithoutRasterStorage_LeavesRasterOnlyProtocolsOut()
    {
        var availability = Substitute.For<IRasterStorageAvailability>();
        availability.IsInstalledAsync(Arg.Any<CancellationToken>()).Returns(false);

        var protocols = await ServiceSettingsEndpoints.ServableProtocolsAsync(Context(availability));

        protocols.Should().Equal(ServiceProtocols.WithoutRasterStorage);
        protocols.Should().Contain(ServiceProtocols.ImageServer).And.NotContain(ServiceProtocols.Terrain);
    }

    [UnitTest]
    public async Task WithoutRasterStore_KeepsEveryProtocol()
    {
        var protocols = await ServiceSettingsEndpoints.ServableProtocolsAsync(Context(null));

        protocols.Should().Equal(ServiceProtocols.All);
    }

    [UnitTest]
    public async Task WhenTheCheckFails_KeepsEveryProtocol()
    {
        var availability = Substitute.For<IRasterStorageAvailability>();
        availability.IsInstalledAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new ProbeFailure()));

        var protocols = await ServiceSettingsEndpoints.ServableProtocolsAsync(Context(availability));

        protocols.Should().Equal(ServiceProtocols.All);
    }

    private static DefaultHttpContext Context(IRasterStorageAvailability? availability)
    {
        var services = new ServiceCollection();
        if (availability is not null)
        {
            services.AddSingleton(availability);
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private sealed class ProbeFailure() : DbException("database unavailable");
}
