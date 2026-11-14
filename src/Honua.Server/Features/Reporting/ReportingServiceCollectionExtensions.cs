// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting;
using Honua.Infrastructure.Security.Admission;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Features.Reporting;

internal static class ReportingServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisReporting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAiExecutionAdmission(configuration);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ReportingConfiguration.SectionName);
        if (!IsReportingEnabled(section))
        {
            return services;
        }

        services.AddReportStore(configuration);
        services.AddMemoryCache();

        services.TryAddSingleton<IAnalysisReportService, AnalysisReportService>();

        var narrativeEnabled = section.GetValue("Narrative:Enabled", defaultValue: false);
        if (narrativeEnabled)
        {
            services.AddResilientHttpClient(
                "reporting-narrative",
                "reporting-narrative",
                HttpResiliencePolicies.FastApiDefaults with { MaxRetryAttempts = 0 },
                configureHandler: () => new HttpClientHandler { AllowAutoRedirect = false });
            services.TryAddSingleton<INarrativeProvider, OpenAiNarrativeProvider>();
        }

        return services;
    }

    public static IEndpointRouteBuilder MapAnalysisReporting(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        var section = configuration.GetSection(ReportingConfiguration.SectionName);
        if (!IsReportingEnabled(section))
        {
            return endpoints;
        }

        endpoints.MapAnalysisReportEndpoints();
        return endpoints;
    }

    private static void AddReportStore(this IServiceCollection services, IConfiguration configuration)
    {
        var hasCustomStore = services.Any(descriptor => descriptor.ServiceType == typeof(IAnalysisReportStore));
        services.AddAnalysisReportingCore(configuration);
        if (!hasCustomStore)
        {
            var localStore = services.Last(descriptor => descriptor.ServiceType == typeof(IAnalysisReportStore));
            services.Replace(ServiceDescriptor.Singleton<IAnalysisReportStore>(sp =>
                sp.GetService<NpgsqlDataSource>() is not null
                    ? new PostgresAnalysisReportStore(sp.GetRequiredService<JobStoreDatabase>(),
                        sp.GetRequiredService<IOptions<ReportingConfiguration>>())
                    : (IAnalysisReportStore)localStore.ImplementationFactory!(sp)));
        }
    }

    private static bool IsReportingEnabled(IConfigurationSection section)
        => section.GetValue("Enabled", defaultValue: true);
}
