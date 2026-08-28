// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Registers the Honua Operations Toolset: the grounding catalog (descriptor providers +
/// aggregator), the executors, the policy decision point seam, and the dispatcher.
/// </summary>
internal static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsToolset(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OperationHandleStore>();
        services.TryAddScoped<IOperationApprovalBridge, AdminOperationApprovalBridge>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOperationApprovalRequestMapper, ServicePublishApprovalRequestMapper>());
        if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            services.TryAddSingleton<IOperationInstanceStore, VolatileOperationInstanceStore>();
        }
        else
        {
            services.TryAddSingleton<IOperationInstanceStore>(sp =>
                new RedisOperationInstanceStore(sp.GetRequiredService<IConnectionMultiplexer>()));
            services.AddHostedService<OperationRuntimeStartupValidator>();
            services.AddHostedService<PlannedProposalReconciler>();
        }
        services.TryAddSingleton<IOperationEnvelopeFactory>(sp =>
            new ScopedOperationEnvelopeFactory(
                sp.GetRequiredService<IServiceScopeFactory>(),
                environment.IsDevelopment() || environment.IsEnvironment("Test")));

        // Grounding catalog: descriptor providers aggregated by the catalog.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOperationDescriptorProvider, ServerOperationDescriptorProvider>());
        services.TryAddSingleton<IOperationCatalog>(sp =>
            new OperationCatalog(
                sp.GetServices<IOperationDescriptorProvider>(),
                sp.GetRequiredService<TimeProvider>()));

        // Executors: concrete work, registered as an enumerable for the dispatcher.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOperationExecutor, ServicePublishExecutor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOperationExecutor, AdminServerStatusExecutor>());
        if (services.Any(descriptor => descriptor.ServiceType ==
                typeof(Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor)))
        {
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.Deploy);
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.AdminConfigChange);
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.MetadataRelease);
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.Geoprocess);
        }

        // One policy seam combines typed operation rules with the legacy guardrail ladder.
        // TryAdd preserves an explicitly registered stricter PDP supplied by a host.
        services
            .AddOptions<OperationPolicyOptions>()
            .Bind(configuration.GetSection(OperationPolicyOptions.SectionName));

        services.TryAddSingleton<IOperationPolicyDecisionPoint, CanonicalOperationPolicyDecisionPoint>();

        // Dispatcher: resolves descriptor + executor, runs policy, executes on Allow.
        services.TryAddScoped<IOperationInvoker>(sp =>
            new OperationDispatcher(
                sp.GetRequiredService<IOperationCatalog>(),
                sp.GetServices<IOperationExecutor>(),
                sp.GetRequiredService<IOperationPolicyDecisionPoint>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IOperationApprovalBridge>(),
                sp.GetRequiredService<IOperationInstanceStore>(),
                environment.IsDevelopment() || environment.IsEnvironment("Test")
                    ? new VolatileOperationAuditLog()
                    : sp.GetRequiredService<Honua.Core.Features.AuditLog.Abstractions.IAuditLog>(),
                sp.GetRequiredService<IOperationEnvelopeFactory>()));

        return services;
    }

    private static void AddLegacyAdapter(
        IServiceCollection services,
        Honua.Core.Features.Guardrails.Domain.OperationClass operationClass)
        => services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor>(sp =>
            new LegacyGatewayOperationAdapter(
                sp.GetServices<Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor>()
                    .Single(actuator => actuator.OperationClass == operationClass))));
}
