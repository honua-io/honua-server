// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Infrastructure.Crs;

internal sealed partial class PostgresCrsWarmupService : BackgroundService
{
    private static readonly string[] _warmupIdentifiers =
    [
        "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
    ];

    private static readonly int[] _warmupSrids =
    [
        4326,
        3857
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PostgresCrsWarmupService> _logger;

    public PostgresCrsWarmupService(IServiceScopeFactory scopeFactory, ILogger<PostgresCrsWarmupService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<ICrsRegistry>();

            foreach (var identifier in _warmupIdentifiers)
            {
                await registry.ResolveAsync(identifier, stoppingToken).ConfigureAwait(false);
            }

            foreach (var srid in _warmupSrids)
            {
                await registry.ResolveBySridAsync(srid, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.WarmupFailed(_logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7201,
            Level = LogLevel.Warning,
            Message = "CRS warmup failed.")]
        public static partial void WarmupFailed(ILogger logger, Exception exception);
    }
}
