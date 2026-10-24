// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres;
using Honua.Db.Postgres.Features.Security;
using Honua.Server.Startup;
using Microsoft.Extensions.Logging.Console;

namespace Honua.Server.Hosting.MigrateOnly;

/// <summary>
/// The service graph a migrate-only run composes (honua-server#6470): logging to standard error,
/// the connection-string secret resolver chain, and the PostgreSQL schema guard, migration runner,
/// migration-safety options and PostGIS preflight, registered by the same
/// <c>AddPostgresMigrationServices</c> call the serving composition makes. It deliberately has no
/// web host, Kestrel server, hosted or background services, Redis, licensing, admin credential,
/// encryption key or data providers, so none of them can start or be required.
/// </summary>
internal static class MigrateOnlyComposition
{
    /// <summary>Registers the migrate-only services on <paramref name="services"/>.</summary>
    /// <param name="services">An empty service collection.</param>
    /// <param name="configuration">The process configuration.</param>
    /// <returns>The same collection.</returns>
    internal static IServiceCollection Register(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(configuration);
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            // Standard output carries only the machine-readable summary.
            logging.AddSimpleConsole(options => options.SingleLine = true);
            logging.Services.Configure<ConsoleLoggerOptions>(options =>
                options.LogToStandardErrorThreshold = LogLevel.Trace);
        });

        services.AddConnectionSecretResolution(configuration);
        services.AddPostgresMigrationServices(configuration, ServerCoreSchemaMigrations.Manifest);
        services.AddSingleton<MigrateOnlyBackupHookRecorder>();
        services.AddSingleton<IDatabaseMigrationBackupHookRecorder>(serviceProvider =>
            serviceProvider.GetRequiredService<MigrateOnlyBackupHookRecorder>());
        return services;
    }

    /// <summary>Builds the migrate-only service provider, validating every registration.</summary>
    /// <param name="configuration">The process configuration.</param>
    /// <returns>A provider the caller disposes.</returns>
    internal static ServiceProvider Build(IConfiguration configuration)
        => Register(new ServiceCollection(), configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}

/// <summary>
/// Keeps the backup-hook outcomes of a migrate-only run for its summary. The serving host records
/// them to the audit log, which needs the audit store; a migrate-only run reports them on standard
/// output instead.
/// </summary>
internal sealed class MigrateOnlyBackupHookRecorder : IDatabaseMigrationBackupHookRecorder
{
    private readonly ConcurrentQueue<DatabaseMigrationBackupHookResult> _results = new();

    /// <summary>Outcomes recorded so far, oldest first.</summary>
    public IReadOnlyList<DatabaseMigrationBackupHookResult> Results => _results.ToArray();

    /// <inheritdoc />
    public Task RecordAsync(DatabaseMigrationBackupHookResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results.Enqueue(result);
        return Task.CompletedTask;
    }
}
