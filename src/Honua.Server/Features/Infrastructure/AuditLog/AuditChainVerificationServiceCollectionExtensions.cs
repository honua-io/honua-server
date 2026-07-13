// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.AuditLog;

/// <summary>
/// DI registration for the scheduled audit hash-chain verifier (#2810).
/// </summary>
internal static class AuditChainVerificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scheduled audit hash-chain verifier and its integrity signal. The signal
    /// singleton is always registered so health checks / findings can inject it unconditionally;
    /// the hosted verification loop no-ops when disabled or when no database-backed verifier is
    /// wired (e.g. non-Postgres / test hosts).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration used to bind verifier options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuditChainVerification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<AuditChainVerificationOptions>()
            .Bind(configuration.GetSection(AuditChainVerificationOptions.SectionName));

        // Register once; expose as the integrity signal source and the hosted verification loop.
        services.AddSingleton<AuditChainVerificationBackgroundService>();
        services.AddSingleton<IAuditChainIntegritySignal>(
            static sp => sp.GetRequiredService<AuditChainVerificationBackgroundService>());
        services.AddHostedService(static sp => sp.GetRequiredService<AuditChainVerificationBackgroundService>());

        return services;
    }
}
