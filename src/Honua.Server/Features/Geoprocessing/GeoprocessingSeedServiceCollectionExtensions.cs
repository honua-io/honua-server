// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Geoprocessing;

internal static class GeoprocessingSeedServiceCollectionExtensions
{
    public static IServiceCollection AddGeoprocessingDefaultServiceSeeding(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<GeoprocessingServiceSeeder>();
        services.AddHostedService<GeoprocessingSeedHostedService>();

        return services;
    }
}

internal static class GeoprocessingSeedConfiguration
{
    public const string SectionName = "Geoprocessing";

    public static bool SeedDefaultService(IConfiguration configuration)
    {
        var sectionValue = configuration[$"{SectionName}:SeedDefaultService"];
        var envValue = configuration["HONUA_GEOPROCESSING_SEED_DEFAULT_SERVICE"];
        var value = !string.IsNullOrWhiteSpace(sectionValue) ? sectionValue : envValue;

        // Default ON: only an explicit "false" disables seeding.
        return string.IsNullOrWhiteSpace(value) || !bool.TryParse(value, out var parsed) || parsed;
    }
}

// A failure to seed the sample service is logged and must not block host startup.
internal sealed partial class GeoprocessingSeedHostedService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<GeoprocessingSeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!GeoprocessingSeedConfiguration.SeedDefaultService(configuration))
        {
            return;
        }

        try
        {
            using var scope = serviceProvider.CreateScope();
            var seeder = scope.ServiceProvider.GetRequiredService<GeoprocessingServiceSeeder>();
            await seeder.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        }
        // Intentional catch-all: as documented on the class above, seeding is a best-effort
        // startup convenience; any failure must not block host startup.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.SeedFailed(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static partial class Log
    {
        [LoggerMessage(9732, LogLevel.Warning,
            "Default geoprocessing GPServer service seed failed; publish a service to enable GPServer")]
        public static partial void SeedFailed(ILogger logger, Exception exception);
    }
}
