// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

internal static class StudioMcpTestFixture
{
    public static WebAppFixture Create() => new WebAppFixture()
        .WithProductionJobSubstrate()
        .ConfigureWebHost(builder => builder.UseSetting("Public:BaseUrl", "http://localhost"))
        .ConfigureServices(services =>
        {
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        });
}
