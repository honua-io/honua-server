// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Fails release startup unless the canonical operation runtime has durable stores, audit,
/// a non-pass-through policy decision point, and exactly one actuator per descriptor.
/// </summary>
public sealed class OperationRuntimeStartupValidator(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var instanceStore = services.GetService<IOperationInstanceStore>();
        if (instanceStore is null or VolatileOperationInstanceStore)
        {
            throw new InvalidOperationException(
                "Production operation runtime requires a durable IOperationInstanceStore.");
        }

        if (services.GetService<Honua.Core.Features.ControlPlane.Abstractions.IOperationProposalStore>() is null)
        {
            throw new InvalidOperationException(
                "Production operation runtime requires a durable IOperationProposalStore.");
        }

        var audit = services.GetService<IAuditLog>();
        if (audit is null || !audit.IsPersisted)
        {
            throw new InvalidOperationException(
                "Production operation runtime requires a durable IAuditLog sink.");
        }

        var policy = services.GetService<IOperationPolicyDecisionPoint>();
        if (policy is null or AllowAllPolicyDecisionPoint)
        {
            throw new InvalidOperationException(
                "Production operation runtime requires a fail-closed policy decision point.");
        }

        if (policy is CanonicalOperationPolicyDecisionPoint &&
            services.GetRequiredService<IOptions<OperationPolicyOptions>>().Value.Enabled is false)
        {
            throw new InvalidOperationException(
                "Production operation runtime requires Operations:Policy:Enabled=true.");
        }

        var catalog = services.GetRequiredService<IOperationCatalog>();
        var descriptors = (await catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Operations;
        var mapperCounts = OperationDescriptorPublication.CountMappings(
            services.GetServices<IOperationApprovalRequestMapper>());
        var unsafePublicDescriptors = descriptors
            .Where(descriptor =>
                !descriptor.IsCompatibilityOnly &&
                descriptor.ApprovalModel != Honua.Core.Features.Operations.Domain.OperationApprovalModel.None &&
                !OperationDescriptorPublication.CanAdvertise(descriptor, mapperCounts))
            .Select(descriptor => descriptor.OperationId)
            .ToArray();
        if (unsafePublicDescriptors.Length != 0)
        {
            throw new InvalidOperationException(
                $"Production operation runtime requires exactly one safe approval mapper for: {string.Join(", ", unsafePublicDescriptors)}.");
        }

        var counts = services.GetServices<IOperationExecutor>()
            .GroupBy(executor => executor.OperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var invalid = descriptors
            .Where(descriptor => !counts.TryGetValue(descriptor.OperationId, out var count) || count != 1)
            .Select(descriptor => descriptor.OperationId)
            .ToArray();
        if (invalid.Length != 0)
        {
            throw new InvalidOperationException(
                $"Production operation runtime requires exactly one actuator for: {string.Join(", ", invalid)}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
