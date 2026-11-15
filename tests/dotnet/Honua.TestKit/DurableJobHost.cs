// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.TestKit;

internal static class DurableJobHost
{
    public static WebAppFixture Create() => new WebAppFixture().WithProductionJobSubstrate();

    public static async Task StartAsync(WebAppFixture fixture)
    {
        await fixture.InitializeAsync();
        await fixture.Postgres.DataSource.ClearJobSubstrateAsync();
        AssertJobsRunOnPostgreSql(fixture);
    }

    public static void AssertJobsRunOnPostgreSql(WebAppFixture fixture)
    {
        fixture.GetService<IExecutionJobStore>().Should().BeOfType<PostgresExecutionJobStore>();
        fixture.GetService<IJobQueue>().Should().BeOfType<PostgresJobQueue>();
        fixture.Services.GetServices<IHostedService>().OfType<JobExecutionService>().Should().NotBeEmpty();
    }
}
