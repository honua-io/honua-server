// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.ServiceDefaults;

/// <summary>
/// Enhanced telemetry extensions providing rich business context and performance milestones
/// for distributed tracing in Honua Server.
/// </summary>
public static class EnhancedTelemetry
{
    // Provider stored for future telemetry consumers
#pragma warning disable IDE0052
    private static Func<int>? _activeDbConnectionsProvider;
#pragma warning restore IDE0052

    /// <summary>
    /// Configures a provider for active database connection counts.
    /// </summary>
    public static void ConfigureActiveDbConnectionsProvider(Func<int> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _activeDbConnectionsProvider = provider;
    }
}
