// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane;

/// <summary>
/// Named HTTP clients the deploy control plane uses to read telemetry and to probe deploy candidates.
/// </summary>
internal static class ControlPlaneHttpClients
{
    /// <summary>
    /// Telemetry backend queries (Prometheus). Retries and a circuit breaker suit a metrics backend.
    /// </summary>
    public const string Telemetry = "control-plane-telemetry";

    /// <summary>
    /// Health and correctness probes against a deploy candidate. A probe must observe every response
    /// exactly once, so this client has no retry, and a failing candidate must never open a circuit
    /// breaker shared with the telemetry gate that reads its metrics (honua-server#4617).
    /// </summary>
    public const string Probe = "control-plane-probe";

    /// <summary>
    /// Registers <see cref="Telemetry"/> with the fast API resilience defaults and <see cref="Probe"/>
    /// without any resilience policy.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddControlPlaneHttpClients(this IServiceCollection services)
    {
        services.AddResilientHttpClient(Telemetry, Telemetry, HttpResiliencePolicies.FastApiDefaults);
        services.AddHttpClient(Probe);
        return services;
    }
}
