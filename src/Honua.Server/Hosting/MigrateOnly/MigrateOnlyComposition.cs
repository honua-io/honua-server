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

// The migrate-only graph has no web host, so those services cannot start and are not required.
internal static class MigrateOnlyComposition
{
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

    internal static ServiceProvider Build(IConfiguration configuration)
        => Register(new ServiceCollection(), configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}

internal sealed class MigrateOnlyBackupHookRecorder : IDatabaseMigrationBackupHookRecorder
{
    private readonly ConcurrentQueue<DatabaseMigrationBackupHookResult> _results = new();

    public IReadOnlyList<DatabaseMigrationBackupHookResult> Results => _results.ToArray();

    public Task RecordAsync(DatabaseMigrationBackupHookResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results.Enqueue(result);
        return Task.CompletedTask;
    }
}
