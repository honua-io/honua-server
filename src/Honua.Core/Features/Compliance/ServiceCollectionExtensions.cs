// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Compliance.Abstractions;
using Honua.Core.Features.Compliance.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Compliance;

/// <summary>
/// Registers the compliance framework services: catalog, dependency gate, residency
/// policy, encryption posture / key ring, evidence collector, renderers, and exporter.
/// </summary>
/// <remarks>
/// Registration is idempotent — repeated calls are safe. The compliance services use
/// scoped lifetimes where they depend on scoped dependencies (the audit log), and
/// singletons elsewhere so the dashboard polling endpoints stay allocation-light.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>Add compliance framework services to the container.</summary>
    public static IServiceCollection AddCompliance(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ComplianceOptions>()
            .Bind(configuration.GetSection(ComplianceOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IComplianceControlCatalog, DefaultComplianceControlCatalog>();
        services.TryAddSingleton<IDataResidencyPolicyProvider, DefaultDataResidencyPolicyProvider>();
        services.TryAddSingleton<IEncryptionPostureProvider, InMemoryEncryptionPostureProvider>();

        // The dependency gate inspects scoped services (IAuditLog, IRoleStore, IOidcProviderStore)
        // so it must be scoped too — singleton would capture the root-scope instances forever.
        services.TryAddScoped<IComplianceDependencyGate, DefaultComplianceDependencyGate>();
        services.TryAddScoped<IComplianceEvidenceCollector, DefaultComplianceEvidenceCollector>();
        services.TryAddScoped<IComplianceReportExporter, DefaultComplianceReportExporter>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IComplianceReportRenderer, CsvComplianceReportRenderer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IComplianceReportRenderer, PdfComplianceReportRenderer>());

        return services;
    }
}
