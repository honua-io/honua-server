// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Operations;

/// <summary>Composes the access-family descriptors and canonical REST-backed executors.</summary>
internal static class AdminAccessOperationsServiceCollectionExtensions
{
    public static IServiceCollection AddAdminAccessOperations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(AdminAccessOperationRegistrationMarker)))
            return services;

        services.TryAddSingleton<OperationLineageAttestationStore>();
        // Keep standalone composition fail-closed while the normal server composition
        // supplies the environment-appropriate encrypted/volatile implementation.
        services.TryAddSingleton<IOperationSecretStore, UnavailableOperationSecretStore>();
        services.AddSingleton<AdminAccessOperationRegistrationMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationDescriptorProvider,
            AdminAccessOperationDescriptorProvider>());
        foreach (var definition in AdminAccessOperationCatalog.Definitions)
        {
            var descriptor = AdminAccessOperationCatalog.Descriptors.Single(
                item => item.OperationId == definition.OperationId);
            if (definition.SideEffect != Honua.Core.Features.Operations.Domain.OperationSideEffectClass.ReadOnly)
            {
                services.AddSingleton<IOperationApprovalRequestMapper>(
                    sp => new AdminOperateOperationApprovalRequestMapper(
                        definition,
                        sp.GetRequiredService<IOperationSecretStore>()));
            }
            services.AddScoped<IOperationExecutor>(sp => new AdminOperateOperationExecutor(
                definition,
                descriptor,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetService<IAdminApiKeyStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<OperationLineageAttestationStore>(),
                sp.GetRequiredService<IOperationSecretStore>()));
        }

        services.AddHttpClient(AdminOperateOperationExecutor.HttpClientName);
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        return services;
    }

    private sealed class AdminAccessOperationRegistrationMarker;
}
