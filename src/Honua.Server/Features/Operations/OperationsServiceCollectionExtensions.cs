// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Authentication;
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
        services.TryAddSingleton<OperationLineageAttestationStore>();
        services.TryAddScoped<IOperationApprovalBridge, AdminOperationApprovalBridge>();
        // The real verifier's constructor requires the durable proposal store, and the
        // dispatcher requires a verifier, so hosts composed without the store failed
        // ValidateOnBuild at boot (trunk red 2026-08-29, run 33241561197 — third of
        // the family after #3614/#3617). Selection happens at RESOLUTION time, not
        // registration time: test fixtures legitimately add the store after this
        // method runs (post-Program ConfigureServices), and a registration-time
        // snapshot would lock them onto the refusing verifier forever. Hosts whose
        // final composition lacks the store get the fail-closed verifier: replay can
        // never verify without the durable authority (ruling 4).
        services.TryAddScoped<IOperationApprovalReplayVerifier>(sp =>
            sp.GetService<Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore>() is { } proposalStore
                ? new OperationApprovalReplayVerifier(proposalStore)
                : new UnavailableOperationApprovalReplayVerifier());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOperationApprovalRequestMapper, ServicePublishApprovalRequestMapper>());
        foreach (var operationId in new[]
                 {
                     StudioDraftOperations.Create,
                     StudioDraftOperations.Update,
                     StudioDraftOperations.Delete,
                     StudioDraftOperations.Validate,
                     StudioDraftOperations.PreviewPlan,
                     StudioDraftOperations.SaveVersion,
                     StudioDraftOperations.CreatePublicationRequest,
                     StudioDraftOperations.ReopenVersion,
                     StudioDraftOperations.Rollback,
                 })
        {
            services.AddSingleton<IOperationApprovalRequestMapper>(
                new StudioDraftApprovalRequestMapper(operationId));
        }
        if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            services.TryAddSingleton<IOperationInstanceStore, VolatileOperationInstanceStore>();
        }
        else
        {
            services.TryAddSingleton<IOperationInstanceStore>(sp =>
                new RedisOperationInstanceStore(sp.GetRequiredService<IConnectionMultiplexer>()));
            if (services.Any(descriptor => descriptor.ServiceType ==
                    typeof(Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore)))
            {
                services.AddHostedService<OperationRuntimeStartupValidator>();
                services.AddHostedService<PlannedProposalReconciler>();
                services.AddHostedService<QueuedOperationReconciler>();
            }
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
            ServiceDescriptor.Scoped<IOperationExecutor, DeferredServicePublishExecutor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOperationExecutor, AdminServerStatusExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioDraftCreateExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioDraftUpdateExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioDraftDeleteExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioDraftValidateExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioDraftPreviewPlanExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioSaveVersionExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioCreatePublicationRequestExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioReopenVersionExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, StudioRollbackExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, DeployRollbackOperationExecutor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOperationExecutor, CoordinatedReleaseRollbackOperationExecutor>());
        services.TryAddScoped<IStudioDraftMutationRuntime, StudioDraftMutationRuntime>();

        var hasProposalStore = services.Any(descriptor => descriptor.ServiceType ==
            typeof(Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore));
        if (hasProposalStore &&
            !services.Any(descriptor => descriptor.ServiceType == typeof(AdminConnectImportRegistrationMarker)))
        {
            services.AddSingleton<AdminConnectImportRegistrationMarker>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationDescriptorProvider,
                AdminConnectImportOperationDescriptorProvider>());
            foreach (var definition in AdminConnectImportOperationCatalog.Definitions)
            {
                if (definition.SideEffect != Honua.Core.Features.Operations.Domain.OperationSideEffectClass.ReadOnly)
                {
                    services.AddSingleton<IOperationApprovalRequestMapper>(
                        new AdminConnectImportApprovalRequestMapper(definition));
                }
                services.AddScoped<IOperationExecutor>(sp => new AdminConnectImportOperationExecutor(
                    definition,
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IHttpContextAccessor>(),
                    sp.GetRequiredService<Honua.Infrastructure.Authentication.IAdminApiKeyStore>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<OperationLineageAttestationStore>()));
            }
            services.AddHttpClient(AdminConnectImportOperationExecutor.HttpClientName);
            services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        }

        if (hasProposalStore &&
            !services.Any(descriptor => descriptor.ServiceType == typeof(AdminApiOperationRegistrationMarker)))
        {
            services.AddSingleton<AdminApiOperationRegistrationMarker>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationDescriptorProvider,
                AdminApiOperationDescriptorProvider>());
            foreach (var definition in AdminApiOperationCatalog.Definitions)
            {
                if (definition.Destructive)
                {
                    services.AddSingleton<IOperationApprovalRequestMapper>(
                        new AdminApiOperationApprovalRequestMapper(definition));
                }
                services.AddScoped<IOperationExecutor>(sp => new AdminApiOperationExecutor(
                    definition,
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IHttpContextAccessor>(),
                    sp.GetRequiredService<Honua.Infrastructure.Authentication.IAdminApiKeyStore>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<OperationLineageAttestationStore>()));
            }
            services.AddHttpClient(AdminApiOperationExecutor.HttpClientName);
            services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        }

        // Legacy adapters register UNCONDITIONALLY and resolve their control-plane
        // actuator at USE time. The former registration-time services.Any gate was
        // an ordering snapshot: hosts that register control-plane executors after
        // AddOperationsToolset (post-Program ConfigureServices) got no adapters, so
        // the dispatcher had no compatibility actuator and converge returned
        // NotSupported with null operation ids (trunk red 2026-08-29, run
        // 33249627814 — same ordering class the #3621 review caught for the replay
        // verifier). The marker keeps repeated AddOperationsToolset calls
        // idempotent; degraded hosts get a typed use-time refusal from the adapter.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(LegacyAdapterRegistrationMarker)))
        {
            services.AddSingleton<LegacyAdapterRegistrationMarker>();
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.Deploy);
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.AdminConfigChange);
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.MetadataRelease);
            AddLegacyAdapter(services, Honua.Core.Features.Guardrails.Domain.OperationClass.Geoprocess);
        }

        foreach (var definition in AdminOperateOperationCatalog.Definitions)
        {
            var descriptor = AdminOperateOperationCatalog.Descriptors.Single(
                item => item.OperationId == definition.OperationId);
            if (definition.ApprovalModel != Honua.Core.Features.Operations.Domain.OperationApprovalModel.None &&
                definition.SideEffect != Honua.Core.Features.Operations.Domain.OperationSideEffectClass.ReadOnly)
            {
                services.AddSingleton<IOperationApprovalRequestMapper>(
                    new AdminOperateOperationApprovalRequestMapper(definition));
            }
            services.AddScoped<IOperationExecutor>(sp => new AdminOperateOperationExecutor(
                definition,
                descriptor,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetService<IAdminApiKeyStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<OperationLineageAttestationStore>()));
        }

        services.AddHttpClient(AdminOperateOperationExecutor.HttpClientName);
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

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
                sp.GetService<Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore>() is null
                    ? null
                    : sp.GetRequiredService<IOperationApprovalReplayVerifier>()));

        return services;
    }

    private static void AddLegacyAdapter(
        IServiceCollection services,
        Honua.Core.Features.Guardrails.Domain.OperationClass operationClass)
        => services.AddScoped<IOperationExecutor>(sp =>
            new LegacyGatewayOperationAdapter(sp, operationClass));

    /// <summary>
    /// Registration sentinel proving the legacy gateway adapters were already added, so
    /// repeated <see cref="AddOperationsToolset"/> calls stay idempotent without
    /// <c>TryAddEnumerable</c> (which cannot hold factory descriptors).
    /// </summary>
    internal sealed class LegacyAdapterRegistrationMarker;

    internal sealed class AdminConnectImportRegistrationMarker;
    internal sealed class AdminApiOperationRegistrationMarker;
}
