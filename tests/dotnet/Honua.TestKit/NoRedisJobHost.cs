// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.TestKit;

internal static class NoRedisJobHost
{
    private const string RedisConnectionStringKey = "ConnectionStrings:redis";
    private const string AspireRedisConnectionStringKey = "Aspire:StackExchange:Redis:ConnectionString";

    // The empty value outranks a Redis address in the process environment and shadows the Aspire
    // key, which is left unset because any value under it switches the host to Aspire defaults.
    public static WebAppFixture Create()
        => new WebAppFixture()
            .WithProductionJobSubstrate()
            .ConfigureWebHost(builder => builder.UseSetting(RedisConnectionStringKey, string.Empty));

    public static async Task StartAsync(WebAppFixture fixture)
    {
        await fixture.InitializeAsync();
        await fixture.Postgres.DataSource.ClearJobSubstrateAsync();
        AssertJobsRunWithoutRedis(fixture);
    }

    public static void AssertJobsRunWithoutRedis(WebAppFixture fixture)
    {
        var configuration = fixture.GetService<IConfiguration>();
        configuration.GetConnectionString("redis").Should().BeNullOrEmpty();
        configuration[AspireRedisConnectionStringKey].Should().BeNullOrEmpty();
        fixture.GetOptionalService<IConnectionMultiplexer>().Should().BeNull();
        fixture.GetService<IExecutionJobStore>().Should().BeOfType<PostgresExecutionJobStore>();
        fixture.GetService<IJobQueue>().Should().BeOfType<PostgresJobQueue>();
        fixture.Services.GetServices<IHostedService>().OfType<JobExecutionService>().Should().NotBeEmpty();
    }
}
