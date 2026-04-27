// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Reporting;

/// <summary>
/// DI registration for the reporting feature on the server-host side. Wires
/// the report service and (when enabled) the OpenAI narrative provider on top
/// of the deterministic core registration. The MCP adapter for the
/// <c>honua://jobs/{jobId}/report</c> resource is registered alongside the
/// other MCP resources by <c>McpServiceCollectionExtensions</c> so the
/// protocol-adapter dependency direction stays Mcp → Reporting.
/// </summary>
internal static class ReportingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the analysis-reporting feature when
    /// <see cref="ReportingConfiguration.Enabled"/> is true (the default).
    /// </summary>
    public static IServiceCollection AddAnalysisReporting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
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
                HttpResiliencePolicies.FastApiDefaults);
            services.TryAddSingleton<INarrativeProvider, OpenAiNarrativeProvider>();
        }

        return services;
    }

    /// <summary>
    /// Maps the analysis-report HTTP endpoints when
    /// <see cref="ReportingConfiguration.Enabled"/> is true. Mirrors the
    /// convention used by other feature slices (a separate map call from the
    /// registration).
    /// </summary>
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
