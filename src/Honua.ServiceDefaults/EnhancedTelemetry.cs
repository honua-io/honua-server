// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.ServiceDefaults;

/// <summary>
/// Enhanced telemetry extensions providing rich business context and performance milestones
/// for distributed tracing in Honua Server.
/// </summary>
public static class EnhancedTelemetry
{
    private static readonly object _gaugeRegistrationLock = new();
    private static volatile Func<int>? _activeDbConnectionsProvider;
    private static bool _gaugeRegistered;

    /// <summary>
    /// Configures a provider for active database connection counts. The provider is
    /// surfaced as the <c>honua.db.connections.active</c> observable gauge on the shared
    /// Honua meter; the gauge is registered once on first configuration.
    /// </summary>
    /// <param name="provider">A callback returning the current active database connection count.</param>
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
