// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure;

public sealed class ConfiguredWebApplicationFactoryTests
{
    [Fact]
    public async Task Create_AfterReturningFactory_RemainsUsableUntilDisposed()
    {
        using var factory = ConfiguredWebApplicationFactory.Create(
            TestWebApplicationFactory.ConfigureForTests);

        using var client = factory.CreateClient();
        Assert.NotNull(factory.Services.GetRequiredService<IDatabaseHealthChecker>());

        using var response = await client.GetAsync("/healthz/live");
        response.EnsureSuccessStatusCode();
    }
}
