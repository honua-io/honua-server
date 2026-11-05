// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane;

internal static class ControlPlaneHttpClients
{
    public const string Telemetry = "control-plane-telemetry";

    // A probe observes every response once, so this client does not retry, and a failing candidate must not open a circuit breaker shared with the telemetry gate.
    public const string Probe = "control-plane-probe";

    public static IServiceCollection AddControlPlaneHttpClients(this IServiceCollection services)
    {
        services.AddResilientHttpClient(Telemetry, Telemetry, HttpResiliencePolicies.FastApiDefaults);
        services.AddHttpClient(Probe);
        return services;
    }
}
