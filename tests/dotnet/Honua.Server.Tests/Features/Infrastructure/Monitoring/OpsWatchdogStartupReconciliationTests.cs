// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Abstractions;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class OpsWatchdogStartupReconciliationTests
{
    [IntegrationTest]
    public async Task Startup_ConfiguredWatchdog_ResolvesProductionStoreWithScopeValidation()
    {
        await using var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        const string modeKey = "Observability__OpsFindings__Watchdog__ModeParameterName";
        const string databaseKey = "ConnectionStrings__DefaultConnection";
        var oldMode = Environment.GetEnvironmentVariable(modeKey);
        var oldDatabase = Environment.GetEnvironmentVariable(databaseKey);
        try
        {
            Environment.SetEnvironmentVariable(modeKey, "/example/watchdog/mode");
            Environment.SetEnvironmentVariable(databaseKey, postgres.ConnectionString);
            await AssertProductionStartupAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(modeKey, oldMode);
            Environment.SetEnvironmentVariable(databaseKey, oldDatabase);
        }
    }

    private static async Task AssertProductionStartupAsync()
    {
        var watchdog = Substitute.For<IOpsWatchdogKillSwitch>();
        await using var fixture = new WebAppFixture().UseSeed("tests/seed/server.yaml")
            .OverrideConfiguration(new Dictionary<string, string?>
            {
                ["Observability:OpsFindings:Watchdog:ModeParameterName"] = "/example/watchdog/mode",
            })
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseDefaultServiceProvider(options => options.ValidateScopes = true);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IOpsWatchdogKillSwitch>();
                services.AddSingleton(watchdog);
            });

        await fixture.InitializeAsync();

        using var scope = fixture.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<OpsAutonomySettingsWriter>());
    }
}
