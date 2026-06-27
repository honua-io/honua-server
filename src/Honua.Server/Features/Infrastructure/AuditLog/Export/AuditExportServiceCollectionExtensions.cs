// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Export;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.AuditLog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// DI registration for the audit SIEM export feature (#2157). Wires the shared
/// dispatcher, dead-letter store, residency guard, and any enabled connector
/// sinks (Splunk HEC, Microsoft Sentinel, S3, syslog).
/// </summary>
/// <remarks>
/// Disabled by default — when <c>AuditLog:Export:Enabled</c> is <c>false</c> the
/// inert dispatcher/store/guard are still registered but no sinks are added, so
/// the audit trail behaves exactly as before. This method is intentionally not
/// called from <c>Program.cs</c> here; the host wires it explicitly.
/// </remarks>
internal static class AuditExportServiceCollectionExtensions
{
    private const string SplunkClient = "honua-audit-splunk";
    private const string SentinelClient = "honua-audit-sentinel";
    private const string S3Client = "honua-audit-s3";

    /// <summary>
    /// Registers the audit export pipeline and enabled sinks.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration root used to bind <see cref="AuditExportOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHonuaAuditExport(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(AuditExportOptions.SectionName);
        services.Configure<AuditExportOptions>(section);

        var options = new AuditExportOptions();
        section.Bind(options);

        // Shared, inert-without-sinks infrastructure.
        services.TryAddSingleton<IAuditDeadLetterStore, InMemoryAuditDeadLetterStore>();
        services.TryAddSingleton(_ => new AuditResidencyGuard(options.AllowedRegions));
        services.TryAddSingleton(sp => new AuditExportDispatcher(
            options.Dispatch,
            sp.GetRequiredService<IAuditDeadLetterStore>(),
            sp.GetRequiredService<ILogger<AuditExportDispatcher>>(),
            sp.GetRequiredService<AuditResidencyGuard>()));

        // Retention is independent of the SIEM push sinks: a bounded retention
        // window (RetentionDays > 0) schedules a background sweep that prunes
        // expired audit records even when no export sink is enabled (#509). A
        // Postgres-backed pruner is used when a database is wired; otherwise the
        // no-op pruner keeps the sweep inert (tests / non-database hosts).
        if (options.ToRetentionPolicy().IsBounded)
        {
            var pruneBatchSize = options.RetentionPruneBatchSize;
            services.TryAddScoped<IAuditRetentionPruner>(sp =>
            {
                var connectionProvider = sp.GetService<IAdoNetDatabaseConnectionProvider>();
                if (connectionProvider is null)
                {
                    return NullAuditRetentionPruner.Instance;
                }

                return new PostgresAuditLogRetentionPruner(
                    connectionProvider,
                    sp.GetRequiredService<ILogger<PostgresAuditLogRetentionPruner>>(),
                    schemaName: null,
                    batchSize: pruneBatchSize);
            });

            services.AddHostedService<AuditRetentionPruneService>();
        }

        if (!options.Enabled)
        {
            return services;
        }

        if (options.Splunk.Enabled)
        {
            services.AddHttpClient(SplunkClient);
            services.AddSingleton<IAuditSink>(sp => new SplunkHecAuditSink(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(SplunkClient),
                options.Splunk));
        }

        if (options.Sentinel.Enabled)
        {
            services.AddHttpClient(SentinelClient);
            services.AddSingleton<IAuditSink>(sp => new SentinelAuditSink(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(SentinelClient),
                options.Sentinel));
        }

        if (options.S3.Enabled)
        {
            services.AddHttpClient(S3Client);
            services.AddSingleton<IAuditSink>(sp => new S3AuditSink(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(S3Client),
                options.S3));
        }

        if (options.Syslog.Enabled)
        {
            services.AddSingleton<IAuditSink>(_ => new SyslogAuditSink(options.Syslog));
        }

        return services;
    }
}
