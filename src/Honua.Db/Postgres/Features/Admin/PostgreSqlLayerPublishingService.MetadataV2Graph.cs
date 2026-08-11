// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Partial: Metadata v2 graph projection.
//
// Houses the logic that mirrors v1 honua.layers/services state into the canonical
// Metadata v2 graph (services, resources, storage bindings, publications, connections)
// and keeps refreshed layer extents in sync across both stores. Lives in its own file
// because the V2 builders + UpsertById/UpsertPublication helpers form a self-contained
// projection layer that is large, shared across the publish and extent-refresh paths,
// and easier to audit independently from the SQL-heavy persistence code.

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Db.Postgres.Features.Admin;

/// <summary>
/// AOT-safe source-generated context for the primitive storage-binding option values.
/// The published server image sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c>,
/// so the reflection-based <c>JsonSerializer.SerializeToElement(value)</c> overload throws at
/// runtime ("Reflection-based serialization has been disabled"). Building the option
/// <see cref="JsonElement"/>s through these typed metadata providers keeps the first layer
/// publish working on a real (trimmed/AOT) deployment (honua-server#1341).
/// </summary>
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class LayerPublishingStorageOptionJsonContext : JsonSerializerContext
{
}

internal sealed partial class PostgreSqlLayerPublishingService
{
    private async Task<MetadataV2GraphMutation> UpsertPublishedLayerMetadataV2Async(
        string serviceName,
        LayerPublishRequest request,
        int layerId,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        IReadOnlyList<LayerFieldInsert> fields,
        LayerExtentInsert? extent,
        CancellationToken cancellationToken)
    {
        var (graph, expectedEtag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
        var service = BuildPublishedService(graph, serviceName, srid, now);
        var resource = BuildPublishedResource(
            request,
            layerId,
            primaryKeyColumn,
            geometryColumn,
            geometryType,
            srid,
            storageSrid,
            fields,
            extent,
            now);
        var binding = BuildPublishedStorageBinding(
            request,
            layerId,
            resource.Metadata.Id,
            schema,
            table,
            primaryKeyColumn,
            geometryColumn,
            storageSrid,
            now);
        var featurePublication = BuildPublishedPublication(
            service,
            resource,
            binding,
            layerIdText,
            request.LayerName.Trim(),
            MetadataV2PublicationType.EsriFeatureLayer,
            isPrimary: true,
            idPrefix: "pub",
            request.Enabled,
            now);
        var stacPublication = BuildPublishedPublication(
            service,
            resource,
            binding,
            layerIdText,
            request.LayerName.Trim(),
            MetadataV2PublicationType.StacCollection,
            isPrimary: false,
            idPrefix: "pub-stac",
            request.Enabled,
            now);
        var connection = BuildPublishedConnection(request.ConnectionId, now);
        service = service with
        {
            PublicationIds = service.PublicationIds
                .Append(featurePublication.Metadata.Id)
                .Append(stacPublication.Metadata.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };

        // ADR-0048 Phase 2 (#1389): project the independent style catalog into the
        // canonical graph. Emit a Type=Style resource per associated catalog style and
        // point the data resource's StyleResourceIds at them ([0] = primary), so the
        // FeatureServer/MapServer StyleResourceIds read path lights up with real data
        // and one style can be shared by many resources.
        var (styleResources, styleResourceIds) =
            await BuildStyleResourcesForLayerAsync(layerId, now, cancellationToken).ConfigureAwait(false);
        if (styleResourceIds.Count > 0)
        {
            resource = resource with { StyleResourceIds = styleResourceIds };
        }

        var resourcesWithStyles = UpsertById(graph.Resources, resource, static item => item.Metadata.Id);
        foreach (var styleResource in styleResources)
        {
            resourcesWithStyles = UpsertById(resourcesWithStyles, styleResource, static item => item.Metadata.Id);
        }

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Services = UpsertById(graph.Services, service, static item => item.Metadata.Id),
            Resources = resourcesWithStyles,
            StorageBindings = UpsertById(graph.StorageBindings, binding, static item => item.Metadata.Id),
            Publications = UpsertPublication(
                UpsertPublication(graph.Publications, featurePublication),
                stacPublication),
            Connections = connection is null
                ? graph.Connections
                : UpsertById(graph.Connections, connection, static item => item.Metadata.Id)
        };

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Published layer metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }

        return await PersistMetadataV2MutationAsync(
                graph,
                updatedGraph,
                expectedEtag,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MetadataV2GraphMutation> UpsertLinkedLayerMetadataV2Async(
        string serviceName,
        PublishedLayerSummary layer,
        CancellationToken cancellationToken)
    {
        var (graph, expectedEtag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        var updatedGraph = BuildLinkedLayerMetadataV2Graph(
            graph,
            serviceName,
            layer.LayerId,
            layer.LayerName,
            layer.Srid,
            DateTimeOffset.UtcNow,
            layer.Enabled);

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Linked layer metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }

        return await PersistMetadataV2MutationAsync(
                graph,
                updatedGraph,
                expectedEtag,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MetadataV2GraphMutation?> UpdateLayerLifecycleMetadataV2Async(
        HashSet<int> layerIds,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (layerIds.Count == 0)
        {
            return null;
        }

        var (graph, expectedEtag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        var updatedGraph = BuildLayerEnabledMetadataV2Graph(
            graph,
            layerIds,
            enabled,
            DateTimeOffset.UtcNow);
        if (ReferenceEquals(updatedGraph, graph))
        {
            return null;
        }

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Layer lifecycle metadata v2 graph is invalid: {string.Join("; ", validation.Errors)}");
        }

        return await PersistMetadataV2MutationAsync(
                graph,
                updatedGraph,
                expectedEtag,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<MetadataV2GraphMutation> PersistMetadataV2MutationAsync(
        MetadataV2Graph previousGraph,
        MetadataV2Graph updatedGraph,
        string? expectedEtag,
        CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _metadataGraphStore
                .SaveAsync(updatedGraph, expectedEtag, cancellationToken)
                .ConfigureAwait(false);
            return new MetadataV2GraphMutation(previousGraph, updatedGraph, persisted.Etag);
        }
        catch (MetadataV2GraphCommitOutcomeUnknownException commitException)
        {
            var mutation = new MetadataV2GraphMutation(
                previousGraph,
                updatedGraph,
                commitException.PendingSnapshot.Etag);
            await CompensateMetadataV2MutationAsync(mutation, commitException).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompensateMetadataV2MutationAsync(
        MetadataV2GraphMutation mutation,
        Exception commitException)
    {
        try
        {
            const int maxAttempts = 4;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var current = await _metadataGraphStore.GetCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                var compensatingGraph = string.Equals(current.Etag, mutation.PersistedEtag, StringComparison.Ordinal)
                    ? BuildCompensatingMetadataV2Graph(
                        mutation.PreviousGraph,
                        current.Revision,
                        DateTimeOffset.UtcNow)
                    : BuildRebasedCompensatingMetadataV2Graph(
                        current.Graph,
                        mutation.PreviousGraph,
                        mutation.PersistedGraph,
                        DateTimeOffset.UtcNow);

                try
                {
                    await _metadataGraphStore
                        .SaveAsync(compensatingGraph, current.Etag, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }
                catch (InvalidOperationException compensationException)
                    when (attempt < maxAttempts && IsMetadataEtagMismatch(compensationException))
                {
                    // Another graph writer won after our read. Reload and rebase the inverse mutation
                    // again so its unrelated additions survive while this failed publication is
                    // removed.
                }
            }
        }
        catch (DbException compensationException)
        {
            throw BuildCompensationFailure(commitException, compensationException);
        }
        catch (InvalidOperationException compensationException)
        {
            throw BuildCompensationFailure(commitException, compensationException);
        }
        catch (IOException compensationException)
        {
            throw BuildCompensationFailure(commitException, compensationException);
        }
        catch (TimeoutException compensationException)
        {
            throw BuildCompensationFailure(commitException, compensationException);
        }

        throw new InvalidOperationException("Metadata v2 compensation exhausted its retry budget.");
    }

    private static bool IsMetadataEtagMismatch(InvalidOperationException exception)
        => exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);

    private static AggregateException BuildCompensationFailure(
        Exception commitException,
        Exception compensationException)
        => new(
            "The layer transaction failed after Metadata v2 persistence, and the compensating graph write also failed.",
            commitException,
            compensationException);

    internal static MetadataV2Graph BuildCompensatingMetadataV2Graph(
        MetadataV2Graph previousGraph,
        long persistedRevision,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(previousGraph);
        return previousGraph with
        {
            Revision = Math.Max(persistedRevision + 1, previousGraph.Revision + 1),
            GeneratedAt = now
        };
    }

    /// <summary>
    /// Rebases the inverse of one failed layer-publication mutation onto a newer graph revision. Only
    /// identities introduced by that mutation are removed; unrelated resources/publications written
    /// after it remain intact.
    /// </summary>
    internal static MetadataV2Graph BuildRebasedCompensatingMetadataV2Graph(
        MetadataV2Graph currentGraph,
        MetadataV2Graph previousGraph,
        MetadataV2Graph persistedGraph,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentGraph);
        ArgumentNullException.ThrowIfNull(previousGraph);
        ArgumentNullException.ThrowIfNull(persistedGraph);

        var previousPublicationIds = previousGraph.Publications
            .Select(item => item.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var previousPublicationsById = previousGraph.Publications
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var persistedPublicationsById = persistedGraph.Publications
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var addedPublicationIds = persistedGraph.Publications
            .Select(item => item.Metadata.Id)
            .Where(id => !previousPublicationIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        var replacedPublicationsByPersistedId = new Dictionary<string, MetadataV2Publication>(StringComparer.Ordinal);
        foreach (var previous in previousGraph.Publications.Where(previous =>
                     !persistedPublicationsById.ContainsKey(previous.Metadata.Id)))
        {
            var replacement = persistedGraph.Publications.FirstOrDefault(persisted =>
                addedPublicationIds.Contains(persisted.Metadata.Id) &&
                HasSamePublicationSlot(previous, persisted));
            if (replacement is not null)
            {
                replacedPublicationsByPersistedId.TryAdd(replacement.Metadata.Id, previous);
            }
        }

        var publications = new List<MetadataV2Publication>(currentGraph.Publications.Count);
        foreach (var current in currentGraph.Publications)
        {
            if (addedPublicationIds.Contains(current.Metadata.Id) &&
                persistedPublicationsById.TryGetValue(current.Metadata.Id, out var persistedAdded) &&
                StillUsesFailedPublicationDataTarget(current, persistedAdded))
            {
                if (replacedPublicationsByPersistedId.TryGetValue(current.Metadata.Id, out var replaced) &&
                    !currentGraph.Publications.Any(other =>
                        !string.Equals(other.Metadata.Id, current.Metadata.Id, StringComparison.Ordinal) &&
                        HasSamePublicationSlot(other, replaced)))
                {
                    publications.Add(replaced);
                }

                continue;
            }

            publications.Add(
                previousPublicationsById.TryGetValue(current.Metadata.Id, out var previous) &&
                persistedPublicationsById.TryGetValue(current.Metadata.Id, out var persisted)
                    ? RestorePublicationMutation(current, previous, persisted)
                    : current);
        }

        var previousServiceIds = previousGraph.Services
            .Select(item => item.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var previousServicesById = previousGraph.Services
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var persistedServicesById = persistedGraph.Services
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var addedServiceIds = persistedGraph.Services
            .Select(item => item.Metadata.Id)
            .Where(id => !previousServiceIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        var services = currentGraph.Services
            .Select(service =>
            {
                var restored = previousServicesById.TryGetValue(service.Metadata.Id, out var previous) &&
                               persistedServicesById.TryGetValue(service.Metadata.Id, out var persisted)
                    ? RestoreServiceMutation(service, previous, persisted)
                    : service;
                return restored with
                {
                    PublicationIds = restored.PublicationIds
                        .Where(id => !addedPublicationIds.Contains(id))
                        .ToArray()
                };
            })
            .Where(service => !addedServiceIds.Contains(service.Metadata.Id) ||
                HasServiceBeenRepurposed(
                    service,
                    persistedServicesById[service.Metadata.Id]) ||
                publications.Any(publication =>
                    string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal)) ||
                service.PublicationIds.Count > 0)
            .ToArray();

        var previousBindingIds = previousGraph.StorageBindings
            .Select(item => item.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var addedBindingIds = persistedGraph.StorageBindings
            .Select(item => item.Metadata.Id)
            .Where(id => !previousBindingIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        var previousBindingsById = previousGraph.StorageBindings
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var persistedBindingsById = persistedGraph.StorageBindings
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var rebasedBindings = currentGraph.StorageBindings
            .Select(binding => previousBindingsById.TryGetValue(binding.Metadata.Id, out var previous) &&
                               persistedBindingsById.TryGetValue(binding.Metadata.Id, out var persisted)
                ? RestoreStorageBindingMutation(binding, previous, persisted)
                : binding)
            .ToArray();
        var lifecycleMutatedResourceIds = persistedGraph.StorageBindings
            .Where(persisted =>
                previousBindingsById.TryGetValue(persisted.Metadata.Id, out var previous) &&
                previous.Status.Lifecycle != persisted.Status.Lifecycle)
            .SelectMany(persisted =>
            {
                var previous = previousBindingsById[persisted.Metadata.Id];
                return new[] { previous.ResourceId, persisted.ResourceId };
            })
            .ToHashSet(StringComparer.Ordinal);
        var previousResourceIds = previousGraph.Resources
            .Select(item => item.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var previousResourcesById = previousGraph.Resources
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var persistedResourcesById = persistedGraph.Resources
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var addedResourceIds = persistedGraph.Resources
            .Select(item => item.Metadata.Id)
            .Where(id => !previousResourceIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        var resources = currentGraph.Resources
            .Select(resource => previousResourcesById.TryGetValue(resource.Metadata.Id, out var previous) &&
                                persistedResourcesById.TryGetValue(resource.Metadata.Id, out var persisted)
                ? RestoreResourceMutation(resource, previous, persisted)
                : resource)
            .Where(resource => !addedResourceIds.Contains(resource.Metadata.Id) ||
                HasResourceBeenRepurposed(
                    resource,
                    persistedResourcesById[resource.Metadata.Id],
                    addedBindingIds) ||
                publications.Any(publication =>
                    string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal)) ||
                currentGraph.Resources.Any(other =>
                    !addedResourceIds.Contains(other.Metadata.Id) &&
                    other.StyleResourceIds.Contains(resource.Metadata.Id, StringComparer.Ordinal)))
            .ToArray();

        var bindings = rebasedBindings
            .Where(binding => !addedBindingIds.Contains(binding.Metadata.Id) ||
                HasStorageBindingBeenRepurposed(binding, persistedBindingsById[binding.Metadata.Id]) &&
                resources.Any(resource =>
                    string.Equals(resource.Metadata.Id, binding.ResourceId, StringComparison.Ordinal)) ||
                publications.Any(publication =>
                    string.Equals(publication.StorageBindingId, binding.Metadata.Id, StringComparison.Ordinal)) ||
                resources.Any(resource =>
                    string.Equals(resource.PrimaryStorageBindingId, binding.Metadata.Id, StringComparison.Ordinal) ||
                    resource.StorageBindingIds.Contains(binding.Metadata.Id, StringComparer.Ordinal)))
            .ToArray();
        var resourceLifecycleRecomputeIds = currentGraph.Resources
            .Where(current =>
                lifecycleMutatedResourceIds.Contains(current.Metadata.Id) &&
                previousResourcesById.TryGetValue(current.Metadata.Id, out var previous) &&
                persistedResourcesById.TryGetValue(current.Metadata.Id, out var persisted) &&
                current.Status.Lifecycle == persisted.Status.Lifecycle &&
                previous.Status.Lifecycle != persisted.Status.Lifecycle)
            .Select(resource => resource.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        resources = resources
            .Select(resource =>
            {
                if (!resourceLifecycleRecomputeIds.Contains(resource.Metadata.Id))
                {
                    return resource;
                }

                return resource with
                {
                    Status = resource.Status with
                    {
                        Lifecycle = DeriveResourceLifecycleFromBindings(bindings, resource.Metadata.Id),
                    },
                };
            })
            .ToArray();

        var previousConnectionIds = previousGraph.Connections
            .Select(item => item.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        var previousConnectionsById = previousGraph.Connections
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var persistedConnectionsById = persistedGraph.Connections
            .ToDictionary(item => item.Metadata.Id, StringComparer.Ordinal);
        var addedConnectionIds = persistedGraph.Connections
            .Select(item => item.Metadata.Id)
            .Where(id => !previousConnectionIds.Contains(id))
            .ToHashSet(StringComparer.Ordinal);
        var connections = currentGraph.Connections
            .Select(connection => previousConnectionsById.TryGetValue(connection.Metadata.Id, out var previous) &&
                                  persistedConnectionsById.TryGetValue(connection.Metadata.Id, out var persisted)
                ? RestoreConnectionMutation(connection, previous, persisted)
                : connection)
            .Where(connection => !addedConnectionIds.Contains(connection.Metadata.Id) ||
                HasConnectionBeenRepurposed(connection, persistedConnectionsById[connection.Metadata.Id]) ||
                bindings.Any(binding =>
                    string.Equals(binding.ConnectionId, connection.Metadata.Id, StringComparison.Ordinal)))
            .ToArray();

        return currentGraph with
        {
            Revision = Math.Max(currentGraph.Revision + 1, persistedGraph.Revision + 1),
            GeneratedAt = now,
            Services = services,
            Resources = resources,
            StorageBindings = bindings,
            Publications = publications,
            Connections = connections,
        };
    }

    private static bool HasSamePublicationSlot(
        MetadataV2Publication left,
        MetadataV2Publication right)
        => string.Equals(left.ServiceId, right.ServiceId, StringComparison.Ordinal) &&
           left.LayerIndex == right.LayerIndex &&
           left.PublicationType == right.PublicationType;

    private static bool StillUsesFailedPublicationDataTarget(
        MetadataV2Publication current,
        MetadataV2Publication persisted)
        => string.Equals(current.ResourceId, persisted.ResourceId, StringComparison.Ordinal) ||
           string.Equals(current.StorageBindingId, persisted.StorageBindingId, StringComparison.Ordinal);

    private static bool HasServiceBeenRepurposed(
        MetadataV2Service current,
        MetadataV2Service persisted)
        => HasTargetMutation(
            new MetadataV2Service
            {
                ServiceType = current.ServiceType,
                Route = current.Route,
                AccessPolicy = current.AccessPolicy,
                SpatialReference = current.SpatialReference,
                Protocols = current.Protocols,
                Options = current.Options,
                Settings = current.Settings,
            },
            new MetadataV2Service
            {
                ServiceType = persisted.ServiceType,
                Route = persisted.Route,
                AccessPolicy = persisted.AccessPolicy,
                SpatialReference = persisted.SpatialReference,
                Protocols = persisted.Protocols,
                Options = persisted.Options,
                Settings = persisted.Settings,
            },
            MetadataV2JsonContext.Default.MetadataV2Service);

    private static bool HasResourceBeenRepurposed(
        MetadataV2Resource current,
        MetadataV2Resource persisted,
        HashSet<string> addedBindingIds)
    {
        var failedBindingIds = persisted.StorageBindingIds
            .Where(addedBindingIds.Contains)
            .ToHashSet(StringComparer.Ordinal);
        if (persisted.PrimaryStorageBindingId is { } primaryBindingId &&
            addedBindingIds.Contains(primaryBindingId))
        {
            failedBindingIds.Add(primaryBindingId);
        }

        if (failedBindingIds.Count == 0 ||
            current.StorageBindingIds.Any(failedBindingIds.Contains) ||
            current.PrimaryStorageBindingId is { } currentPrimary && failedBindingIds.Contains(currentPrimary))
        {
            return false;
        }

        return current.Type != persisted.Type ||
               current.StorageBindingIds.Any(id => !addedBindingIds.Contains(id)) ||
               current.PrimaryStorageBindingId is { } independentPrimary &&
               !addedBindingIds.Contains(independentPrimary);
    }

    private static bool HasStorageBindingBeenRepurposed(
        MetadataV2StorageBinding current,
        MetadataV2StorageBinding persisted)
        => HasTargetMutation(
            new MetadataV2StorageBinding
            {
                ResourceId = current.ResourceId,
                ConnectionId = current.ConnectionId,
                StorageType = current.StorageType,
                Locator = current.Locator,
                StorageLayerId = current.StorageLayerId,
                Options = current.Options,
            },
            new MetadataV2StorageBinding
            {
                ResourceId = persisted.ResourceId,
                ConnectionId = persisted.ConnectionId,
                StorageType = persisted.StorageType,
                Locator = persisted.Locator,
                StorageLayerId = persisted.StorageLayerId,
                Options = persisted.Options,
            },
            MetadataV2JsonContext.Default.MetadataV2StorageBinding);

    private static bool HasConnectionBeenRepurposed(
        MetadataV2Connection current,
        MetadataV2Connection persisted)
        => HasTargetMutation(
            new MetadataV2Connection
            {
                Type = current.Type,
                Provider = current.Provider,
                Endpoint = current.Endpoint,
                SecretRef = current.SecretRef,
                Options = current.Options,
            },
            new MetadataV2Connection
            {
                Type = persisted.Type,
                Provider = persisted.Provider,
                Endpoint = persisted.Endpoint,
                SecretRef = persisted.SecretRef,
                Options = persisted.Options,
            },
            MetadataV2JsonContext.Default.MetadataV2Connection);

    private static bool HasTargetMutation<T>(
        T current,
        T persisted,
        JsonTypeInfo<T> typeInfo)
        => !JsonEquivalent(current, persisted, typeInfo);

    private static MetadataV2Publication RestorePublicationMutation(
        MetadataV2Publication current,
        MetadataV2Publication previous,
        MetadataV2Publication persisted)
        => current with
        {
            Metadata = RestoreObjectMetadataMutation(current.Metadata, previous.Metadata, persisted.Metadata),
            ResourceId = RestoreMutationValue(current.ResourceId, previous.ResourceId, persisted.ResourceId),
            ServiceId = RestoreMutationValue(current.ServiceId, previous.ServiceId, persisted.ServiceId),
            StorageBindingId = RestoreMutationValue(
                current.StorageBindingId,
                previous.StorageBindingId,
                persisted.StorageBindingId),
            PublicationType = RestoreMutationValue(
                current.PublicationType,
                previous.PublicationType,
                persisted.PublicationType),
            TitleOverride = RestoreMutationValue(
                current.TitleOverride,
                previous.TitleOverride,
                persisted.TitleOverride),
            Identifier = RestorePublicationMutationValue(
                current.Identifier,
                previous.Identifier,
                persisted.Identifier,
                static value => new MetadataV2Publication { Identifier = value }),
            LayerIndex = RestoreMutationValue(current.LayerIndex, previous.LayerIndex, persisted.LayerIndex),
            Path = RestoreMutationValue(current.Path, previous.Path, persisted.Path),
            ServiceLocalId = RestoreMutationValue(
                current.ServiceLocalId,
                previous.ServiceLocalId,
                persisted.ServiceLocalId),
            IsPrimary = RestoreMutationValue(current.IsPrimary, previous.IsPrimary, persisted.IsPrimary),
            SupportedFormats = RestoreMutationSequence(
                current.SupportedFormats,
                previous.SupportedFormats,
                persisted.SupportedFormats),
            FieldAliases = RestoreMetadataMapMutation(
                current.FieldAliases,
                previous.FieldAliases,
                persisted.FieldAliases),
            Capabilities = RestoreMutationSequence(
                current.Capabilities,
                previous.Capabilities,
                persisted.Capabilities),
            Options = RestoreJsonMapMutation(
                current.Options,
                previous.Options,
                persisted.Options),
            Status = RestoreStatusMutation(current.Status, previous.Status, persisted.Status),
            Extensions = RestoreJsonMapMutation(
                current.Extensions,
                previous.Extensions,
                persisted.Extensions),
        };

    /// <summary>
    /// Applies a field-level three-way inverse to the existing service that publication updated.
    /// Fields still equal to the failed persisted mutation return to their pre-mutation values;
    /// fields a later graph writer changed remain untouched.
    /// </summary>
    private static MetadataV2Service RestoreServiceMutation(
        MetadataV2Service current,
        MetadataV2Service previous,
        MetadataV2Service persisted)
        => current with
        {
            Metadata = RestoreServiceMetadataMutation(current.Metadata, previous.Metadata, persisted.Metadata),
            ServiceType = RestoreMutationValue(current.ServiceType, previous.ServiceType, persisted.ServiceType),
            Route = RestoreMutationValue(current.Route, previous.Route, persisted.Route),
            SpatialReference = RestoreSpatialReferenceMutation(
                current.SpatialReference,
                previous.SpatialReference,
                persisted.SpatialReference),
            Protocols = RestoreMutationSequence(current.Protocols, previous.Protocols, persisted.Protocols),
            Status = RestoreStatusMutation(current.Status, previous.Status, persisted.Status),
        };

    private static MetadataV2Resource RestoreResourceMutation(
        MetadataV2Resource current,
        MetadataV2Resource previous,
        MetadataV2Resource persisted)
        => current with
        {
            Metadata = RestoreObjectMetadataMutation(current.Metadata, previous.Metadata, persisted.Metadata),
            Type = RestoreMutationValue(current.Type, previous.Type, persisted.Type),
            StorageBindingIds = RestoreMutationSequence(
                current.StorageBindingIds,
                previous.StorageBindingIds,
                persisted.StorageBindingIds),
            PrimaryStorageBindingId = RestoreMutationValue(
                current.PrimaryStorageBindingId,
                previous.PrimaryStorageBindingId,
                persisted.PrimaryStorageBindingId),
            SchemaFields = RestoreSchemaFieldsMutation(
                current.SchemaFields,
                previous.SchemaFields,
                persisted.SchemaFields),
            PolicyIds = RestoreMutationSequence(
                current.PolicyIds,
                previous.PolicyIds,
                persisted.PolicyIds),
            Relationships = RestoreResourceMutationValue(
                current.Relationships,
                previous.Relationships,
                persisted.Relationships,
                static value => new MetadataV2Resource { Relationships = value },
                static resource => resource.Relationships),
            AccessPolicy = RestoreAccessPolicyMutation(
                current.AccessPolicy,
                previous.AccessPolicy,
                persisted.AccessPolicy),
            Spatial = RestoreResourceSpatialMutation(current.Spatial, previous.Spatial, persisted.Spatial),
            Temporal = RestoreResourceTemporalMutation(
                current.Temporal,
                previous.Temporal,
                persisted.Temporal),
            PermanentFilter = RestoreResourceMutationValue(
                current.PermanentFilter,
                previous.PermanentFilter,
                persisted.PermanentFilter,
                static value => new MetadataV2Resource { PermanentFilter = value },
                static resource => resource.PermanentFilter),
            Subtypes = RestoreResourceMutationValue(
                current.Subtypes,
                previous.Subtypes,
                persisted.Subtypes,
                static value => new MetadataV2Resource { Subtypes = value },
                static resource => resource.Subtypes),
            AttributeRules = RestoreAttributeRulesMutation(
                current.AttributeRules,
                previous.AttributeRules,
                persisted.AttributeRules),
            ContingentValueGroups = RestoreResourceMutationValue(
                current.ContingentValueGroups,
                previous.ContingentValueGroups,
                persisted.ContingentValueGroups,
                static value => new MetadataV2Resource { ContingentValueGroups = value },
                static resource => resource.ContingentValueGroups),
            OwnerEditPolicy = RestoreResourceMutationValue(
                current.OwnerEditPolicy,
                previous.OwnerEditPolicy,
                persisted.OwnerEditPolicy,
                static value => new MetadataV2Resource { OwnerEditPolicy = value },
                static resource => resource.OwnerEditPolicy),
            Extrusion = RestoreResourceMutationValue(
                current.Extrusion,
                previous.Extrusion,
                persisted.Extrusion,
                static value => new MetadataV2Resource { Extrusion = value },
                static resource => resource.Extrusion),
            Symbology3D = RestoreResourceMutationValue(
                current.Symbology3D,
                previous.Symbology3D,
                persisted.Symbology3D,
                static value => new MetadataV2Resource { Symbology3D = value },
                static resource => resource.Symbology3D),
            StyleResourceIds = RestoreMutationSequence(
                current.StyleResourceIds,
                previous.StyleResourceIds,
                persisted.StyleResourceIds),
            Style = RestoreResourceMutationValue(
                current.Style,
                previous.Style,
                persisted.Style,
                static value => new MetadataV2Resource { Style = value },
                static resource => resource.Style),
            Display = RestoreResourceMutationValue(
                current.Display,
                previous.Display,
                persisted.Display,
                static value => new MetadataV2Resource { Display = value },
                static resource => resource.Display),
            Editing = RestoreResourceMutationValue(
                current.Editing,
                previous.Editing,
                persisted.Editing,
                static value => new MetadataV2Resource { Editing = value },
                static resource => resource.Editing),
            Status = RestoreStatusMutation(current.Status, previous.Status, persisted.Status),
            Extensions = RestoreJsonMapMutation(
                current.Extensions,
                previous.Extensions,
                persisted.Extensions),
        };

    private static List<MetadataV2Field> RestoreSchemaFieldsMutation(
        IReadOnlyList<MetadataV2Field> current,
        IReadOnlyList<MetadataV2Field> previous,
        IReadOnlyList<MetadataV2Field> persisted)
    {
        var previousToPersisted = AlignSchemaFields(previous, persisted);
        var persistedToPrevious = previousToPersisted.ToDictionary(
            match => match.RightIndex,
            match => match.LeftIndex);
        var persistedToCurrent = AlignSchemaFields(persisted, current);
        var currentToPersisted = persistedToCurrent.ToDictionary(
            match => match.RightIndex,
            match => match.LeftIndex);
        var restored = new List<RestoredSchemaField>(previous.Count + current.Count);

        for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
        {
            if (!currentToPersisted.TryGetValue(currentIndex, out var persistedIndex))
            {
                restored.Add(new RestoredSchemaField(current[currentIndex], null));
                continue;
            }

            if (persistedToPrevious.TryGetValue(persistedIndex, out var previousIndex))
            {
                restored.Add(new RestoredSchemaField(
                    RestoreSchemaFieldMutation(
                        current[currentIndex],
                        previous[previousIndex],
                        persisted[persistedIndex]),
                    previousIndex));
            }
            // Fields introduced by the failed publication are omitted, including when a
            // later writer renamed them. Their paired SQL columns never committed.
        }

        var matchedPreviousIndices = previousToPersisted
            .Select(match => match.LeftIndex)
            .ToHashSet();
        for (var previousIndex = 0; previousIndex < previous.Count; previousIndex++)
        {
            if (matchedPreviousIndices.Contains(previousIndex))
            {
                continue;
            }

            var recreatedIndex = restored.FindIndex(item =>
                item.PreviousIndex is null &&
                HasSameSchemaFieldIdentity(item.Field, previous[previousIndex]));
            if (recreatedIndex >= 0)
            {
                restored[recreatedIndex] = restored[recreatedIndex] with { PreviousIndex = previousIndex };
                continue;
            }

            var insertionIndex = restored.FindIndex(item => item.PreviousIndex > previousIndex);
            restored.Insert(
                insertionIndex >= 0 ? insertionIndex : restored.Count,
                new RestoredSchemaField(previous[previousIndex], previousIndex));
        }

        return restored.Select(item => item.Field).ToList();
    }

    private static bool HasSameSchemaFieldIdentity(MetadataV2Field left, MetadataV2Field right)
        => (!string.IsNullOrWhiteSpace(left.SemanticId) &&
            !string.IsNullOrWhiteSpace(right.SemanticId) &&
            string.Equals(left.SemanticId.Trim(), right.SemanticId.Trim(), StringComparison.Ordinal)) ||
           string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static List<SchemaFieldMatch> AlignSchemaFields(
        IReadOnlyList<MetadataV2Field> left,
        IReadOnlyList<MetadataV2Field> right)
    {
        var matches = new List<SchemaFieldMatch>(Math.Min(left.Count, right.Count));
        var matchedLeft = new HashSet<int>();
        var matchedRight = new HashSet<int>();

        MatchSchemaFieldsByIdentity(
            left,
            right,
            matches,
            matchedLeft,
            matchedRight,
            static (leftField, rightField) =>
                !string.IsNullOrWhiteSpace(leftField.SemanticId) &&
                !string.IsNullOrWhiteSpace(rightField.SemanticId) &&
                string.Equals(
                    leftField.SemanticId.Trim(),
                    rightField.SemanticId.Trim(),
                    StringComparison.Ordinal));
        MatchSchemaFieldsByIdentity(
            left,
            right,
            matches,
            matchedLeft,
            matchedRight,
            static (leftField, rightField) =>
                string.Equals(leftField.Name, rightField.Name, StringComparison.OrdinalIgnoreCase));

        var remainingLeft = Enumerable.Range(0, left.Count)
            .Where(index => !matchedLeft.Contains(index))
            .ToArray();
        var remainingRight = Enumerable.Range(0, right.Count)
            .Where(index => !matchedRight.Contains(index))
            .ToArray();
        matches.AddRange(AlignSchemaFieldsBySimilarity(left, right, remainingLeft, remainingRight));
        return matches;
    }

    private static void MatchSchemaFieldsByIdentity(
        IReadOnlyList<MetadataV2Field> left,
        IReadOnlyList<MetadataV2Field> right,
        ICollection<SchemaFieldMatch> matches,
        HashSet<int> matchedLeft,
        HashSet<int> matchedRight,
        Func<MetadataV2Field, MetadataV2Field, bool> matchesIdentity)
    {
        for (var leftIndex = 0; leftIndex < left.Count; leftIndex++)
        {
            if (matchedLeft.Contains(leftIndex))
            {
                continue;
            }

            for (var rightIndex = 0; rightIndex < right.Count; rightIndex++)
            {
                if (matchedRight.Contains(rightIndex) || !matchesIdentity(left[leftIndex], right[rightIndex]))
                {
                    continue;
                }

                matches.Add(new SchemaFieldMatch(leftIndex, rightIndex));
                matchedLeft.Add(leftIndex);
                matchedRight.Add(rightIndex);
                break;
            }
        }
    }

    private static List<SchemaFieldMatch> AlignSchemaFieldsBySimilarity(
        IReadOnlyList<MetadataV2Field> left,
        IReadOnlyList<MetadataV2Field> right,
        int[] leftIndices,
        int[] rightIndices)
    {
        const int gapPenalty = -3;
        var scores = new int[leftIndices.Length + 1, rightIndices.Length + 1];
        var moves = new byte[leftIndices.Length + 1, rightIndices.Length + 1];
        for (var leftIndex = 1; leftIndex <= leftIndices.Length; leftIndex++)
        {
            scores[leftIndex, 0] = scores[leftIndex - 1, 0] + gapPenalty;
            moves[leftIndex, 0] = 2;
        }

        for (var rightIndex = 1; rightIndex <= rightIndices.Length; rightIndex++)
        {
            scores[0, rightIndex] = scores[0, rightIndex - 1] + gapPenalty;
            moves[0, rightIndex] = 3;
        }

        for (var leftIndex = 1; leftIndex <= leftIndices.Length; leftIndex++)
        {
            for (var rightIndex = 1; rightIndex <= rightIndices.Length; rightIndex++)
            {
                var similarity = GetSchemaFieldSimilarity(
                    left[leftIndices[leftIndex - 1]],
                    right[rightIndices[rightIndex - 1]]);
                var matchScore = similarity >= 6
                    ? scores[leftIndex - 1, rightIndex - 1] + similarity + 4
                    : int.MinValue;
                var skipLeftScore = scores[leftIndex - 1, rightIndex] + gapPenalty;
                var skipRightScore = scores[leftIndex, rightIndex - 1] + gapPenalty;
                if (matchScore >= skipLeftScore && matchScore >= skipRightScore)
                {
                    scores[leftIndex, rightIndex] = matchScore;
                    moves[leftIndex, rightIndex] = 1;
                }
                else if (skipLeftScore >= skipRightScore)
                {
                    scores[leftIndex, rightIndex] = skipLeftScore;
                    moves[leftIndex, rightIndex] = 2;
                }
                else
                {
                    scores[leftIndex, rightIndex] = skipRightScore;
                    moves[leftIndex, rightIndex] = 3;
                }
            }
        }

        var matches = new List<SchemaFieldMatch>(Math.Min(leftIndices.Length, rightIndices.Length));
        var leftCursor = leftIndices.Length;
        var rightCursor = rightIndices.Length;
        while (leftCursor > 0 || rightCursor > 0)
        {
            switch (moves[leftCursor, rightCursor])
            {
                case 1:
                    matches.Add(new SchemaFieldMatch(
                        leftIndices[leftCursor - 1],
                        rightIndices[rightCursor - 1]));
                    leftCursor--;
                    rightCursor--;
                    break;
                case 2:
                    leftCursor--;
                    break;
                default:
                    rightCursor--;
                    break;
            }
        }

        matches.Reverse();
        return matches;
    }

    private static int GetSchemaFieldSimilarity(MetadataV2Field left, MetadataV2Field right)
    {
        var score = left.Type == right.Type ? 6 : 0;
        if (left.Title is not null && string.Equals(left.Title, right.Title, StringComparison.Ordinal))
        {
            score += 4;
        }
        if (left.Alias is not null && string.Equals(left.Alias, right.Alias, StringComparison.Ordinal))
        {
            score += 4;
        }
        if (left.Description is not null && string.Equals(left.Description, right.Description, StringComparison.Ordinal))
        {
            score += 3;
        }
        if (left.SqlType is not null && string.Equals(left.SqlType, right.SqlType, StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }
        if (left.Length is not null && left.Length == right.Length)
        {
            score += 2;
        }
        if (left.SemanticRoles.Count > 0 && left.SemanticRoles.SequenceEqual(right.SemanticRoles))
        {
            score += 3;
        }
        if (left.Nullable == right.Nullable)
        {
            score++;
        }
        if (left.Hidden == right.Hidden)
        {
            score++;
        }

        return score;
    }

    private readonly record struct SchemaFieldMatch(int LeftIndex, int RightIndex);

    private readonly record struct RestoredSchemaField(MetadataV2Field Field, int? PreviousIndex);

    private static List<MetadataV2AttributeRule> RestoreAttributeRulesMutation(
        IReadOnlyList<MetadataV2AttributeRule> current,
        IReadOnlyList<MetadataV2AttributeRule> previous,
        IReadOnlyList<MetadataV2AttributeRule> persisted)
    {
        var restored = new List<RestoredAttributeRule>(previous.Count + current.Count);
        for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
        {
            var currentRule = current[currentIndex];
            var persistedIndex = FindAttributeRuleIndex(persisted, currentRule.Name);
            if (persistedIndex < 0)
            {
                restored.Add(new RestoredAttributeRule(currentRule, null));
                continue;
            }

            var previousIndex = FindAttributeRuleIndex(previous, persisted[persistedIndex].Name);
            if (previousIndex >= 0)
            {
                restored.Add(new RestoredAttributeRule(
                    RestoreAttributeRuleMutation(
                        currentRule,
                        previous[previousIndex],
                        persisted[persistedIndex]),
                    previousIndex));
            }
            else if (!AttributeRulesEquivalent(currentRule, persisted[persistedIndex]))
            {
                // A later writer repurposed the rule introduced by the failed mutation.
                restored.Add(new RestoredAttributeRule(currentRule, null));
            }
            // Rules introduced only by the failed mutation are otherwise omitted.
        }

        for (var previousIndex = 0; previousIndex < previous.Count; previousIndex++)
        {
            if (FindAttributeRuleIndex(persisted, previous[previousIndex].Name) >= 0)
            {
                continue;
            }

            var recreatedIndex = restored.FindIndex(item =>
                item.PreviousIndex is null &&
                HasSameAttributeRuleIdentity(item.Rule, previous[previousIndex]));
            if (recreatedIndex >= 0)
            {
                restored[recreatedIndex] = restored[recreatedIndex] with { PreviousIndex = previousIndex };
                continue;
            }

            var insertionIndex = restored.FindIndex(item => item.PreviousIndex > previousIndex);
            restored.Insert(
                insertionIndex >= 0 ? insertionIndex : restored.Count,
                new RestoredAttributeRule(previous[previousIndex], previousIndex));
        }

        return restored.Select(item => item.Rule).ToList();
    }

    private static MetadataV2AttributeRule RestoreAttributeRuleMutation(
        MetadataV2AttributeRule current,
        MetadataV2AttributeRule previous,
        MetadataV2AttributeRule persisted)
        => current with
        {
            Name = RestoreMutationValue(current.Name, previous.Name, persisted.Name),
            Type = RestoreMutationValue(current.Type, previous.Type, persisted.Type),
            FieldName = RestoreMutationValue(current.FieldName, previous.FieldName, persisted.FieldName),
            ScriptExpression = RestoreMutationValue(
                current.ScriptExpression,
                previous.ScriptExpression,
                persisted.ScriptExpression),
            TriggeringEvents = RestoreMutationSequence(
                current.TriggeringEvents,
                previous.TriggeringEvents,
                persisted.TriggeringEvents),
            ErrorMessage = RestoreMutationValue(
                current.ErrorMessage,
                previous.ErrorMessage,
                persisted.ErrorMessage),
            IsEnabled = RestoreMutationValue(current.IsEnabled, previous.IsEnabled, persisted.IsEnabled),
            Batch = RestoreMutationValue(current.Batch, previous.Batch, persisted.Batch),
        };

    private static int FindAttributeRuleIndex(
        IReadOnlyList<MetadataV2AttributeRule> rules,
        string name)
    {
        for (var index = 0; index < rules.Count; index++)
        {
            if (string.Equals(rules[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool HasSameAttributeRuleIdentity(
        MetadataV2AttributeRule left,
        MetadataV2AttributeRule right)
        => string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static bool AttributeRulesEquivalent(
        MetadataV2AttributeRule left,
        MetadataV2AttributeRule right)
        => string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
           left.Type == right.Type &&
           string.Equals(left.FieldName, right.FieldName, StringComparison.Ordinal) &&
           string.Equals(left.ScriptExpression, right.ScriptExpression, StringComparison.Ordinal) &&
           left.TriggeringEvents.SequenceEqual(right.TriggeringEvents, StringComparer.Ordinal) &&
           string.Equals(left.ErrorMessage, right.ErrorMessage, StringComparison.Ordinal) &&
           left.IsEnabled == right.IsEnabled &&
           left.Batch == right.Batch;

    private readonly record struct RestoredAttributeRule(
        MetadataV2AttributeRule Rule,
        int? PreviousIndex);

    private static MetadataV2Field RestoreSchemaFieldMutation(
        MetadataV2Field current,
        MetadataV2Field previous,
        MetadataV2Field persisted)
    {
        if (current == persisted)
        {
            return previous;
        }

        return current with
        {
            SemanticId = RestoreMutationValue(current.SemanticId, previous.SemanticId, persisted.SemanticId),
            Name = RestoreMutationValue(current.Name, previous.Name, persisted.Name),
            Type = RestoreMutationValue(current.Type, previous.Type, persisted.Type),
            Title = RestoreMutationValue(current.Title, previous.Title, persisted.Title),
            Description = RestoreMutationValue(current.Description, previous.Description, persisted.Description),
            Nullable = RestoreMutationValue(current.Nullable, previous.Nullable, persisted.Nullable),
            SemanticRoles = RestoreMutationSequence(
                current.SemanticRoles,
                previous.SemanticRoles,
                persisted.SemanticRoles),
            Alias = RestoreMutationValue(current.Alias, previous.Alias, persisted.Alias),
            EditableValue = RestoreMutationValue(
                current.EditableValue,
                previous.EditableValue,
                persisted.EditableValue),
            Length = RestoreMutationValue(current.Length, previous.Length, persisted.Length),
            DefaultValue = RestoreSchemaFieldMutationValue(
                current.DefaultValue,
                previous.DefaultValue,
                persisted.DefaultValue,
                static value => new MetadataV2Field { DefaultValue = value }),
            Domain = RestoreSchemaFieldMutationValue(
                current.Domain,
                previous.Domain,
                persisted.Domain,
                static value => new MetadataV2Field { Domain = value }),
            Hidden = RestoreMutationValue(current.Hidden, previous.Hidden, persisted.Hidden),
            SqlType = RestoreMutationValue(current.SqlType, previous.SqlType, persisted.SqlType),
            Extensions = RestoreJsonMapMutation(
                current.Extensions,
                previous.Extensions,
                persisted.Extensions),
        };
    }

    private static MetadataV2StorageBinding RestoreStorageBindingMutation(
        MetadataV2StorageBinding current,
        MetadataV2StorageBinding previous,
        MetadataV2StorageBinding persisted)
        => current with
        {
            Metadata = RestoreObjectMetadataMutation(current.Metadata, previous.Metadata, persisted.Metadata),
            ResourceId = RestoreMutationValue(current.ResourceId, previous.ResourceId, persisted.ResourceId),
            ConnectionId = RestoreMutationValue(current.ConnectionId, previous.ConnectionId, persisted.ConnectionId),
            StorageType = RestoreMutationValue(current.StorageType, previous.StorageType, persisted.StorageType),
            Locator = RestoreMutationValue(current.Locator, previous.Locator, persisted.Locator),
            StorageLayerId = RestoreMutationValue(
                current.StorageLayerId,
                previous.StorageLayerId,
                persisted.StorageLayerId),
            Capabilities = RestoreMutationSequence(
                current.Capabilities,
                previous.Capabilities,
                persisted.Capabilities),
            Options = RestoreJsonMapMutation(
                current.Options,
                previous.Options,
                persisted.Options),
            Status = RestoreStatusMutation(current.Status, previous.Status, persisted.Status),
            Extensions = RestoreJsonMapMutation(
                current.Extensions,
                previous.Extensions,
                persisted.Extensions),
        };

    private static MetadataV2Connection RestoreConnectionMutation(
        MetadataV2Connection current,
        MetadataV2Connection previous,
        MetadataV2Connection persisted)
        => current with
        {
            Metadata = RestoreObjectMetadataMutation(current.Metadata, previous.Metadata, persisted.Metadata),
            Type = RestoreMutationValue(current.Type, previous.Type, persisted.Type),
            Provider = RestoreMutationValue(current.Provider, previous.Provider, persisted.Provider),
            Endpoint = RestoreMutationValue(current.Endpoint, previous.Endpoint, persisted.Endpoint),
            SecretRef = RestoreMutationValue(current.SecretRef, previous.SecretRef, persisted.SecretRef),
            Options = RestoreJsonMapMutation(
                current.Options,
                previous.Options,
                persisted.Options),
            Status = RestoreStatusMutation(current.Status, previous.Status, persisted.Status),
            Extensions = RestoreJsonMapMutation(
                current.Extensions,
                previous.Extensions,
                persisted.Extensions),
        };

    private static MetadataV2ObjectMetadata RestoreObjectMetadataMutation(
        MetadataV2ObjectMetadata current,
        MetadataV2ObjectMetadata previous,
        MetadataV2ObjectMetadata persisted)
        => current with
        {
            Id = RestoreMutationValue(current.Id, previous.Id, persisted.Id),
            Name = RestoreMutationValue(current.Name, previous.Name, persisted.Name),
            Namespace = RestoreMutationValue(current.Namespace, previous.Namespace, persisted.Namespace),
            Tenant = RestoreMutationValue(current.Tenant, previous.Tenant, persisted.Tenant),
            Title = RestoreMutationValue(current.Title, previous.Title, persisted.Title),
            Description = RestoreMutationValue(current.Description, previous.Description, persisted.Description),
            Tags = RestoreMutationSequence(
                current.Tags,
                previous.Tags,
                persisted.Tags),
            Labels = RestoreMetadataMapMutation(
                current.Labels,
                previous.Labels,
                persisted.Labels),
            Annotations = RestoreMetadataMapMutation(
                current.Annotations,
                previous.Annotations,
                persisted.Annotations),
            Generation = RestoreMutationValue(current.Generation, previous.Generation, persisted.Generation),
            CreatedAt = RestoreMutationValue(current.CreatedAt, previous.CreatedAt, persisted.CreatedAt),
            UpdatedAt = RestoreMutationValue(current.UpdatedAt, previous.UpdatedAt, persisted.UpdatedAt),
            Keywords = RestoreMutationSequence(
                current.Keywords,
                previous.Keywords,
                persisted.Keywords),
            Themes = RestoreMutationSequence(
                current.Themes,
                previous.Themes,
                persisted.Themes),
            Language = RestoreMutationValue(current.Language, previous.Language, persisted.Language),
            License = RestoreMutationValue(current.License, previous.License, persisted.License),
            Attribution = RestoreMutationValue(current.Attribution, previous.Attribution, persisted.Attribution),
            Publisher = RestoreMutationValue(current.Publisher, previous.Publisher, persisted.Publisher),
            ContactPoint = RestoreContactPointMutation(
                current.ContactPoint,
                previous.ContactPoint,
                persisted.ContactPoint),
            Links = RestoreMetadataLinksMutation(
                current.Links,
                previous.Links,
                persisted.Links),
        };

    private static MetadataV2ContactPoint? RestoreContactPointMutation(
        MetadataV2ContactPoint? current,
        MetadataV2ContactPoint? previous,
        MetadataV2ContactPoint? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new MetadataV2ContactPoint();
        var persistedValue = persisted ?? new MetadataV2ContactPoint();
        var restored = current with
        {
            Name = RestoreMutationValue(current.Name, previousValue.Name, persistedValue.Name),
            Email = RestoreMutationValue(current.Email, previousValue.Email, persistedValue.Email),
            Url = RestoreMutationValue(current.Url, previousValue.Url, persistedValue.Url),
        };

        return previous is null && restored.Name is null && restored.Email is null && restored.Url is null
            ? null
            : restored;
    }

    private static List<MetadataV2Link> RestoreMetadataLinksMutation(
        IReadOnlyList<MetadataV2Link> current,
        IReadOnlyList<MetadataV2Link> previous,
        IReadOnlyList<MetadataV2Link> persisted)
    {
        var previousToPersisted = AlignMetadataLinks(previous, persisted);
        var persistedToPrevious = previousToPersisted.ToDictionary(
            match => match.RightIndex,
            match => match.LeftIndex);
        var persistedToCurrent = AlignMetadataLinks(persisted, current);
        var currentToPersisted = persistedToCurrent.ToDictionary(
            match => match.RightIndex,
            match => match.LeftIndex);
        var restored = new List<RestoredMetadataLink>(previous.Count + current.Count);

        for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
        {
            if (!currentToPersisted.TryGetValue(currentIndex, out var persistedIndex))
            {
                restored.Add(new RestoredMetadataLink(current[currentIndex], null));
                continue;
            }

            if (persistedToPrevious.TryGetValue(persistedIndex, out var previousIndex))
            {
                restored.Add(new RestoredMetadataLink(
                    RestoreMetadataLinkMutation(
                        current[currentIndex],
                        previous[previousIndex],
                        persisted[persistedIndex]),
                    previousIndex));
            }
            // Links introduced by the failed publication are omitted. An unmatched
            // current link is a concurrent insertion and was preserved above.
        }

        var matchedPreviousIndices = previousToPersisted
            .Select(match => match.LeftIndex)
            .ToHashSet();
        for (var previousIndex = 0; previousIndex < previous.Count; previousIndex++)
        {
            if (matchedPreviousIndices.Contains(previousIndex))
            {
                continue;
            }

            var recreatedIndex = restored.FindIndex(item =>
                item.PreviousIndex is null && item.Link == previous[previousIndex]);
            if (recreatedIndex >= 0)
            {
                restored[recreatedIndex] = restored[recreatedIndex] with { PreviousIndex = previousIndex };
                continue;
            }

            var insertionIndex = restored.FindIndex(item => item.PreviousIndex > previousIndex);
            restored.Insert(
                insertionIndex >= 0 ? insertionIndex : restored.Count,
                new RestoredMetadataLink(previous[previousIndex], previousIndex));
        }

        return restored.Select(item => item.Link).ToList();
    }

    private static MetadataV2Link RestoreMetadataLinkMutation(
        MetadataV2Link current,
        MetadataV2Link previous,
        MetadataV2Link persisted)
        => current with
        {
            Href = RestoreMutationValue(current.Href, previous.Href, persisted.Href),
            Rel = RestoreMutationValue(current.Rel, previous.Rel, persisted.Rel),
            Type = RestoreMutationValue(current.Type, previous.Type, persisted.Type),
            Title = RestoreMutationValue(current.Title, previous.Title, persisted.Title),
            Hreflang = RestoreMutationValue(current.Hreflang, previous.Hreflang, persisted.Hreflang),
            ManagedBy = RestoreMutationValue(current.ManagedBy, previous.ManagedBy, persisted.ManagedBy),
        };

    private static List<MetadataLinkMatch> AlignMetadataLinks(
        IReadOnlyList<MetadataV2Link> left,
        IReadOnlyList<MetadataV2Link> right)
    {
        const int gapPenalty = -2;
        var scores = new int[left.Count + 1, right.Count + 1];
        var moves = new byte[left.Count + 1, right.Count + 1];
        for (var leftIndex = 1; leftIndex <= left.Count; leftIndex++)
        {
            scores[leftIndex, 0] = scores[leftIndex - 1, 0] + gapPenalty;
            moves[leftIndex, 0] = 2;
        }

        for (var rightIndex = 1; rightIndex <= right.Count; rightIndex++)
        {
            scores[0, rightIndex] = scores[0, rightIndex - 1] + gapPenalty;
            moves[0, rightIndex] = 3;
        }

        for (var leftIndex = 1; leftIndex <= left.Count; leftIndex++)
        {
            for (var rightIndex = 1; rightIndex <= right.Count; rightIndex++)
            {
                var similarity = GetMetadataLinkSimilarity(left[leftIndex - 1], right[rightIndex - 1]);
                var matchScore = similarity > 0
                    ? scores[leftIndex - 1, rightIndex - 1] + similarity + 4
                    : int.MinValue;
                var skipLeftScore = scores[leftIndex - 1, rightIndex] + gapPenalty;
                var skipRightScore = scores[leftIndex, rightIndex - 1] + gapPenalty;
                if (matchScore >= skipLeftScore && matchScore >= skipRightScore)
                {
                    scores[leftIndex, rightIndex] = matchScore;
                    moves[leftIndex, rightIndex] = 1;
                }
                else if (skipLeftScore >= skipRightScore)
                {
                    scores[leftIndex, rightIndex] = skipLeftScore;
                    moves[leftIndex, rightIndex] = 2;
                }
                else
                {
                    scores[leftIndex, rightIndex] = skipRightScore;
                    moves[leftIndex, rightIndex] = 3;
                }
            }
        }

        var matches = new List<MetadataLinkMatch>(Math.Min(left.Count, right.Count));
        var leftCursor = left.Count;
        var rightCursor = right.Count;
        while (leftCursor > 0 || rightCursor > 0)
        {
            switch (moves[leftCursor, rightCursor])
            {
                case 1:
                    matches.Add(new MetadataLinkMatch(leftCursor - 1, rightCursor - 1));
                    leftCursor--;
                    rightCursor--;
                    break;
                case 2:
                    leftCursor--;
                    break;
                default:
                    rightCursor--;
                    break;
            }
        }

        matches.Reverse();
        return matches;
    }

    private static int GetMetadataLinkSimilarity(MetadataV2Link left, MetadataV2Link right)
    {
        var score = 0;
        if (string.Equals(left.Href, right.Href, StringComparison.Ordinal))
        {
            score += 8;
        }
        if (string.Equals(left.Rel, right.Rel, StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }
        if (left.ManagedBy is not null &&
            string.Equals(left.ManagedBy, right.ManagedBy, StringComparison.Ordinal))
        {
            score += 4;
        }
        if (left.Type is not null && string.Equals(left.Type, right.Type, StringComparison.Ordinal))
        {
            score += 2;
        }
        if (left.Title is not null && string.Equals(left.Title, right.Title, StringComparison.Ordinal))
        {
            score += 2;
        }
        if (left.Hreflang is not null && string.Equals(left.Hreflang, right.Hreflang, StringComparison.Ordinal))
        {
            score += 2;
        }

        return score;
    }

    private readonly record struct MetadataLinkMatch(int LeftIndex, int RightIndex);

    private readonly record struct RestoredMetadataLink(MetadataV2Link Link, int? PreviousIndex);

    private static MetadataV2ResourceSpatial? RestoreResourceSpatialMutation(
        MetadataV2ResourceSpatial? current,
        MetadataV2ResourceSpatial? previous,
        MetadataV2ResourceSpatial? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new MetadataV2ResourceSpatial();
        var persistedValue = persisted ?? new MetadataV2ResourceSpatial();
        var restored = current with
        {
            SpatialReference = RestoreSpatialReferenceMutation(
                current.SpatialReference,
                previousValue.SpatialReference,
                persistedValue.SpatialReference),
            GeometryType = RestoreMutationValue(
                current.GeometryType,
                previousValue.GeometryType,
                persistedValue.GeometryType),
            Bbox = RestoreBboxMutation(
                current.Bbox,
                previousValue.Bbox,
                persistedValue.Bbox),
            PrimaryGeometryField = RestoreMutationValue(
                current.PrimaryGeometryField,
                previousValue.PrimaryGeometryField,
                persistedValue.PrimaryGeometryField),
            SupportedCrs = RestoreMutationSequence(
                current.SupportedCrs,
                previousValue.SupportedCrs,
                persistedValue.SupportedCrs),
            StorageCrs = RestoreSpatialReferenceMutation(
                current.StorageCrs,
                previousValue.StorageCrs,
                persistedValue.StorageCrs),
            StorageCrsCoordinateEpoch = RestoreMutationValue(
                current.StorageCrsCoordinateEpoch,
                previousValue.StorageCrsCoordinateEpoch,
                persistedValue.StorageCrsCoordinateEpoch),
        };

        return previous is null &&
               restored.SpatialReference is null &&
               restored.GeometryType == MetadataV2GeometryType.None &&
               restored.Bbox is null &&
               restored.PrimaryGeometryField is null &&
               restored.SupportedCrs.Count == 0 &&
               restored.StorageCrs is null &&
               restored.StorageCrsCoordinateEpoch is null
            ? null
            : restored;
    }

    private static MetadataV2Bbox? RestoreBboxMutation(
        MetadataV2Bbox? current,
        MetadataV2Bbox? previous,
        MetadataV2Bbox? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new MetadataV2Bbox();
        var persistedValue = persisted ?? new MetadataV2Bbox();
        var restored = current with
        {
            West = RestoreMutationValue(current.West, previousValue.West, persistedValue.West),
            South = RestoreMutationValue(current.South, previousValue.South, persistedValue.South),
            East = RestoreMutationValue(current.East, previousValue.East, persistedValue.East),
            North = RestoreMutationValue(current.North, previousValue.North, persistedValue.North),
        };

        return previous is null &&
               restored.West.Equals(0d) &&
               restored.South.Equals(0d) &&
               restored.East.Equals(0d) &&
               restored.North.Equals(0d)
            ? null
            : restored;
    }

    private static AccessPolicy? RestoreAccessPolicyMutation(
        AccessPolicy? current,
        AccessPolicy? previous,
        AccessPolicy? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new AccessPolicy();
        var persistedValue = persisted ?? new AccessPolicy();
        var restored = current with
        {
            AllowAnonymous = RestoreMutationValue(
                current.AllowAnonymous,
                previousValue.AllowAnonymous,
                persistedValue.AllowAnonymous),
            AllowAnonymousWrite = RestoreMutationValue(
                current.AllowAnonymousWrite,
                previousValue.AllowAnonymousWrite,
                persistedValue.AllowAnonymousWrite),
            AllowedRoles = RestoreAccessPolicyRolesMutation(
                current.AllowedRoles,
                previousValue.AllowedRoles,
                persistedValue.AllowedRoles),
            AllowedWriteRoles = RestoreAccessPolicyRolesMutation(
                current.AllowedWriteRoles,
                previousValue.AllowedWriteRoles,
                persistedValue.AllowedWriteRoles),
        };

        return previous is null &&
               !restored.AllowAnonymous &&
               !restored.AllowAnonymousWrite &&
               restored.AllowedRoles is null &&
               restored.AllowedWriteRoles is null
            ? null
            : restored;
    }

    private static string[]? RestoreAccessPolicyRolesMutation(
        string[]? current,
        string[]? previous,
        string[]? persisted)
        => !NullableSequenceEqual(previous, persisted) && NullableSequenceEqual(current, persisted)
            ? previous
            : current;

    private static bool NullableSequenceEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
        => left is null ? right is null : right is not null && left.SequenceEqual(right);

    private static MetadataV2ResourceTemporal? RestoreResourceTemporalMutation(
        MetadataV2ResourceTemporal? current,
        MetadataV2ResourceTemporal? previous,
        MetadataV2ResourceTemporal? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new MetadataV2ResourceTemporal();
        var persistedValue = persisted ?? new MetadataV2ResourceTemporal();
        var restored = current with
        {
            StartTimeField = RestoreMutationValue(
                current.StartTimeField,
                previousValue.StartTimeField,
                persistedValue.StartTimeField),
            EndTimeField = RestoreMutationValue(
                current.EndTimeField,
                previousValue.EndTimeField,
                persistedValue.EndTimeField),
            TrackIdField = RestoreMutationValue(
                current.TrackIdField,
                previousValue.TrackIdField,
                persistedValue.TrackIdField),
            Extent = RestoreTimeRangeMutation(current.Extent, previousValue.Extent, persistedValue.Extent),
        };

        return previous is null &&
               restored.StartTimeField is null &&
               restored.EndTimeField is null &&
               restored.TrackIdField is null &&
               restored.Extent is null
            ? null
            : restored;
    }

    private static MetadataV2TimeRange? RestoreTimeRangeMutation(
        MetadataV2TimeRange? current,
        MetadataV2TimeRange? previous,
        MetadataV2TimeRange? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new MetadataV2TimeRange();
        var persistedValue = persisted ?? new MetadataV2TimeRange();
        var restored = current with
        {
            Start = RestoreMutationValue(current.Start, previousValue.Start, persistedValue.Start),
            End = RestoreMutationValue(current.End, previousValue.End, persistedValue.End),
        };

        return previous is null && restored.Start is null && restored.End is null ? null : restored;
    }

    private static T RestoreResourceMutationValue<T>(
        T current,
        T previous,
        T persisted,
        Func<T, MetadataV2Resource> containerFactory,
        Func<MetadataV2Resource, T> valueSelector)
    {
        var typeInfo = MetadataV2JsonContext.Default.MetadataV2Resource;
        var currentNode = JsonSerializer.SerializeToNode(containerFactory(current), typeInfo);
        var previousNode = JsonSerializer.SerializeToNode(containerFactory(previous), typeInfo);
        var persistedNode = JsonSerializer.SerializeToNode(containerFactory(persisted), typeInfo);
        var restoredNode = RestoreJsonNodeMutation(
            new OptionalJsonNode(true, currentNode),
            new OptionalJsonNode(true, previousNode),
            new OptionalJsonNode(true, persistedNode));
        var restoredContainer = JsonSerializer.Deserialize(restoredNode.Node!.ToJsonString(), typeInfo)
            ?? throw new InvalidOperationException("Resource compensation produced invalid metadata JSON.");
        return valueSelector(restoredContainer);
    }

    private static OptionalJsonNode RestoreJsonNodeMutation(
        OptionalJsonNode current,
        OptionalJsonNode previous,
        OptionalJsonNode persisted)
    {
        if (JsonNodesEquivalent(previous, persisted))
        {
            return current.DeepClone();
        }

        if (JsonNodesEquivalent(current, persisted))
        {
            return previous.DeepClone();
        }

        if (current.Node is JsonObject currentObject &&
            IsJsonObjectOrEmpty(previous) &&
            IsJsonObjectOrEmpty(persisted))
        {
            return new OptionalJsonNode(
                true,
                RestoreJsonObjectMutation(
                    currentObject,
                    previous.Node as JsonObject,
                    persisted.Node as JsonObject));
        }

        if (current.Node is JsonArray currentArray &&
            IsJsonArrayOrEmpty(previous) &&
            IsJsonArrayOrEmpty(persisted))
        {
            return new OptionalJsonNode(
                true,
                RestoreJsonArrayMutation(
                    currentArray,
                    previous.Node as JsonArray ?? new JsonArray(),
                    persisted.Node as JsonArray ?? new JsonArray()));
        }

        return current.DeepClone();
    }

    private static JsonObject RestoreJsonObjectMutation(
        JsonObject current,
        JsonObject? previous,
        JsonObject? persisted)
    {
        var propertyNames = current.Select(property => property.Key)
            .Concat(previous?.Select(property => property.Key) ?? [])
            .Concat(persisted?.Select(property => property.Key) ?? [])
            .Distinct(StringComparer.Ordinal);
        var restored = new JsonObject();
        foreach (var propertyName in propertyNames)
        {
            var restoredProperty = RestoreJsonNodeMutation(
                GetOptionalJsonProperty(current, propertyName),
                GetOptionalJsonProperty(previous, propertyName),
                GetOptionalJsonProperty(persisted, propertyName));
            if (restoredProperty.Exists)
            {
                restored[propertyName] = restoredProperty.Node;
            }
        }

        return restored;
    }

    private static JsonArray RestoreJsonArrayMutation(
        JsonArray current,
        JsonArray previous,
        JsonArray persisted)
    {
        if (TryGetStableJsonArrayIdentity(current, previous, persisted, out var identityProperty))
        {
            return RestoreIdentifiedJsonArrayMutation(
                current,
                previous,
                persisted,
                identityProperty);
        }

        var restoredJson = RestoreMutationSequence(
            current.Select(JsonNodeText).ToArray(),
            previous.Select(JsonNodeText).ToArray(),
            persisted.Select(JsonNodeText).ToArray());
        var restored = new JsonArray();
        foreach (var json in restoredJson)
        {
            restored.Add(JsonNode.Parse(json));
        }

        return restored;
    }

    private static JsonArray RestoreIdentifiedJsonArrayMutation(
        JsonArray current,
        JsonArray previous,
        JsonArray persisted,
        string identityProperty)
    {
        var currentIdentities = GetJsonArrayOccurrenceIdentities(current, identityProperty);
        var previousIdentities = GetJsonArrayOccurrenceIdentities(previous, identityProperty);
        var persistedIdentities = GetJsonArrayOccurrenceIdentities(persisted, identityProperty);
        var previousIndices = previousIdentities
            .Select((identity, index) => new { Identity = identity, Index = index })
            .ToDictionary(item => item.Identity, item => item.Index, StringComparer.Ordinal);
        var persistedIndices = persistedIdentities
            .Select((identity, index) => new { Identity = identity, Index = index })
            .ToDictionary(item => item.Identity, item => item.Index, StringComparer.Ordinal);
        var restored = new List<RestoredJsonArrayItem>(previous.Count + current.Count);
        for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
        {
            var currentItem = current[currentIndex]!;
            var identity = currentIdentities[currentIndex];
            if (!persistedIndices.TryGetValue(identity, out var persistedIndex))
            {
                restored.Add(new RestoredJsonArrayItem(currentItem.DeepClone(), null, identity));
                continue;
            }

            var persistedItem = persisted[persistedIndex]!;
            if (previousIndices.TryGetValue(identity, out var previousIndex))
            {
                var restoredItem = RestoreJsonNodeMutation(
                    new OptionalJsonNode(true, currentItem),
                    new OptionalJsonNode(true, previous[previousIndex]),
                    new OptionalJsonNode(true, persistedItem));
                restored.Add(new RestoredJsonArrayItem(restoredItem.Node, previousIndex, identity));
            }
            else if (!JsonNode.DeepEquals(currentItem, persistedItem))
            {
                restored.Add(new RestoredJsonArrayItem(currentItem.DeepClone(), null, identity));
            }
            // Items introduced only by the failed mutation are otherwise omitted.
        }

        for (var previousIndex = 0; previousIndex < previous.Count; previousIndex++)
        {
            var previousItem = previous[previousIndex]!;
            var identity = previousIdentities[previousIndex];
            if (persistedIndices.ContainsKey(identity))
            {
                continue;
            }

            var recreatedIndex = restored.FindIndex(item =>
                item.PreviousIndex is null &&
                string.Equals(item.Identity, identity, StringComparison.Ordinal));
            if (recreatedIndex >= 0)
            {
                restored[recreatedIndex] = restored[recreatedIndex] with { PreviousIndex = previousIndex };
                continue;
            }

            var insertionIndex = restored.FindIndex(item => item.PreviousIndex > previousIndex);
            restored.Insert(
                insertionIndex >= 0 ? insertionIndex : restored.Count,
                new RestoredJsonArrayItem(previousItem.DeepClone(), previousIndex, identity));
        }

        var restoredArray = new JsonArray();
        foreach (var item in restored)
        {
            restoredArray.Add(item.Node);
        }

        return restoredArray;
    }

    private static bool TryGetStableJsonArrayIdentity(
        JsonArray current,
        JsonArray previous,
        JsonArray persisted,
        out string identityProperty)
    {
        string[] identityProperties = ["id", "code", "name", "encoding", "attribute"];
        if (!current.Concat(previous).Concat(persisted).Any())
        {
            identityProperty = string.Empty;
            return false;
        }

        identityProperty = identityProperties.FirstOrDefault(candidate =>
            HasJsonArrayIdentity(current, candidate) &&
            HasJsonArrayIdentity(previous, candidate) &&
            HasJsonArrayIdentity(persisted, candidate)) ?? string.Empty;
        return identityProperty.Length > 0;
    }

    private static bool HasJsonArrayIdentity(JsonArray array, string identityProperty)
        => array.All(item =>
            item is JsonObject itemObject &&
            itemObject.TryGetPropertyValue(identityProperty, out var identityNode) &&
            identityNode is not null);

    private static string[] GetJsonArrayOccurrenceIdentities(JsonArray array, string identityProperty)
    {
        var isCaseInsensitive = identityProperty is "name" or "encoding" or "attribute";
        var identityComparer = isCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var occurrences = new Dictionary<string, int>(identityComparer);
        return array.Select(item =>
        {
            var identity = GetJsonArrayIdentity(item!, identityProperty);
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            var normalizedIdentity = isCaseInsensitive
                ? identity.ToUpperInvariant()
                : identity;
            return string.Concat(
                normalizedIdentity,
                "\u001F",
                occurrence.ToString(CultureInfo.InvariantCulture));
        })
            .ToArray();
    }

    private static string GetJsonArrayIdentity(JsonNode item, string identityProperty)
        => JsonNodeText(item[identityProperty]!);

    private static string JsonNodeText(JsonNode? node)
        => node?.ToJsonString() ?? "null";

    private static bool IsJsonObjectOrEmpty(OptionalJsonNode value)
        => !value.Exists || value.Node is null || value.Node is JsonObject;

    private static bool IsJsonArrayOrEmpty(OptionalJsonNode value)
        => !value.Exists || value.Node is null || value.Node is JsonArray;

    private static OptionalJsonNode GetOptionalJsonProperty(JsonObject? value, string propertyName)
        => value is not null && value.TryGetPropertyValue(propertyName, out var propertyValue)
            ? new OptionalJsonNode(true, propertyValue)
            : new OptionalJsonNode(false, null);

    private static bool JsonNodesEquivalent(OptionalJsonNode left, OptionalJsonNode right)
        => left.Exists == right.Exists &&
           (!left.Exists || JsonNode.DeepEquals(left.Node, right.Node));

    private readonly record struct OptionalJsonNode(bool Exists, JsonNode? Node)
    {
        public OptionalJsonNode DeepClone()
            => new(Exists, Node?.DeepClone());
    }

    private readonly record struct RestoredJsonArrayItem(
        JsonNode? Node,
        int? PreviousIndex,
        string Identity);

    private static T RestorePublicationMutationValue<T>(
        T current,
        T previous,
        T persisted,
        Func<T, MetadataV2Publication> containerFactory)
        => RestoreJsonMutationValue(
            current,
            previous,
            persisted,
            containerFactory,
            MetadataV2JsonContext.Default.MetadataV2Publication);

    private static T RestoreSchemaFieldMutationValue<T>(
        T current,
        T previous,
        T persisted,
        Func<T, MetadataV2Field> containerFactory)
        => RestoreJsonMutationValue(
            current,
            previous,
            persisted,
            containerFactory,
            MetadataV2JsonContext.Default.MetadataV2Field);

    private static T RestoreJsonMutationValue<T, TContainer>(
        T current,
        T previous,
        T persisted,
        Func<T, TContainer> containerFactory,
        JsonTypeInfo<TContainer> typeInfo)
        => !JsonEquivalent(containerFactory(previous), containerFactory(persisted), typeInfo) &&
           JsonEquivalent(containerFactory(current), containerFactory(persisted), typeInfo)
            ? previous
            : current;

    private static MetadataV2ObjectMetadata RestoreServiceMetadataMutation(
        MetadataV2ObjectMetadata current,
        MetadataV2ObjectMetadata previous,
        MetadataV2ObjectMetadata persisted)
        => current with
        {
            Name = RestoreMutationValue(current.Name, previous.Name, persisted.Name),
            Title = RestoreMutationValue(current.Title, previous.Title, persisted.Title),
            Description = RestoreMutationValue(current.Description, previous.Description, persisted.Description),
            CreatedAt = RestoreMutationValue(current.CreatedAt, previous.CreatedAt, persisted.CreatedAt),
            UpdatedAt = RestoreMutationValue(current.UpdatedAt, previous.UpdatedAt, persisted.UpdatedAt),
        };

    private static MetadataV2SpatialReference? RestoreSpatialReferenceMutation(
        MetadataV2SpatialReference? current,
        MetadataV2SpatialReference? previous,
        MetadataV2SpatialReference? persisted)
    {
        if (current is null)
        {
            return persisted is null && previous is not null ? previous : null;
        }

        var previousValue = previous ?? new MetadataV2SpatialReference();
        var persistedValue = persisted ?? new MetadataV2SpatialReference();
        var restored = current with
        {
            Srid = RestoreMutationValue(current.Srid, previousValue.Srid, persistedValue.Srid),
            Crs = RestoreMutationValue(current.Crs, previousValue.Crs, persistedValue.Crs),
            IsGeographic = RestoreMutationValue(
                current.IsGeographic,
                previousValue.IsGeographic,
                persistedValue.IsGeographic),
        };

        return previous is null && restored.Srid is null && restored.Crs is null && !restored.IsGeographic
            ? null
            : restored;
    }

    private static MetadataV2Status RestoreStatusMutation(
        MetadataV2Status current,
        MetadataV2Status previous,
        MetadataV2Status persisted)
        => current with
        {
            Lifecycle = RestoreMutationValue(current.Lifecycle, previous.Lifecycle, persisted.Lifecycle),
            State = RestoreMutationValue(current.State, previous.State, persisted.State),
            Conditions = RestoreMutationSequence(current.Conditions, previous.Conditions, persisted.Conditions),
            ObservedAt = RestoreMutationValue(current.ObservedAt, previous.ObservedAt, persisted.ObservedAt),
        };

    private static T RestoreMutationValue<T>(T current, T previous, T persisted)
        => !EqualityComparer<T>.Default.Equals(previous, persisted) &&
           EqualityComparer<T>.Default.Equals(current, persisted)
            ? previous
            : current;

    private static IReadOnlyList<T> RestoreMutationSequence<T>(
        IReadOnlyList<T> current,
        IReadOnlyList<T> previous,
        IReadOnlyList<T> persisted)
    {
        if (previous.SequenceEqual(persisted))
        {
            return current;
        }

        var mutationAnchors = AlignEqualSequence(previous, persisted);
        var persistedToPrevious = new Dictionary<int, int>();
        var pendingPrevious = new List<(int Boundary, int PreviousIndex)>();
        var previousCursor = 0;
        var persistedCursor = 0;
        for (var anchorIndex = 0; anchorIndex <= mutationAnchors.Count; anchorIndex++)
        {
            var hasAnchor = anchorIndex < mutationAnchors.Count;
            var previousEnd = hasAnchor ? mutationAnchors[anchorIndex].LeftIndex : previous.Count;
            var persistedEnd = hasAnchor ? mutationAnchors[anchorIndex].RightIndex : persisted.Count;
            var pairedCount = Math.Min(previousEnd - previousCursor, persistedEnd - persistedCursor);
            for (var offset = 0; offset < pairedCount; offset++)
            {
                persistedToPrevious[persistedCursor + offset] = previousCursor + offset;
            }

            for (var previousIndex = previousCursor + pairedCount;
                 previousIndex < previousEnd;
                 previousIndex++)
            {
                pendingPrevious.Add((persistedEnd, previousIndex));
            }

            if (!hasAnchor)
            {
                break;
            }

            var anchor = mutationAnchors[anchorIndex];
            persistedToPrevious[anchor.RightIndex] = anchor.LeftIndex;
            previousCursor = anchor.LeftIndex + 1;
            persistedCursor = anchor.RightIndex + 1;
        }

        var persistedToCurrent = MatchEqualSequenceOccurrences(persisted, current);
        var currentToPersisted = persistedToCurrent.ToDictionary(
            match => match.RightIndex,
            match => match.LeftIndex);
        var matchedPersistedIndices = persistedToCurrent
            .Select(match => match.LeftIndex)
            .ToHashSet();
        foreach (var mapping in persistedToPrevious.Where(mapping =>
                     !matchedPersistedIndices.Contains(mapping.Key) &&
                     !EqualityComparer<T>.Default.Equals(previous[mapping.Value], persisted[mapping.Key])))
        {
            pendingPrevious.Add((mapping.Key, mapping.Value));
        }

        var insertions = new Dictionary<int, List<int>>();
        foreach (var pending in pendingPrevious.OrderBy(item => item.PreviousIndex))
        {
            var insertionIndex = persisted.Count == 0
                ? 0
                : persistedToCurrent
                    .Where(match => match.LeftIndex >= pending.Boundary)
                    .OrderBy(match => match.LeftIndex)
                    .Select(match => match.RightIndex)
                    .DefaultIfEmpty(current.Count)
                    .First();
            if (!insertions.TryGetValue(insertionIndex, out var previousIndices))
            {
                previousIndices = [];
                insertions[insertionIndex] = previousIndices;
            }
            previousIndices.Add(pending.PreviousIndex);
        }

        var restored = new List<T>(previous.Count + current.Count);
        for (var currentIndex = 0; currentIndex <= current.Count; currentIndex++)
        {
            if (insertions.TryGetValue(currentIndex, out var previousIndices))
            {
                restored.AddRange(previousIndices.Select(previousIndex => previous[previousIndex]));
            }

            if (currentIndex == current.Count)
            {
                break;
            }

            if (!currentToPersisted.TryGetValue(currentIndex, out var persistedIndex))
            {
                restored.Add(current[currentIndex]);
                continue;
            }

            if (persistedToPrevious.TryGetValue(persistedIndex, out var previousIndex))
            {
                restored.Add(EqualityComparer<T>.Default.Equals(
                    previous[previousIndex],
                    persisted[persistedIndex])
                    ? current[currentIndex]
                    : previous[previousIndex]);
            }
            // Values introduced only by the failed mutation are omitted.
        }

        return restored;
    }

    private static List<SequenceIndexMatch> AlignEqualSequence<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right)
    {
        var lengths = new int[left.Count + 1, right.Count + 1];
        for (var leftIndex = left.Count - 1; leftIndex >= 0; leftIndex--)
        {
            for (var rightIndex = right.Count - 1; rightIndex >= 0; rightIndex--)
            {
                lengths[leftIndex, rightIndex] = EqualityComparer<T>.Default.Equals(
                    left[leftIndex],
                    right[rightIndex])
                    ? lengths[leftIndex + 1, rightIndex + 1] + 1
                    : Math.Max(lengths[leftIndex + 1, rightIndex], lengths[leftIndex, rightIndex + 1]);
            }
        }

        var matches = new List<SequenceIndexMatch>(lengths[0, 0]);
        var leftCursor = 0;
        var rightCursor = 0;
        while (leftCursor < left.Count && rightCursor < right.Count)
        {
            if (EqualityComparer<T>.Default.Equals(left[leftCursor], right[rightCursor]))
            {
                matches.Add(new SequenceIndexMatch(leftCursor, rightCursor));
                leftCursor++;
                rightCursor++;
            }
            else if (lengths[leftCursor + 1, rightCursor] >= lengths[leftCursor, rightCursor + 1])
            {
                leftCursor++;
            }
            else
            {
                rightCursor++;
            }
        }

        return matches;
    }

    private static List<SequenceIndexMatch> MatchEqualSequenceOccurrences<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right)
    {
        var matches = new List<SequenceIndexMatch>(Math.Min(left.Count, right.Count));
        var matchedRight = new HashSet<int>();
        for (var leftIndex = 0; leftIndex < left.Count; leftIndex++)
        {
            for (var rightIndex = 0; rightIndex < right.Count; rightIndex++)
            {
                if (matchedRight.Contains(rightIndex) ||
                    !EqualityComparer<T>.Default.Equals(left[leftIndex], right[rightIndex]))
                {
                    continue;
                }

                matches.Add(new SequenceIndexMatch(leftIndex, rightIndex));
                matchedRight.Add(rightIndex);
                break;
            }
        }

        return matches;
    }

    private readonly record struct SequenceIndexMatch(int LeftIndex, int RightIndex);

    private static Dictionary<string, string> RestoreMetadataMapMutation(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> persisted)
    {
        var restored = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var key in previous.Keys.Concat(persisted.Keys).Distinct(StringComparer.Ordinal))
        {
            var hadPrevious = previous.TryGetValue(key, out var previousValue);
            var wasPersisted = persisted.TryGetValue(key, out var persistedValue);
            if (hadPrevious == wasPersisted &&
                (!hadPrevious || string.Equals(previousValue, persistedValue, StringComparison.Ordinal)))
            {
                continue;
            }

            var hasCurrent = current.TryGetValue(key, out var currentValue);
            if (!wasPersisted)
            {
                if (hadPrevious && !hasCurrent)
                {
                    restored[key] = previousValue!;
                }

                continue;
            }

            if (hasCurrent && string.Equals(currentValue, persistedValue, StringComparison.Ordinal))
            {
                if (hadPrevious)
                {
                    restored[key] = previousValue!;
                }
                else
                {
                    restored.Remove(key);
                }
            }
        }

        return restored;
    }

    private static Dictionary<string, JsonElement> RestoreJsonMapMutation(
        IReadOnlyDictionary<string, JsonElement> current,
        IReadOnlyDictionary<string, JsonElement> previous,
        IReadOnlyDictionary<string, JsonElement> persisted)
    {
        var restored = new Dictionary<string, JsonElement>(current, StringComparer.Ordinal);
        foreach (var key in previous.Keys.Concat(persisted.Keys).Distinct(StringComparer.Ordinal))
        {
            var hadPrevious = previous.TryGetValue(key, out var previousValue);
            var wasPersisted = persisted.TryGetValue(key, out var persistedValue);
            if (hadPrevious == wasPersisted &&
                (!hadPrevious || JsonElement.DeepEquals(previousValue, persistedValue)))
            {
                continue;
            }

            var hasCurrent = current.TryGetValue(key, out var currentValue);
            if (!wasPersisted)
            {
                if (hadPrevious && !hasCurrent)
                {
                    restored[key] = previousValue;
                }

                continue;
            }

            if (hasCurrent && JsonElement.DeepEquals(currentValue, persistedValue))
            {
                if (hadPrevious)
                {
                    restored[key] = previousValue;
                }
                else
                {
                    restored.Remove(key);
                }
            }
        }

        return restored;
    }

    private static bool JsonEquivalent<T>(T left, T right, JsonTypeInfo<T> typeInfo)
        => JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(left, typeInfo),
            JsonSerializer.SerializeToElement(right, typeInfo));

    internal static MetadataV2Graph BuildLinkedLayerMetadataV2Graph(
        MetadataV2Graph graph,
        string serviceName,
        int layerId,
        string layerName,
        int srid,
        DateTimeOffset now,
        bool enabled = true)
    {
        var storageLayerBindings = graph.StorageBindings
            .Where(candidate => candidate.StorageLayerId == layerId)
            .ToArray();
        var canonicalBindingId = BuildStorageBindingId(layerId);
        var binding = storageLayerBindings.FirstOrDefault(candidate =>
            string.Equals(candidate.Metadata.Id, canonicalBindingId, StringComparison.Ordinal));
        if (binding is null && storageLayerBindings.Length > 1)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Layer {layerId} has multiple legacy storage bindings and no canonical '{canonicalBindingId}' binding.",
                layerId);
        }

        binding ??= storageLayerBindings.SingleOrDefault();
        var resource = binding is null
            ? null
            : graph.Resources.FirstOrDefault(candidate =>
                string.Equals(candidate.Metadata.Id, binding.ResourceId, StringComparison.Ordinal));
        if (binding is null)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Layer {layerId} is absent from the canonical metadata graph and cannot be linked to service '{serviceName}'.",
                layerId);
        }

        if (resource is null)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Layer {layerId} references missing canonical resource '{binding.ResourceId}' and cannot be linked to service '{serviceName}'.",
                layerId);
        }

        var linkedStatus = LayerReadyStatus(enabled, now);
        binding = binding with { Status = linkedStatus };
        var storageBindings = UpsertById(
            graph.StorageBindings,
            binding,
            static item => item.Metadata.Id);
        resource = resource with
        {
            Status = resource.Status with
            {
                Lifecycle = DeriveResourceLifecycleFromBindings(storageBindings, resource.Metadata.Id),
                State = MetadataV2OperationalState.Ready,
                ObservedAt = now,
            },
        };

        var service = ResolveUniquePublishedFeatureService(graph, serviceName, layerId);
        service ??= BuildPublishedService(graph, serviceName, srid, now);
        var existingFeaturePublication = graph.Publications.FirstOrDefault(publication =>
            string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal) &&
            string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal) &&
            string.Equals(publication.StorageBindingId, binding.Metadata.Id, StringComparison.Ordinal) &&
            publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);
        MetadataV2Publication featurePublication;
        if (existingFeaturePublication is not null)
        {
            featurePublication = existingFeaturePublication with { Status = linkedStatus };
        }
        else
        {
            var collidingFeaturePublication = graph.Publications.FirstOrDefault(publication =>
                string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal) &&
                publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer &&
                publication.LayerIndex == layerId &&
                (!string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal) ||
                 !string.Equals(publication.StorageBindingId, binding.Metadata.Id, StringComparison.Ordinal)));
            if (collidingFeaturePublication is not null)
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Conflict,
                    $"Service '{serviceName}' already publishes FeatureServer layer {layerId} from another storage binding.",
                    layerId);
            }

            var layerIdText = layerId.ToString(CultureInfo.InvariantCulture);
            featurePublication = BuildPublishedPublication(
                service,
                resource,
                binding,
                layerIdText,
                layerName,
                MetadataV2PublicationType.EsriFeatureLayer,
                isPrimary: true,
                idPrefix: "pub",
                enabled,
                now) with
            { Status = linkedStatus };
        }
        service = service with
        {
            PublicationIds = service.PublicationIds
                .Append(featurePublication.Metadata.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        var synchronizedPublications = graph.Publications
            .Select(publication =>
            {
                var publicationBindingId = publication.StorageBindingId;
                if (publicationBindingId is null &&
                    string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
                {
                    publicationBindingId = resource.PrimaryStorageBindingId;
                }

                return string.Equals(publicationBindingId, binding.Metadata.Id, StringComparison.Ordinal)
                    ? publication with { Status = linkedStatus }
                    : publication;
            })
            .ToArray();

        return graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Services = UpsertById(graph.Services, service, static item => item.Metadata.Id),
            Resources = UpsertById(graph.Resources, resource, static item => item.Metadata.Id),
            StorageBindings = storageBindings,
            Publications = UpsertPublication(synchronizedPublications, featurePublication)
        };
    }

    internal static MetadataV2Graph BuildLayerEnabledMetadataV2Graph(
        MetadataV2Graph graph,
        HashSet<int> layerIds,
        bool enabled,
        DateTimeOffset now)
    {
        var affectedBindingIds = graph.StorageBindings
            .Where(binding => binding.StorageLayerId is { } storageLayerId && layerIds.Contains(storageLayerId))
            .Select(binding => binding.Metadata.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (affectedBindingIds.Count == 0)
        {
            return graph;
        }

        var lifecycle = enabled
            ? MetadataV2LifecycleStatus.Active
            : MetadataV2LifecycleStatus.Retired;
        var changed = false;
        MetadataV2Status SetLifecycle(MetadataV2Status status, MetadataV2LifecycleStatus targetLifecycle)
            => status with
            {
                Lifecycle = targetLifecycle,
                State = MetadataV2OperationalState.Ready,
                ObservedAt = now,
            };

        var storageBindings = graph.StorageBindings
            .Select(binding =>
            {
                if (!affectedBindingIds.Contains(binding.Metadata.Id))
                {
                    return binding;
                }

                changed = true;
                return binding with { Status = SetLifecycle(binding.Status, lifecycle) };
            })
            .ToArray();
        var affectedResourceIds = storageBindings
            .Where(binding => affectedBindingIds.Contains(binding.Metadata.Id))
            .Select(binding => binding.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
        var resources = graph.Resources
            .Select(resource =>
            {
                if (!affectedResourceIds.Contains(resource.Metadata.Id))
                {
                    return resource;
                }

                changed = true;
                var resourceLifecycle = DeriveResourceLifecycleFromBindings(
                    storageBindings,
                    resource.Metadata.Id);
                return resource with { Status = SetLifecycle(resource.Status, resourceLifecycle) };
            })
            .ToArray();
        var resourcesById = graph.Resources.ToDictionary(resource => resource.Metadata.Id, StringComparer.Ordinal);
        var publications = graph.Publications
            .Select(publication =>
            {
                var storageBindingId = publication.StorageBindingId;
                if (storageBindingId is null &&
                    resourcesById.TryGetValue(publication.ResourceId, out var resource))
                {
                    storageBindingId = resource.PrimaryStorageBindingId;
                }

                if (storageBindingId is null || !affectedBindingIds.Contains(storageBindingId))
                {
                    return publication;
                }

                changed = true;
                return publication with { Status = SetLifecycle(publication.Status, lifecycle) };
            })
            .ToArray();
        if (!changed)
        {
            return graph;
        }

        return graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            StorageBindings = storageBindings,
            Resources = resources,
            Publications = publications,
        };
    }

    private static MetadataV2Service? ResolveUniquePublishedFeatureService(
        MetadataV2Graph graph,
        string serviceName,
        int? layerId)
    {
        var matchingServices = graph.Services
            .Where(candidate =>
                string.Equals(candidate.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Metadata.Id, serviceName, StringComparison.Ordinal))
            .ToArray();
        var exactIdService = matchingServices.FirstOrDefault(candidate =>
            string.Equals(candidate.Metadata.Id, serviceName, StringComparison.Ordinal));
        if (exactIdService is not null)
        {
            if (!IsFeatureServerService(graph, exactIdService) ||
                !MetadataV2ServiceProtocols.IsProtocolEnabled(
                    exactIdService,
                    MetadataV2ServiceProtocols.FeatureServer))
            {
                throw new LayerPublishingException(
                    LayerPublishingErrorKind.Conflict,
                    $"Service '{serviceName}' does not resolve to one unique Esri FeatureServer service.",
                    layerId);
            }

            return exactIdService;
        }

        var matchingFeatureServices = matchingServices
            .Where(candidate => IsFeatureServerService(graph, candidate))
            .ToArray();

        var protocolEnabledServices = matchingFeatureServices
            .Where(candidate => MetadataV2ServiceProtocols.IsProtocolEnabled(
                candidate,
                MetadataV2ServiceProtocols.FeatureServer))
            .ToArray();
        // Protocol-disabled compatibility candidates can retain FeatureServer-shaped
        // publications. Exclude them from name-based resolution even when none remain,
        // so graph mutation and governance hydration match runtime routing.
        matchingFeatureServices = protocolEnabledServices;

        MetadataV2Service? service = null;
        if (matchingFeatureServices.Length > 1)
        {
            var activeResourceIds = graph.Resources
                .Where(resource => resource.Status.Lifecycle != MetadataV2LifecycleStatus.Retired)
                .Select(resource => resource.Metadata.Id)
                .ToHashSet(StringComparer.Ordinal);
            var preferredFeatureServices = matchingFeatureServices
                .Where(candidate => graph.Publications.Any(publication =>
                    string.Equals(publication.ServiceId, candidate.Metadata.Id, StringComparison.Ordinal) &&
                    publication.Status.Lifecycle != MetadataV2LifecycleStatus.Retired &&
                    MetadataV2ServiceProtocols.IsPreferredPublicationType(
                        MetadataV2ServiceProtocols.FeatureServer,
                        publication.PublicationType) &&
                    activeResourceIds.Contains(publication.ResourceId)))
                .ToArray();
            service = preferredFeatureServices.Length == 1 ? preferredFeatureServices[0] : null;
        }

        if (service is null && matchingFeatureServices.Length > 1)
        {
            var publishedFeatureServices = matchingFeatureServices
                .Where(candidate => graph.Publications.Any(publication =>
                    string.Equals(publication.ServiceId, candidate.Metadata.Id, StringComparison.Ordinal) &&
                    MetadataV2ServiceProtocols.IsPreferredPublicationType(
                        MetadataV2ServiceProtocols.FeatureServer,
                        publication.PublicationType)))
                .ToArray();
            service = publishedFeatureServices.Length == 1 ? publishedFeatureServices[0] : null;
        }

        service ??= matchingFeatureServices.Length == 1 ? matchingFeatureServices[0] : null;
        if (service is null && matchingServices.Length > 0)
        {
            throw new LayerPublishingException(
                LayerPublishingErrorKind.Conflict,
                $"Service '{serviceName}' does not resolve to one unique Esri FeatureServer service.",
                layerId);
        }

        return service;
    }

    private static bool IsFeatureServerService(MetadataV2Graph graph, MetadataV2Service service)
        => service.ServiceType == MetadataV2ServiceType.EsriFeatureService ||
           service.Protocols.Contains(MetadataV2ServiceProtocols.FeatureServer, StringComparer.OrdinalIgnoreCase) ||
           graph.Publications.Any(publication =>
               string.Equals(publication.ServiceId, service.Metadata.Id, StringComparison.Ordinal) &&
               publication.PublicationType == MetadataV2PublicationType.EsriFeatureLayer);

    // Loads the active Metadata v2 graph for mutation, tolerating a fresh-DB
    // container where no snapshot has been activated yet (e.g. migration 031 ran
    // but the compat/bootstrap compile has not). In that case we start from an
    // empty graph and force the first write (null expectedEtag) instead of 500ing
    // the admin layer-publish path. (honua-server#1341.)
    //
    // This deliberately reads only the persisted snapshot via the write-base reader,
    // NOT GetCurrentAsync. GetCurrentAsync synthesizes a graph from the V1 catalog when
    // no snapshot is activated (honua-server#1412); building a publish on top of that
    // synthesized-but-never-persisted base makes SaveAsync's reconciliation fail, so
    // every AutoPublish import would report "publishing did not complete".
    private async Task<(MetadataV2Graph Graph, string? ExpectedEtag)> LoadCurrentOrEmptyGraphAsync(
        CancellationToken cancellationToken)
    {
        if (_metadataWriteBaseReader is not null)
        {
            var persisted = await _metadataWriteBaseReader
                .TryGetPersistedCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            return persisted is null
                ? (new MetadataV2Graph(), null)
                : (persisted.Graph, persisted.Etag);
        }

        // Test doubles that do not implement the write-base seam: preserve the legacy
        // throw-on-missing behavior (start empty + force first write).
        try
        {
            var snapshot = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            return (snapshot.Graph, snapshot.Etag);
        }
        catch (InvalidOperationException)
        {
            return (new MetadataV2Graph(), null);
        }
    }

    private async Task SyncRefreshedExtentsIntoV2GraphAsync(
        Dictionary<int, LayerExtentInsert?> refreshedExtents,
        CancellationToken cancellationToken)
    {
        if (refreshedExtents.Count == 0)
        {
            return;
        }

        // Extent refresh mutates the current graph and saves it, so it must read the
        // persisted snapshot only (same rationale as LoadCurrentOrEmptyGraphAsync): a
        // synthesized V1-compat graph is never persisted and would fail SaveAsync.
        // When no snapshot is activated there is nothing to update — extents are served
        // straight from the V1 catalog — so skip. (honua-server#1412.)
        MetadataV2GraphSnapshot? snapshot;
        if (_metadataWriteBaseReader is not null)
        {
            snapshot = await _metadataWriteBaseReader
                .TryGetPersistedCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return;
            }
        }
        else
        {
            snapshot = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        }

        var graph = snapshot.Graph;

        // Map layer_id -> resource ids (a layer may be published into multiple services).
        var affectedResourceIds = new HashSet<string>(StringComparer.Ordinal);
        var extentByResourceId = new Dictionary<string, LayerExtentInsert?>(StringComparer.Ordinal);
        var publicationIndex = 0;
        while (publicationIndex < graph.Publications.Count)
        {
            var publication = graph.Publications[publicationIndex];
            if (publication.LayerIndex is { } layerIndex &&
                refreshedExtents.TryGetValue(layerIndex, out var extent) &&
                affectedResourceIds.Add(publication.ResourceId))
            {
                extentByResourceId[publication.ResourceId] = extent;
            }

            publicationIndex++;
        }
        if (affectedResourceIds.Count == 0)
        {
            return;
        }

        var updatedResources = graph.Resources
            .Select(resource =>
            {
                if (!affectedResourceIds.Contains(resource.Metadata.Id))
                {
                    return resource;
                }

                var extent = extentByResourceId[resource.Metadata.Id];
                MetadataV2Bbox? bbox = extent is null
                    ? null
                    : new MetadataV2Bbox
                    {
                        West = extent.MinX,
                        South = extent.MinY,
                        East = extent.MaxX,
                        North = extent.MaxY,
                    };

                var spatial = (resource.Spatial ?? new MetadataV2ResourceSpatial()) with { Bbox = bbox };
                return resource with { Spatial = spatial };
            })
            .ToArray();

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = updatedResources,
        };

        await _metadataGraphStore.SaveAsync(updatedGraph, snapshot.Etag, cancellationToken).ConfigureAwait(false);
    }

    private static MetadataV2Service BuildPublishedService(
        MetadataV2Graph graph,
        string serviceName,
        int srid,
        DateTimeOffset now)
    {
        var existing = graph.Services.FirstOrDefault(service =>
            string.Equals(service.Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(service.Metadata.Id, serviceName, StringComparison.Ordinal));

        // Hoist to a local so every access below narrows through the same null check
        // instead of repeating `existing?.Metadata` (which CodeQL cannot correlate with
        // the later null-forgiving access to `existing!.Metadata`).
        var existingMetadata = existing?.Metadata;
        var serviceId = existingMetadata?.Id ?? $"svc-publish-{SanitizeMetadataId(serviceName)}";
        var metadata = (existingMetadata ?? new MetadataV2ObjectMetadata()) with
        {
            Id = serviceId,
            Name = existingMetadata is not null && !string.IsNullOrWhiteSpace(existingMetadata.Name)
                ? existingMetadata.Name
                : serviceName,
            Title = existingMetadata?.Title ?? serviceName,
            Description = existingMetadata?.Description ?? $"Honua service '{serviceName}'",
            CreatedAt = existingMetadata?.CreatedAt ?? now,
            UpdatedAt = now
        };

        return (existing ?? new MetadataV2Service()) with
        {
            Metadata = metadata,
            ServiceType = MetadataV2ServiceType.EsriFeatureService,
            Route = $"/rest/services/{serviceName}/FeatureServer",
            Protocols = MetadataV2ServiceProtocols.All,
            SpatialReference = CreateSpatialReference(srid),
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Resource BuildPublishedResource(
        LayerPublishRequest request,
        int layerId,
        string primaryKeyColumn,
        string geometryColumn,
        string geometryType,
        int srid,
        int storageSrid,
        IReadOnlyList<LayerFieldInsert> fields,
        LayerExtentInsert? extent,
        DateTimeOffset now)
    {
        var bindingId = BuildStorageBindingId(layerId);
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildResourceId(layerId),
                Name = request.LayerName.Trim(),
                Title = request.LayerName.Trim(),
                Description = request.Description,
                License = request.SourceGovernance?.License,
                Attribution = request.SourceGovernance?.Attribution,
                Publisher = request.SourceGovernance?.Publisher,
                Links = request.SourceGovernance?.ToMetadataLinks() ?? [],
                CreatedAt = now,
                UpdatedAt = now
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            PrimaryStorageBindingId = bindingId,
            SchemaFields = fields
                .Select(field => MapLayerFieldToMetadataV2(field, primaryKeyColumn, geometryColumn))
                .ToArray(),
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = CreateSpatialReference(srid),
                GeometryType = MapMetadataV2GeometryType(geometryType),
                Bbox = extent is not null && extent.Srid == srid
                    ? new MetadataV2Bbox
                    {
                        West = extent.MinX,
                        South = extent.MinY,
                        East = extent.MaxX,
                        North = extent.MaxY
                    }
                    : null,
                PrimaryGeometryField = geometryColumn,
                StorageCrs = CreateSpatialReference(storageSrid)
            },
            Display = new MetadataV2ResourceDisplay
            {
                DisplayField = fields
                    .FirstOrDefault(field =>
                        field.Type != MetadataV2FieldType.Geometry &&
                        !string.Equals(field.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
                    ?.Name,
                Queryable = true,
                DefaultVisibility = request.Enabled
            },
            // Carry the captured Esri subtypes into the canonical graph so they survive
            // the compat-compile snapshot and are served on the FeatureServer layer
            // metadata (subtypeField / subtypes / defaultSubtypeCode) (honua-server#1378).
            Subtypes = ResolveSubtypesForPublish(request.Subtypes, fields),
            // Carry the captured Esri attribute rules into the canonical graph so they
            // fire on the shared edit path (FeatureServer applyEdits). Calculation rules
            // whose target column was not published are dropped (honua-server#1271).
            AttributeRules = ResolveAttributeRulesForPublish(request.AttributeRules, fields),
            Status = LayerReadyStatus(request.Enabled, now)
        };
    }

    // Attaches captured attribute rules, dropping calculation rules whose target field
    // was not published. A calculation rule pointing at a column the layer did not publish
    // could never be applied and would fail graph validation, so it is omitted rather than
    // persisted against a missing field. Constraint/validation rules are carried as-is;
    // their expressions are evaluated against the published attribute set at edit time and
    // unsupported expressions are routed out of scope, so a stale field reference simply
    // skips rather than breaking the edit.
    private static MetadataV2AttributeRule[] ResolveAttributeRulesForPublish(
        IReadOnlyList<MetadataV2AttributeRule>? attributeRules,
        IReadOnlyList<LayerFieldInsert> fields)
    {
        if (attributeRules is null || attributeRules.Count == 0)
        {
            return [];
        }

        var publishedNames = new HashSet<string>(
            fields.Select(field => field.Name),
            StringComparer.OrdinalIgnoreCase);

        return attributeRules
            .Where(rule => rule.Type != MetadataV2AttributeRuleType.Calculation
                || (!string.IsNullOrWhiteSpace(rule.FieldName) && publishedNames.Contains(rule.FieldName!)))
            .ToArray();
    }

    // Attaches the captured subtype set only when its subtype field was actually
    // published as a column on the layer. A subtype set referencing a column that was
    // not published (e.g. dropped during selection) is omitted rather than persisted
    // against a missing field, which would fail graph validation and false-fail later
    // reconciliation.
    private static MetadataV2Subtypes? ResolveSubtypesForPublish(
        MetadataV2Subtypes? subtypes,
        IReadOnlyList<LayerFieldInsert> fields)
    {
        if (subtypes is null || string.IsNullOrWhiteSpace(subtypes.SubtypeField))
        {
            return null;
        }

        var publishedField = fields.FirstOrDefault(field =>
            string.Equals(field.Name, subtypes.SubtypeField, StringComparison.OrdinalIgnoreCase));
        if (publishedField is null)
        {
            return null;
        }

        // Drop per-subtype overrides that reference columns the layer did not publish so
        // graph validation (which requires every override field be declared) passes.
        var publishedNames = new HashSet<string>(
            fields.Select(field => field.Name),
            StringComparer.OrdinalIgnoreCase);

        var prunedSubtypes = subtypes.Subtypes
            .Select(subtype =>
            {
                if (subtype.FieldOverrides.Count == 0)
                {
                    return subtype;
                }

                var keptOverrides = subtype.FieldOverrides
                    .Where(pair => publishedNames.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

                return keptOverrides.Count == subtype.FieldOverrides.Count
                    ? subtype
                    : subtype with { FieldOverrides = keptOverrides };
            })
            .ToArray();

        return subtypes with { Subtypes = prunedSubtypes };
    }

    private MetadataV2StorageBinding BuildPublishedStorageBinding(
        LayerPublishRequest request,
        int layerId,
        string resourceId,
        string schema,
        string table,
        string primaryKeyColumn,
        string geometryColumn,
        int storageSrid,
        DateTimeOffset now)
    {
        var connectionId = request.ConnectionId?.ToString("D");
        var options = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [FeatureStorageMapping.SourceBackedOption] = BoolOption(true),
            ["schemaName"] = StringOption(schema),
            ["tableName"] = StringOption(table),
            ["primaryKeyColumn"] = StringOption(primaryKeyColumn),
            ["geometryColumn"] = StringOption(geometryColumn),
            ["storageSrid"] = IntOption(storageSrid)
        };

        // Layers published onto the shared Honua 'features' table store their non-key
        // attributes as keys inside the 'features.attributes' JSONB column (not as
        // physical columns) and share the table across layers via the 'layer_id'
        // discriminator column. Declare both so the storage-mapped reader projects
        // attributes->>'field' instead of bare columns (Postgres 42703) AND constrains
        // reads to this layer's rows (WHERE layer_id = StorageLayerId) — without the
        // discriminator a query for layer A would return layer B's features.
        //
        // Gate on the ACTUAL shared table: name == 'features' AND schema == the Honua
        // metadata schema. A user source table that merely happens to be named
        // 'features' in another schema (e.g. public.features) has neither the JSONB
        // 'attributes' column nor 'layer_id', so applying these options there would make
        // the reader emit columns the table lacks and fail with 42703. (honua-server#1238.)
        if (string.Equals(table, DatabaseSchema.FeaturesTable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(schema, _metadataSchema, StringComparison.OrdinalIgnoreCase))
        {
            options["attributesColumn"] = StringOption("attributes");
            options["layerDiscriminatorColumn"] = StringOption(DatabaseSchema.LayerIdColumn);
        }

        return new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = BuildStorageBindingId(layerId),
                Name = BuildStorageBindingId(layerId),
                Title = $"{schema}.{table}",
                CreatedAt = now,
                UpdatedAt = now
            },
            ResourceId = resourceId,
            ConnectionId = connectionId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = $"{schema}.{table}",
            StorageLayerId = layerId,
            Capabilities =
            [
                MetadataV2StorageBindingCapability.Query,
                MetadataV2StorageBindingCapability.Filter,
                MetadataV2StorageBindingCapability.Sort,
                MetadataV2StorageBindingCapability.Aggregate
            ],
            Options = options,
            Status = LayerReadyStatus(request.Enabled, now)
        };
    }

    private static MetadataV2Publication BuildPublishedPublication(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2StorageBinding binding,
        string layerIdText,
        string layerTitle,
        MetadataV2PublicationType publicationType,
        bool isPrimary,
        string idPrefix,
        bool enabled,
        DateTimeOffset now)
    {
        return new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"{idPrefix}-{service.Metadata.Id}-{layerIdText}",
                Name = layerIdText,
                Title = layerTitle,
                CreatedAt = now,
                UpdatedAt = now
            },
            ResourceId = resource.Metadata.Id,
            ServiceId = service.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            PublicationType = publicationType,
            Identifier = new MetadataV2PublicationIdentifier
            {
                Value = layerIdText,
                IsNumeric = true
            },
            IsPrimary = isPrimary,
            SupportedFormats = _defaultFormats,
            Capabilities = _defaultCapabilities,
            Status = LayerReadyStatus(enabled, now)
        };
    }

    private static MetadataV2Connection? BuildPublishedConnection(Guid? connectionId, DateTimeOffset now)
    {
        if (!connectionId.HasValue)
        {
            return null;
        }

        var id = connectionId.Value.ToString("D");
        return new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = id,
                Name = id,
                Title = "PostGIS secure connection",
                CreatedAt = now,
                UpdatedAt = now
            },
            Type = MetadataV2ConnectionType.Database,
            Provider = DataProviderNames.Postgis,
            Status = ActiveReadyStatus(now)
        };
    }

    private static MetadataV2Field MapLayerFieldToMetadataV2(
        LayerFieldInsert field,
        string primaryKeyColumn,
        string geometryColumn)
    {
        var semanticRoles = new List<string>(capacity: 2);
        if (string.Equals(field.Name, primaryKeyColumn, StringComparison.OrdinalIgnoreCase))
        {
            semanticRoles.Add("id.primary");
        }
        if (string.Equals(field.Name, geometryColumn, StringComparison.OrdinalIgnoreCase))
        {
            semanticRoles.Add("geometry.primary");
        }

        return new MetadataV2Field
        {
            Name = field.Name,
            Type = MapMetadataV2FieldType(field.Type),
            Title = field.Name,
            Description = field.Description,
            Nullable = field.Nullable,
            SemanticRoles = semanticRoles.ToArray(),
            Alias = field.Name,
            Editable = field.Type != MetadataV2FieldType.Geometry,
            Length = field.MaxLength,
            // Carry the captured Esri coded-value/range domain into the canonical
            // graph so it survives the compat-compile snapshot and is served via the
            // FeatureServer field domain and queryDomains surfaces (honua-server#1255).
            Domain = field.Domain
        };
    }

    private static MetadataV2FieldType MapMetadataV2FieldType(MetadataV2FieldType fieldType)
        => fieldType switch
        {
            MetadataV2FieldType.String => MetadataV2FieldType.String,
            MetadataV2FieldType.Integer => MetadataV2FieldType.Integer,
            MetadataV2FieldType.BigInteger => MetadataV2FieldType.BigInteger,
            MetadataV2FieldType.Double => MetadataV2FieldType.Double,
            MetadataV2FieldType.Float => MetadataV2FieldType.Float,
            MetadataV2FieldType.Boolean => MetadataV2FieldType.Boolean,
            MetadataV2FieldType.DateTime => MetadataV2FieldType.DateTime,
            MetadataV2FieldType.Date => MetadataV2FieldType.Date,
            MetadataV2FieldType.Time => MetadataV2FieldType.Time,
            MetadataV2FieldType.Geometry => MetadataV2FieldType.Geometry,
            MetadataV2FieldType.Json => MetadataV2FieldType.Json,
            MetadataV2FieldType.Binary => MetadataV2FieldType.Binary,
            MetadataV2FieldType.Uuid => MetadataV2FieldType.Uuid,
            _ => MetadataV2FieldType.Unknown
        };

    private static MetadataV2GeometryType MapMetadataV2GeometryType(string geometryType)
        => geometryType.Trim().ToLowerInvariant() switch
        {
            "point" => MetadataV2GeometryType.Point,
            "multipoint" => MetadataV2GeometryType.MultiPoint,
            "linestring" => MetadataV2GeometryType.LineString,
            "multilinestring" => MetadataV2GeometryType.MultiLineString,
            "polygon" => MetadataV2GeometryType.Polygon,
            "multipolygon" => MetadataV2GeometryType.MultiPolygon,
            "geometrycollection" => MetadataV2GeometryType.GeometryCollection,
            _ => MetadataV2GeometryType.Mixed
        };

    private static MetadataV2SpatialReference CreateSpatialReference(int srid)
        => new()
        {
            Srid = srid,
            Crs = $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}",
            IsGeographic = srid == 4326
        };

    private static MetadataV2Status ActiveReadyStatus(DateTimeOffset now)
        => LayerReadyStatus(enabled: true, now);

    private static MetadataV2Status LayerReadyStatus(bool enabled, DateTimeOffset now)
        => new()
        {
            Lifecycle = enabled
                ? MetadataV2LifecycleStatus.Active
                : MetadataV2LifecycleStatus.Retired,
            State = MetadataV2OperationalState.Ready,
            ObservedAt = now
        };

    private static MetadataV2LifecycleStatus DeriveResourceLifecycleFromBindings(
        IEnumerable<MetadataV2StorageBinding> bindings,
        string resourceId)
    {
        var lifecycles = bindings
            .Where(binding => string.Equals(binding.ResourceId, resourceId, StringComparison.Ordinal))
            .Select(binding => binding.Status.Lifecycle)
            .ToHashSet();
        if (lifecycles.Contains(MetadataV2LifecycleStatus.Active))
        {
            return MetadataV2LifecycleStatus.Active;
        }
        if (lifecycles.Contains(MetadataV2LifecycleStatus.Deprecated))
        {
            return MetadataV2LifecycleStatus.Deprecated;
        }
        if (lifecycles.Contains(MetadataV2LifecycleStatus.Draft))
        {
            return MetadataV2LifecycleStatus.Draft;
        }
        if (lifecycles.Contains(MetadataV2LifecycleStatus.Archived))
        {
            return MetadataV2LifecycleStatus.Archived;
        }

        return MetadataV2LifecycleStatus.Retired;
    }

    private static List<T> UpsertById<T>(
        IReadOnlyList<T> items,
        T item,
        Func<T, string> idSelector)
    {
        var itemId = idSelector(item);
        var result = new List<T>(items.Count + 1);
        var replaced = false;

        foreach (var existing in items)
        {
            if (string.Equals(idSelector(existing), itemId, StringComparison.Ordinal))
            {
                if (!replaced)
                {
                    result.Add(item);
                    replaced = true;
                }
                continue;
            }

            result.Add(existing);
        }

        if (!replaced)
        {
            result.Add(item);
        }

        return result;
    }

    private static List<MetadataV2Publication> UpsertPublication(
        IReadOnlyList<MetadataV2Publication> publications,
        MetadataV2Publication publication)
    {
        var result = new List<MetadataV2Publication>(publications.Count + 1);
        var replaced = false;

        foreach (var existing in publications)
        {
            var sameIdentity = string.Equals(
                existing.Metadata.Id,
                publication.Metadata.Id,
                StringComparison.Ordinal);
            var sameServiceLayer = string.Equals(
                existing.ServiceId,
                publication.ServiceId,
                StringComparison.Ordinal) &&
                existing.LayerIndex == publication.LayerIndex &&
                existing.PublicationType == publication.PublicationType;

            if (sameIdentity || sameServiceLayer)
            {
                if (!replaced)
                {
                    result.Add(publication);
                    replaced = true;
                }
                continue;
            }

            result.Add(existing);
        }

        if (!replaced)
        {
            result.Add(publication);
        }

        return result;
    }

    // Builds the Type=Style graph resources for a layer's associated catalog styles
    // and the ordered StyleResourceIds the data resource should reference. Returns
    // empty when the style catalog is unavailable or the layer has no associations
    // (e.g. a freshly published, not-yet-styled layer), so the publish path degrades
    // gracefully and the data resource simply carries no StyleResourceIds.
    private async Task<(IReadOnlyList<MetadataV2Resource> Styles, IReadOnlyList<string> StyleResourceIds)>
        BuildStyleResourcesForLayerAsync(int layerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_styleCatalog is null)
        {
            return (Array.Empty<MetadataV2Resource>(), Array.Empty<string>());
        }

        var styles = await _styleCatalog.GetStylesForLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (styles.Count == 0)
        {
            return (Array.Empty<MetadataV2Resource>(), Array.Empty<string>());
        }

        var resources = new List<MetadataV2Resource>(styles.Count);
        var ids = new List<string>(styles.Count);
        foreach (var style in styles)
        {
            var resourceId = MetadataV2StyleResourceFactory.BuildStyleResourceId(style.StyleId);
            resources.Add(MetadataV2StyleResourceFactory.BuildStyleResource(
                style.StyleId,
                style.MapLibreStyleJson,
                style.Title,
                style.Description,
                style.DrawingInfoJson,
                style.StyleVersion,
                style.CreatedAt == default ? now : style.CreatedAt,
                style.UpdatedAt == default ? now : style.UpdatedAt));
            ids.Add(resourceId);
        }

        return (resources, ids);
    }

    private static string BuildResourceId(int layerId)
        => $"res-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildStorageBindingId(int layerId)
        => $"binding-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";

    private static JsonElement BoolOption(bool value)
        => JsonSerializer.SerializeToElement(value, LayerPublishingStorageOptionJsonContext.Default.Boolean);

    private static JsonElement IntOption(int value)
        => JsonSerializer.SerializeToElement(value, LayerPublishingStorageOptionJsonContext.Default.Int32);

    private static JsonElement StringOption(string value)
        => JsonSerializer.SerializeToElement(value, LayerPublishingStorageOptionJsonContext.Default.String);

    private static string SanitizeMetadataId(string value)
    {
        var trimmed = value.Trim();
        var chars = trimmed.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.'
                ? ch
                : '-');
        var sanitized = new string(chars.ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }
}
