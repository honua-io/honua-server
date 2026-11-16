// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Realtime;

internal sealed class RealtimeDrainHostedService(
    IRealtimeStreamLifecycle lifecycle,
    IHostApplicationLifetime? lifetime) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = lifetime?.ApplicationStopping.Register(lifecycle.BeginDrain) ?? default;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => lifecycle.DrainAsync(cancellationToken);

    public void Dispose() => _stopping.Dispose();
}
