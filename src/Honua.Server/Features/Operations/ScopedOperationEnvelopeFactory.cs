// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Singleton-safe envelope factory that resolves the scoped audit sink for each durable
/// acceptance instead of allowing singleton mutation callers to capture a root scope.
/// </summary>
internal sealed class ScopedOperationEnvelopeFactory(
    IServiceScopeFactory scopeFactory,
    bool useVolatileAudit) : IOperationEnvelopeFactory
{
    public async Task<OperationHandle> CreateAcceptedAsync(
        string operationId,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        context = BindRequestTenant(context, services);
        var factory = new OperationEnvelopeFactory(
            services.GetRequiredService<IOperationInstanceStore>(),
            useVolatileAudit
                ? new VolatileOperationAuditLog()
                : services.GetRequiredService<IAuditLog>(),
            services.GetRequiredService<TimeProvider>());
        return await factory.CreateAcceptedAsync(operationId, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationHandle> CompleteCacheHitAsync(
        string operationId,
        OperationPolicyContext context,
        string sourceOperationInstanceId,
        string? sourceAuditId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        context = BindRequestTenant(context, services);
        var factory = new OperationEnvelopeFactory(
            services.GetRequiredService<IOperationInstanceStore>(),
            useVolatileAudit
                ? new VolatileOperationAuditLog()
                : services.GetRequiredService<IAuditLog>(),
            services.GetRequiredService<TimeProvider>());
        return await factory.CompleteCacheHitAsync(
                operationId,
                context,
                sourceOperationInstanceId,
                sourceAuditId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static OperationPolicyContext BindRequestTenant(OperationPolicyContext context, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Replay carries its sealed context. New HTTP acceptances, including callers
        // that pre-create an envelope, use the tenant resolved for that request.
        if (!string.IsNullOrWhiteSpace(context.ApprovedProposalId)) return context;
        var request = services.GetService<IHttpContextAccessor>()?.HttpContext;
        return request is null ? context : context with
        {
            TenantId = request.RequestServices.GetService<ITenantContext>()?.TenantId,
        };
    }
}
