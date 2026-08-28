// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
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
        var factory = new OperationEnvelopeFactory(
            services.GetRequiredService<IOperationInstanceStore>(),
            useVolatileAudit
                ? new VolatileOperationAuditLog()
                : services.GetRequiredService<IAuditLog>(),
            services.GetRequiredService<TimeProvider>());
        return await factory.CreateAcceptedAsync(operationId, context, cancellationToken).ConfigureAwait(false);
    }
}
