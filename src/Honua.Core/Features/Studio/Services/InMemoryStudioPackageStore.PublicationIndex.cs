// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Core.Features.Studio.Services;

public sealed partial class InMemoryStudioPackageStore : IStudioPublicationIndex, IStudioPackageUnpublisher
{
    private readonly Dictionary<Guid, (StudioUnpublishCommand Command, StudioContentItemPointers Pointers)> _unpublications = new();

    public Task<IReadOnlyList<StudioPublicationLineage>> ListAppServicesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StudioPublicationLineage> edges = PublishedLineage().Where(edge => edge.ItemId == itemId)
                .OrderBy(edge => edge.ServiceId, StringComparer.Ordinal).ToArray();
            return Task.FromResult(edges);
        }
    }

    public Task<IReadOnlyList<StudioPublicationLineage>> ListServiceAppsAsync(string serviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<StudioPublicationLineage> edges = PublishedLineage().Where(edge => edge.ServiceId == serviceId)
                .OrderBy(edge => edge.ItemId).ToArray();
            return Task.FromResult(edges);
        }
    }

    public Task<StudioContentItemPointers> UnpublishAsync(StudioUnpublishCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_unpublications.TryGetValue(command.RequestId, out var replay))
            {
                if (replay.Command != command)
                {
                    throw new InvalidOperationException("The unpublish request identity already belongs to another transition.");
                }
                return Task.FromResult(replay.Pointers);
            }
            if (!_items.TryGetValue(command.ItemId, out var item) || item.TenantId != command.TenantId)
            {
                throw new KeyNotFoundException("Studio content item was not found.");
            }
            if (item.PublishedVersionId != command.ExpectedPublishedVersionId)
            {
                throw new InvalidOperationException("The published version changed after approval; review the current version and retry.");
            }
            _items[item.ItemId] = item with { PublishedVersionId = null, UpdatedBy = command.ActorId, UpdatedAt = DateTimeOffset.UtcNow };
            var pointers = new StudioContentItemPointers
            {
                ItemId = item.ItemId, CurrentVersionId = item.CurrentVersionId, PublishedVersionId = null,
                OwnerId = item.OwnerId, TenantId = item.TenantId
            };
            _unpublications.Add(command.RequestId, (command, pointers));
            return Task.FromResult(pointers);
        }
    }

    private IEnumerable<StudioPublicationLineage> PublishedLineage()
    {
        foreach (var item in _items.Values.Where(item => item.Family == StudioPackageFamily.App && item.PublishedVersionId.HasValue))
        {
            var version = GetVersions(item.ItemId).Single(version => version.VersionId == item.PublishedVersionId);
            foreach (var serviceId in version.Dependencies.Where(dependency => dependency.Kind == "service")
                         .Select(dependency => dependency.Ref).Distinct(StringComparer.Ordinal))
            {
                yield return new StudioPublicationLineage
                {
                    ItemId = item.ItemId, VersionId = version.VersionId, ServiceId = serviceId, TenantId = item.TenantId
                };
            }
        }
    }
}
