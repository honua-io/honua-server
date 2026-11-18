// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices;

public sealed class ExportTilesArtifactOptions
{
    public const string SectionName = "GeoServices:ExportTiles";

    // Stored archives that anonymous synchronous exportTiles calls may create per task per hour; 0 refuses them.
    public int AnonymousArtifactsPerHour { get; set; } = 30;
}

// Synchronous exportTiles stores one archive per call. Signed-in callers are bounded by their own
// authorization; anonymous callers share a fixed hourly budget per task, so the storage an anonymous
// caller can create is bounded by budget x tasks x artifact lifetime x transfer limit.
internal sealed class ExportTilesArtifactBudget(IOptions<ExportTilesArtifactOptions> options, TimeProvider timeProvider)
{
    public const string RefusedMessage =
        "Anonymous tile exports are limited on this server. Sign in, or try again after the Retry-After interval.";

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _used;

    // Null when the call may store an archive; otherwise the seconds until the anonymous budget resets.
    public int? TryTake(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated == true)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (now - _windowStart >= Window)
            {
                _windowStart = now;
                _used = 0;
            }

            // A negative setting refuses like 0.
            if (_used < Math.Max(0, options.Value.AnonymousArtifactsPerHour))
            {
                _used++;
                return null;
            }

            return (int)Math.Ceiling((_windowStart + Window - now).TotalSeconds);
        }
    }
}

internal static class ExportTilesArtifactBudgetServiceCollectionExtensions
{
    // MapServer and ImageServer share one budget.
    public static IServiceCollection AddExportTilesArtifactBudget(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExportTilesArtifactOptions>(configuration.GetSection(ExportTilesArtifactOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ExportTilesArtifactBudget>();
        return services;
    }
}
