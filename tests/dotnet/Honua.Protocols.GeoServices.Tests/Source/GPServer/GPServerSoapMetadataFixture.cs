// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geoprocessing;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

// The job factories replace the job service (and, for TerminalFactory, the terminal service)
// with one substitute each, registered as singletons so every consumer, including the real
// terminal service, sees the same object. A case calls ResetJobs/ResetTerminal first, which
// clears configured returns, callbacks and received calls, then configures its own behaviour.
// Factories start on first use, so a filtered run pays only for the hosts it touches.
public sealed class GPServerSoapMetadataFixture : IDisposable
{
    public GPServerSoapMetadataFixture()
    {
        JobFactory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(Jobs);
        });
        TerminalFactory = ServiceRbacTestFixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(Jobs);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(Terminal);
        });
    }

    public WebApplicationFactory<Program> Factory { get; } = ServiceRbacTestFixture.CreateFactory();

    public WebApplicationFactory<Program> ProtectedFactory { get; } = ServiceRbacTestFixture.CreateFactory(
        () => new RbacTestLayerCatalog(
            alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["gp-reader"])));

    public WebApplicationFactory<Program> JobFactory { get; }

    public WebApplicationFactory<Program> TerminalFactory { get; }

    internal IGeoprocessingJobService Jobs { get; } = Substitute.For<IGeoprocessingJobService>();

    internal IGeoprocessingJobTerminalService Terminal { get; } = Substitute.For<IGeoprocessingJobTerminalService>();

    internal IGeoprocessingJobService ResetJobs()
    {
        Jobs.ClearSubstitute(ClearOptions.All);
        return Jobs;
    }

    internal IGeoprocessingJobTerminalService ResetTerminal()
    {
        Terminal.ClearSubstitute(ClearOptions.All);
        return Terminal;
    }

    public void Dispose()
    {
        TerminalFactory.Dispose();
        JobFactory.Dispose();
        ProtectedFactory.Dispose();
        Factory.Dispose();
    }
}
