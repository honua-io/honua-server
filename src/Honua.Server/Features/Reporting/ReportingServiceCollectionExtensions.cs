// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting;
using Honua.Infrastructure.Security.Admission;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        services.AddAnalysisReportingCore(configuration);
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

    private static bool IsReportingEnabled(IConfigurationSection section)
        => section.GetValue("Enabled", defaultValue: true);
}
