// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

internal static class StudioMcpFixtures
{
    // Direct composition uses Pro; Enterprise approval is exercised by separate suites.
    internal static WebAppFixture CreateCompositionFixture() => new WebAppFixture()
        .ConfigureWebHost(builder => builder.UseSetting("Licensing:DevGrantEdition", "Pro"))
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        });
}
