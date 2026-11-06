// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming.Conformance;

// Each record carries its own deadline, and the sweep deletes from the row alone.
internal sealed class FeatureStreamConformanceSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<FeatureStreamConformanceOptions> _options;
    private readonly ILogger<FeatureStreamConformanceSweepService> _logger;

    public FeatureStreamConformanceSweepService(
        IServiceScopeFactory scopeFactory,
        IOptions<FeatureStreamConformanceOptions> options,
        ILogger<FeatureStreamConformanceSweepService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.SweepInterval);
        try
        {
            // Sweep once at startup as well as on the interval: a process that died holding
            // controlled records leaves them behind, and the replacement should not wait a
            // full interval before reclaiming them.
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on host shutdown.
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<FeatureStreamConformanceService>();

            // The canonical edit path resolves the outbox scope and the change-event publish
            // from an ambient request context. A sweep has none, so it supplies a synthetic
            // context bound to this scope — the same approach background dispatchers in other
            // slices use — rather than a second, sweep-local write path.
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            _ = await service.SweepAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // Intentionally generic: a sweep failure must not tear down the host. The next tick
        // retries, and the records remain marked with their deadlines until it succeeds.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureStreamConformanceLog.SweepFailed(_logger, ex);
        }
    }
}
