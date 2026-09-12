// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Keeps discovery and dispatcher composition independent of actuator dependencies.
/// The scoped DI container owns the selected actuator and its disposal.
/// </summary>
internal sealed class DeferredOperationExecutor(string operationId, Func<IOperationExecutor> resolve)
    : IOperationExecutor, IOperationRequestPreparer
{
    private readonly Lazy<IOperationExecutor> _executor = new(() =>
    {
        var executor = resolve();
        if (!string.Equals(operationId, executor.OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The operation actuator registration has a mismatched identity.");
        }

        return executor;
    });

    public string OperationId => operationId;

    public Task<OperationRequest> PrepareAsync(
        OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
        => _executor.Value is IOperationRequestPreparer preparer
            ? preparer.PrepareAsync(request, context, cancellationToken)
            : Task.FromResult(request);

    public Task<OperationValidation> ValidateAsync(
        OperationRequest request, CancellationToken cancellationToken = default)
        => _executor.Value.ValidateAsync(request, cancellationToken);

    public Task<OperationHandle> SubmitAsync(
        OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
        => _executor.Value.SubmitAsync(request, context, cancellationToken);

    public Task<OperationStatus> GetStatusAsync(
        OperationHandle handle, CancellationToken cancellationToken = default)
        => _executor.Value.GetStatusAsync(handle, cancellationToken);
}

/// <summary>Registers operation identities without constructing their actuator graphs.</summary>
internal static class DeferredOperationExecutorServiceCollectionExtensions
{
    public static IServiceCollection TryAddDeferredOperationExecutor<TExecutor>(
        this IServiceCollection services, string operationId)
        where TExecutor : class, IOperationExecutor
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IOperationExecutor) &&
                descriptor.IsKeyedService && Equals(descriptor.ServiceKey, operationId)))
        {
            return services;
        }

        services.TryAdd(ServiceDescriptor.KeyedScoped<IOperationExecutor, TExecutor>(operationId));
        return AddProjection(services, operationId);
    }

    public static IServiceCollection AddDeferredOperationExecutor(
        this IServiceCollection services, string operationId, Func<IServiceProvider, IOperationExecutor> factory)
    {
        services.AddKeyedScoped<IOperationExecutor>(operationId, (provider, _) => factory(provider));
        return AddProjection(services, operationId);
    }

    private static IServiceCollection AddProjection(IServiceCollection services, string operationId)
        => services.AddScoped<IOperationExecutor>(provider => new DeferredOperationExecutor(
            operationId, () => provider.GetRequiredKeyedService<IOperationExecutor>(operationId)));
}
