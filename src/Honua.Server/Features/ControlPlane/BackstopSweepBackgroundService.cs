// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal abstract class BackstopSweepBackgroundService(
    IOptions<ControlPlaneTriggerOptions> options) : BackgroundService
{
    private readonly ControlPlaneTriggerOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.BackstopInterval > TimeSpan.Zero
            ? _options.BackstopInterval
            : TimeSpan.FromMinutes(2);
        var staleThreshold = _options.StaleThreshold > TimeSpan.Zero
            ? _options.StaleThreshold
            : TimeSpan.FromSeconds(90);

        LogBackstopStarted((int)interval.TotalSeconds, (int)staleThreshold.TotalSeconds);

        return PeriodicBackgroundLoop.RunAsync(
            new PeriodicBackgroundLoopOptions
            {
                Interval = interval,
                ExecuteIterationAsync = ct => ExecuteSweepAsync(staleThreshold, ct),
                OnIterationError = LogBackstopSweepFailed
            },
            stoppingToken);
    }

    protected abstract void LogBackstopStarted(int intervalSeconds, int staleSeconds);

    protected abstract void LogBackstopSweepFailed(Exception exception);

    protected abstract Task ExecuteSweepAsync(TimeSpan staleThreshold, CancellationToken cancellationToken);
}
