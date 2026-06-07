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
            // Honour the flat HONUA_ENABLE_OBSERVABILITY_TEST_SEED alias everywhere the bound
            // options are consumed (endpoint mapping, validator, startup seed service) (#1229).
            .PostConfigure(opts => opts.Enabled =
                OperateObservabilityFixtureOptions.ResolveEnabled(configuration, opts.Enabled))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<OperateObservabilityFixtureOptions>,
                OperateObservabilityFixtureOptionsValidator>());

        var options = new OperateObservabilityFixtureOptions();
        configuration.GetSection(OperateObservabilityFixtureOptions.SectionName).Bind(options);
        options.Enabled = OperateObservabilityFixtureOptions.ResolveEnabled(configuration, options.Enabled);
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

        // Singleton to match existing geoprocessing and job-orchestration consumers. The
        // concrete fixture store creates a short child scope per operation, so it does not
        // capture the scoped IDatabaseConnectionProvider while preserving schema routing.
        if (!hasJobStore || !hasLogStore)
        {
            services.TryAddSingleton<PostgresOperateFixtureExecutionStore>();
        }

        if (!hasJobStore)
        {
            services.AddSingleton<IExecutionJobStore>(sp => sp.GetRequiredService<PostgresOperateFixtureExecutionStore>());
        }

        if (!hasLogStore)
        {
            services.AddSingleton<IExecutionLogStore>(sp => sp.GetRequiredService<PostgresOperateFixtureExecutionStore>());
        }

        if (options.SeedOnStartup)
        {
            services.AddHostedService<OperateObservabilityFixtureStartupSeedService>();
        }

        return services;
    }
}
