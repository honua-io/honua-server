// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal sealed partial class OperateObservabilityFixtureStartupSeedService(
    IServiceScopeFactory scopeFactory,
    IOptions<OperateObservabilityFixtureOptions> options,
    ILogger<OperateObservabilityFixtureStartupSeedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || !options.Value.SeedOnStartup)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IOperateObservabilityFixtureSeeder>();
        var response = await seeder.SeedAsync(options.Value.Profile, cancellationToken).ConfigureAwait(false);
        SeededOnStartup(logger, response.Profile, response.Investigation.InvestigationId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 120901,
        Level = LogLevel.Information,
        Message = "Seeded Operate observability fixture profile {Profile} on startup with investigation {InvestigationId}.")]
    private static partial void SeededOnStartup(ILogger logger, string profile, string investigationId);
}
