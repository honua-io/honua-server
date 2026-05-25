// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal static class OperateObservabilityFixtureServiceCollectionExtensions
{
    public static IServiceCollection AddOperateObservabilityFixtures(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services
            .AddOptions<OperateObservabilityFixtureOptions>()
            .Bind(configuration.GetSection(OperateObservabilityFixtureOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OperateObservabilityFixtureOptions>,
                OperateObservabilityFixtureOptionsValidator>());

        var options = new OperateObservabilityFixtureOptions();
        configuration.GetSection(OperateObservabilityFixtureOptions.SectionName).Bind(options);
        var validation = new OperateObservabilityFixtureOptionsValidator(environment, configuration).Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                nameof(OperateObservabilityFixtureOptions),
                typeof(OperateObservabilityFixtureOptions),
                validation.Failures);
        }

        if (!options.Enabled)
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IOperateObservabilityFixtureSeeder, OperateObservabilityFixtureSeeder>();

        var hasJobStore = services.Any(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));
        var hasLogStore = services.Any(descriptor => descriptor.ServiceType == typeof(IExecutionLogStore));

        // Scoped so the store shares the request/seed scope's IDatabaseConnectionProvider
        // (and its ISchemaContext search-path). A singleton would capture the scoped
        // connection provider and would also seed jobs/logs into a different schema than
        // the seeder's direct alert/investigation writes. The fixture replaces these stores
        // only when Redis-backed implementations are absent, and their non-fixture consumers
        // (ConsoleJobService) resolve them per scope, so scoped lifetime is safe here.
        if (!hasJobStore || !hasLogStore)
        {
            services.TryAddScoped<PostgresOperateFixtureExecutionStore>();
        }

        if (!hasJobStore)
        {
            services.AddScoped<IExecutionJobStore>(sp => sp.GetRequiredService<PostgresOperateFixtureExecutionStore>());
        }

        if (!hasLogStore)
        {
            services.AddScoped<IExecutionLogStore>(sp => sp.GetRequiredService<PostgresOperateFixtureExecutionStore>());
        }

        if (options.SeedOnStartup)
        {
            services.AddHostedService<OperateObservabilityFixtureStartupSeedService>();
        }

        return services;
    }
}
