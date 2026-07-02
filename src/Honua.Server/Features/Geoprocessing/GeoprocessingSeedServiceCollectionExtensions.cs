// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geoprocessing;

internal static class GeoprocessingSeedServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default GPServer service seeder (honua-server#2349). Enabled by
    /// default so a fresh instance is usable out of the box; disable with
    /// <c>Geoprocessing:SeedDefaultService=false</c> (or
    /// <c>HONUA_GEOPROCESSING_SEED_DEFAULT_SERVICE=false</c>).
    /// </summary>
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

/// <summary>
/// Configuration gate for the default GPServer service seed. Enabled unless explicitly
/// turned off, so the out-of-the-box behavior is a usable GPServer facade.
/// </summary>
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

/// <summary>
/// Runs <see cref="GeoprocessingServiceSeeder"/> once at startup. Failures are logged
/// but never thrown: an inability to seed the sample service must not block host
/// startup (an operator can still publish a service to enable GPServer).
/// </summary>
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
        catch (Exception ex)
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
