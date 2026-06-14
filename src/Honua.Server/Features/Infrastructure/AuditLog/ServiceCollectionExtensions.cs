// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Postgres.Features.AuditLog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Infrastructure.AuditLog;

/// <summary>
/// DI registration for the audit log feature (#1144).
/// </summary>
/// <remarks>
/// <para>
/// Registers <see cref="IAuditLog"/> with a Postgres-backed implementation when
/// the <see cref="IDatabaseConnectionProvider"/> is available, falling back to
/// <see cref="NullAuditLog"/> otherwise. The fallback is intentional: tests and
/// non-database hosts (e.g. unit-test compositions, certain demo modes) should
/// still resolve an <see cref="IAuditLog"/> rather than crash on DI validation.
/// </para>
/// <para>
/// The registration uses <c>TryAdd</c> so a host that wants to plug in an
/// alternative sink (S3, OpenTelemetry log signals, Kafka, ...) can register
/// its own implementation before calling this method.
/// </para>
/// </remarks>
internal static class AuditLogServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IAuditLog"/> for security-relevant event persistence.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHonuaAuditLog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Route-to-action resolver used by the audit middleware to decide which
        // operations are audited declaratively, from route metadata (#507).
        services.AddHonuaAuditActionResolver();

        services.TryAddScoped<IAuditLog>(static sp =>
        {
            var connectionProvider = sp.GetService<IDatabaseConnectionProvider>();
            if (connectionProvider is null)
            {
                // No database wired in this host — fall back to the no-op sink so
                // call sites can still inject IAuditLog unconditionally.
                return NullAuditLog.Instance;
            }

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new PostgresAuditLog(
                connectionProvider,
                loggerFactory.CreateLogger<PostgresAuditLog>());
        });

        return services;
    }
}
