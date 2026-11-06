// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Db.Postgres.Features.AuditLog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.AuditLog;

internal static class AuditLogServiceCollectionExtensions
{
    public static IServiceCollection AddHonuaAuditLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Route-to-action resolver used by the audit middleware to decide which
        // operations are audited declaratively, from route metadata (#507).
        services.AddHonuaAuditActionResolver();

        services.TryAddScoped<IAuditLog>(static sp =>
        {
            var connectionProvider = sp.GetService<IAdoNetDatabaseConnectionProvider>();
            if (connectionProvider is null)
            {
                // No database wired in this host — fall back to the no-op sink so
                // call sites can still inject IAuditLog unconditionally.
                return NullAuditLog.Instance;
            }

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new PostgresAuditLog(
                connectionProvider,
                loggerFactory.CreateLogger<PostgresAuditLog>(),
                chainKey: sp.GetService<AuditChainKeySnapshot>()?.Key ?? ReadOnlyMemory<byte>.Empty);
        });

        return services;
    }
}
