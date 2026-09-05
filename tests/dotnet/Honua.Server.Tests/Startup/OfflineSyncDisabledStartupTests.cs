// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Startup;

[Collection("Database")]
public sealed class OfflineSyncDisabledStartupTests
{
    [IntegrationTest]
    [Endpoint("GET /healthz/ready")]
    public async Task Startup_OfflineSyncDisabled_ValidatesDependenciesAndReachesReadiness()
    {
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Capabilities:Experimental:Enabled"] = "false",
                    ["Capabilities:Experimental:sync.offline:Enabled"] = "false"
                }));
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        });
        await fixture.InitializeAsync();
        try
        {
            using var scope = fixture.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IReplicaConflictRepository>().Should().NotBeNull();
            scope.ServiceProvider.GetRequiredService<ReplicaConflictResolutionService>().Should().NotBeNull();

            using var response = await fixture.Client.GetAsync("/healthz/ready");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
