// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Infrastructure.AuditLog;

internal static class AuditChainVerificationServiceCollectionExtensions
{
    public static IServiceCollection AddAuditChainVerification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // A present key that is not usable fails startup. An absent key leaves
        // verification unable to succeed for a hashed chain; writes continue.
        services.TryAddSingleton(new AuditChainKeySnapshot(
            configuration[AuditChainVerificationOptions.SectionName + ":Key"]));

        services
            .AddOptions<AuditChainVerificationOptions>()
            .Bind(configuration.GetSection(AuditChainVerificationOptions.SectionName));

        // Register once; expose as the integrity signal source and the hosted verification loop.
        services.AddSingleton<AuditChainVerificationBackgroundService>();
        services.AddSingleton<IAuditChainIntegritySignal>(
            static sp => sp.GetRequiredService<AuditChainVerificationBackgroundService>());
        // The verification pass opens a connection on its interval. Skip it on the
        // event-handler Lambdas, whose pool is released when the invocation ends.
        if (ControlPlaneLambdaConnectionRelease.HostsDatabasePollingLoops(configuration))
        {
            services.AddHostedService(static sp => sp.GetRequiredService<AuditChainVerificationBackgroundService>());
        }

        return services;
    }
}
