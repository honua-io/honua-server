// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;

namespace Honua.Server.Features.Mcp.Stores;

/// <summary>
/// In-memory fallback for <see cref="IPublishedServiceStore"/>. Registered via
/// <c>TryAddSingleton</c> so the MCP resource surface can resolve its dependencies
/// today; a durable backing store shipped by the publishing lifecycle can register
/// earlier in the composition root and win DI resolution.
/// </summary>
internal sealed class InMemoryPublishedServiceStore : IPublishedServiceStore
{
    private readonly ConcurrentDictionary<string, PublishedServiceRecord> _records = new(StringComparer.Ordinal);

    public Task<bool> TryCreateAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return Task.FromResult(_records.TryAdd(service.ServiceId, service));
    }

    public Task<PublishedServiceRecord?> GetAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        _records.TryGetValue(serviceId, out var record);
        return Task.FromResult(record);
    }

    public Task SetAsync(
        PublishedServiceRecord service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        _records[service.ServiceId] = service;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PublishedServiceRecord>> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PublishedServiceRecord> list = _records.Values
            .Where(record => record.Status != PublishedServiceStatus.Decommissioned)
            .OrderBy(record => record.ServiceId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<PublishedServiceRecord>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PublishedServiceRecord> list = _records.Values
            .Where(record => string.Equals(record.SourceId, sourceId, StringComparison.Ordinal))
            .OrderBy(record => record.ServiceId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }
}

/// <summary>
/// In-memory fallback for <see cref="IPublishIntentStore"/>. Registered via
/// <c>TryAddSingleton</c> so the MCP resource surface can resolve its dependencies
/// today; a durable backing store shipped by the publishing lifecycle can register
/// earlier in the composition root and win DI resolution.
/// </summary>
internal sealed class InMemoryPublishIntentStore : IPublishIntentStore
{
    private readonly ConcurrentDictionary<string, PublishIntent> _intents = new(StringComparer.Ordinal);

    public Task<bool> TryCreateAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Task.FromResult(_intents.TryAdd(intent.IntentId, intent));
    }

    public Task<PublishIntent?> GetAsync(
        string intentId,
        CancellationToken cancellationToken = default)
    {
        _intents.TryGetValue(intentId, out var intent);
        return Task.FromResult(intent);
    }

    public Task SetAsync(
        PublishIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        _intents[intent.IntentId] = intent;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PublishIntent>> ListBySourceAsync(
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PublishIntent> list = _intents.Values
            .Where(intent => string.Equals(intent.SourceId, sourceId, StringComparison.Ordinal))
            .OrderBy(intent => intent.IntentId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }
}

/// <summary>
/// In-memory fallback for <see cref="IDeploymentStore"/>. Registered via
/// <c>TryAddSingleton</c> so the MCP resource surface can resolve its dependencies
/// today; a durable backing store shipped by the deployment lifecycle can register
/// earlier in the composition root and win DI resolution.
/// </summary>
internal sealed class InMemoryDeploymentStore : IDeploymentStore
{
    private readonly ConcurrentDictionary<string, Deployment> _deployments = new(StringComparer.Ordinal);

    public Task<bool> TryCreateAsync(
        Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return Task.FromResult(_deployments.TryAdd(deployment.DeploymentId, deployment));
    }

    public Task<Deployment?> GetAsync(
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        _deployments.TryGetValue(deploymentId, out var deployment);
        return Task.FromResult(deployment);
    }

    public Task SetAsync(
        Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        _deployments[deployment.DeploymentId] = deployment;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Deployment>> ListActiveAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Deployment> list = _deployments.Values
            .Where(deployment => deployment.Status != DeploymentStatus.Retired
                && deployment.Status != DeploymentStatus.Superseded)
            .OrderBy(deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<Deployment>> ListBySourceAsync(
        DeploymentSourceKind sourceKind,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Deployment> list = _deployments.Values
            .Where(deployment => deployment.Source.Kind == sourceKind
                && string.Equals(deployment.Source.SourceId, sourceId, StringComparison.Ordinal))
            .OrderBy(deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<Deployment>> ListByTargetAsync(
        string targetId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Deployment> list = _deployments.Values
            .Where(deployment => string.Equals(deployment.Target.TargetId, targetId, StringComparison.Ordinal))
            .OrderBy(deployment => deployment.DeploymentId, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(list);
    }
}
