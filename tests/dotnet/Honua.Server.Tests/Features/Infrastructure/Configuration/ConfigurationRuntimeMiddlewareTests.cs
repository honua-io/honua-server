// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Infrastructure.RateLimiting;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.Configuration;

[Collection("UnitTest")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class ConfigurationRuntimeMiddlewareTests
{
    [Fact]
    public async Task UseSharedConfiguration_MalformedDataSource_DefersConstructionUntilGuardedPreflight()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NpgsqlDataSource>(_ => throw new ArgumentException("Malformed connection string."));
        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        var configure = () => app.UseSharedConfiguration();

        configure.Should().NotThrow();
    }

    [Fact]
    public async Task UseRateLimiting_DatabaseBackedPolicy_DefersConstructionUntilRequests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRateLimitPolicyStore>(_ => throw new ArgumentException("Malformed connection string."));
        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        app.UseRateLimiting();
        var build = () => app.Build();

        build.Should().NotThrow();
    }

    [Theory]
    [InlineData("/healthz/live", StatusCodes.Status204NoContent, 0)]
    [InlineData("/api/v1/admin/metadata", StatusCodes.Status503ServiceUnavailable, 1)]
    public async Task UseSharedConfiguration_UnmigratedDatabase_DefersResolutionAndRefusesProtectedRequests(
        string path, int expectedStatus, int expectedResolutions)
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unmigrated;Username=test;Password=test"));
        services.AddSingleton<ConfigurationRuntimeRefresh>(_ =>
        {
            resolutions++;
            throw new InvalidOperationException("Schema has not been migrated yet.");
        });
        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        app.UseSharedConfiguration();
        resolutions.Should().Be(0);
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = path;

        await app.Build()(context);

        context.Response.StatusCode.Should().Be(expectedStatus);
        resolutions.Should().Be(expectedResolutions);
    }
}
