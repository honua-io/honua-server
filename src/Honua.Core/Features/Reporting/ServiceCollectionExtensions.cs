// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Services;
using Honua.Core.Features.Reporting.Templates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Reporting;

/// <summary>
/// Service registration helpers for the analysis-reporting feature. Registers
/// the deterministic narrative provider, builders, renderers, in-memory store,
/// and the four built-in family templates plus the generic fallback.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the deterministic reporting pipeline. Server-side wiring (LLM
    /// narrative provider, MCP resource, endpoints) is layered on top via
    /// <c>Honua.Server.Features.Reporting</c>.
    /// </summary>
    public static IServiceCollection AddAnalysisReportingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ReportingConfiguration>()
            .Bind(configuration.GetSection(ReportingConfiguration.SectionName))
            .PostConfigure(options =>
            {
                if (string.IsNullOrWhiteSpace(options.Narrative.ApiKey))
                {
                    var envKey = Environment.GetEnvironmentVariable("HONUA_REPORTING_NARRATIVE_API_KEY");
                    if (!string.IsNullOrWhiteSpace(envKey))
                    {
                        options.Narrative.ApiKey = envKey;
                    }
                }
            })
            .ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<ReportingConfiguration>, ReportingConfigurationValidator>();

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<DeterministicNarrativeProvider>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportTemplate, GenericAnalysisReportTemplate>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportTemplate, AnalyticsBufferAggregateReportTemplate>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportTemplate, AnalyticsDensityReportTemplate>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportTemplate, SurfaceSlopeReportTemplate>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportTemplate, GeneralizationDissolveReportTemplate>());

        services.TryAddSingleton<IAnalysisReportTemplateRegistry, AnalysisReportTemplateRegistry>();
        services.TryAddSingleton<IAnalysisReportBuilder, AnalysisReportBuilder>();
        services.TryAddSingleton<IAnalysisReportStore, InMemoryAnalysisReportStore>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportRenderer, MarkdownReportRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAnalysisReportRenderer, HtmlReportRenderer>());

        return services;
    }
}
