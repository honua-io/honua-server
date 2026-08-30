// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Ai.Protocols.Mcp;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Defers construction of the service-publish executor until that operation is selected.
/// Protocol-only and in-memory hosts can therefore use unrelated canonical operations without
/// composing the database-backed publishing graph. Selecting service.publish in such a host
/// returns a typed, actionable validation failure instead of failing the entire dispatcher.
/// </summary>
internal sealed class DeferredServicePublishExecutor(IServiceProvider services) : IOperationExecutor
{
    public string OperationId => ServicePublishOperation.OperationId;

    public Task<OperationValidation> ValidateAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(out var executor, out var unavailable))
        {
            return Task.FromResult(unavailable!);
        }

        return executor!.ValidateAsync(request, cancellationToken);
    }

    public async Task<OperationHandle> SubmitAsync(
        OperationRequest request,
        OperationPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (TryResolve(out var executor, out var unavailable))
        {
            return await executor!.SubmitAsync(request, context, cancellationToken).ConfigureAwait(false);
        }

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var reason = unavailable!.Messages.Single();
        return new OperationHandle
        {
            OperationInstanceId = context.OperationInstanceId ?? $"opinst-{Guid.NewGuid():N}",
            OperationId = OperationId,
            CorrelationId = context.CorrelationId ?? $"corr-{Guid.NewGuid():N}",
            Status = OperationHandleStatus.Failed,
            CreatedAt = now,
            UpdatedAt = now,
            Reason = reason,
            Result = new OperationResultSummary { Summary = reason },
        };
    }

    public Task<OperationStatus> GetStatusAsync(
        OperationHandle handle,
        CancellationToken cancellationToken = default)
    {
        if (TryResolve(out var executor, out _))
        {
            return executor!.GetStatusAsync(handle, cancellationToken);
        }

        return Task.FromResult(new OperationStatus
        {
            OperationInstanceId = handle.OperationInstanceId,
            OperationId = handle.OperationId,
            CorrelationId = handle.CorrelationId,
            Status = OperationHandleStatus.Failed,
            CreatedAt = handle.CreatedAt,
            UpdatedAt = handle.UpdatedAt,
            Reason = handle.Reason,
            Result = handle.Result,
        });
    }

    private bool TryResolve(
        out ServicePublishExecutor? executor,
        out OperationValidation? unavailable)
    {
        var missing = new List<string>();
        var publishingService = Require<ILayerPublishingService>(missing);
        var connectionResolver = Require<ISecureConnectionResolver>(missing);
        var graphProvider = Require<IMetadataV2GraphProvider>(missing);
        var clock = Require<TimeProvider>(missing);

        if (missing.Count > 0)
        {
            executor = null;
            unavailable = new OperationValidation
            {
                IsValid = false,
                Status = "unavailable",
                Messages =
                [
                    $"Operation '{OperationId}' is unavailable because required services are not registered: {string.Join(", ", missing)}.",
                ],
            };
            return false;
        }

        executor = new ServicePublishExecutor(
            publishingService!,
            connectionResolver!,
            graphProvider!,
            clock!,
            services.GetService<IMcpNotificationPublisher>());
        unavailable = null;
        return true;
    }

    private T? Require<T>(List<string> missing) where T : class
    {
        var service = services.GetService<T>();
        if (service is null)
        {
            missing.Add(typeof(T).Name);
        }

        return service;
    }
}
