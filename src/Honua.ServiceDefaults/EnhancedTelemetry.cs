// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.ServiceDefaults;

public static class EnhancedTelemetry
{
    private static readonly object _gaugeRegistrationLock = new();
    private static volatile Func<int>? _activeDbConnectionsProvider;
    private static bool _gaugeRegistered;

    public static void ConfigureActiveDbConnectionsProvider(Func<int> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _activeDbConnectionsProvider = provider;

        if (_gaugeRegistered)
        {
            return;
        }

        lock (_gaugeRegistrationLock)
        {
            if (_gaugeRegistered)
            {
                return;
            }

            HonuaTelemetry.Meter.CreateObservableGauge(
                "honua.db.connections.active",
                static () => _activeDbConnectionsProvider?.Invoke() ?? 0,
                unit: "{connection}",
                description: "Number of active database connections currently in use.");

            _gaugeRegistered = true;
        }
    }
}
