// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class GPServerSoapMetadataFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; } = ServiceRbacTestFixture.CreateFactory();

    public WebApplicationFactory<Program> ProtectedFactory { get; } = ServiceRbacTestFixture.CreateFactory(
        () => new RbacTestLayerCatalog(
            alphaServiceMetadata: ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["gp-reader"])));

    public void Dispose()
    {
        ProtectedFactory.Dispose();
        Factory.Dispose();
    }
}
