// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.CloudDemo;

internal static class CloudDemoServiceCollectionExtensions
{
    public static IServiceCollection AddCloudDemoServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<CloudDemoServiceSeeder>();
        services.AddHostedService<CloudDemoSeedHostedService>();

        return services;
    }
}

internal sealed class CloudDemoSeedHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<CloudDemoSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!CloudDemoConfiguration.IsEnabled(configuration) ||
            !CloudDemoConfiguration.SeedOnStartup(configuration))
        {
            return;
        }

        try
        {
            using var scope = serviceProvider.CreateScope();
            var seeder = scope.ServiceProvider.GetRequiredService<CloudDemoServiceSeeder>();
            await seeder.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CloudDemoSeedLog.SeedFailed(logger, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static partial class CloudDemoSeedLog
{
    [LoggerMessage(
        EventId = 9720,
        Level = LogLevel.Error,
        Message = "Cloud demo service seed failed")]
    public static partial void SeedFailed(ILogger logger, Exception exception);
}
