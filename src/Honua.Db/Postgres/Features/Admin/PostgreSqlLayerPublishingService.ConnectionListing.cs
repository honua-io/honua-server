// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService
{
    // Apply ownership before LIMIT and COUNT. Canonical per-layer bindings take
    // precedence over the legacy service-level connection, which cannot represent
    // a service containing both managed and source-backed layers.
    private string BuildPublishedLayerConnectionPredicate() => $"""
        (@connectionId IS NULL
         OR l.layer_id = ANY(@matchingLayerIds)
         OR (NOT (l.layer_id = ANY(@canonicalLayerIds)) AND EXISTS (
             SELECT 1 FROM {ServicesTable} s
             WHERE s.service_name = sl.service_name
               AND (s.connection_id = @connectionId
                    OR (s.connection_id IS NULL AND @includeManaged)))))
        """;

    private readonly record struct ConnectionLayerScope(
        string ServiceName, Guid? ConnectionId, Guid? ManagedConnectionId);

    private async Task AddPublishedLayerConnectionParametersAsync(
        NpgsqlCommand command, ConnectionLayerScope scope, CancellationToken cancellationToken)
    {
        var connectionId = scope.ConnectionId;
        var canonicalIds = new HashSet<int>();
        var matchingIds = new HashSet<int>();
        var includeManaged = connectionId.HasValue && connectionId == scope.ManagedConnectionId;
        if (connectionId.HasValue)
        {
            var (graph, _) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
            var service = ResolveUniquePublishedFeatureService(graph, scope.ServiceName, layerId: null);
            var snapshot = new MetadataV2GraphSnapshot(graph, "\"connection-listing\"", DateTimeOffset.UtcNow);
            foreach (var publication in graph.Publications.Where(publication =>
                         service is not null && publication.ServiceId == service.Metadata.Id &&
                         publication.PublicationType == MetadataV2LayerType.EsriFeatureLayer))
            {
                var binding = snapshot.ResolveStorageBinding(publication);
                if (binding?.StorageLayerId is not { } layerId)
                {
                    continue;
                }

                canonicalIds.Add(layerId);
                if (string.Equals(binding.ConnectionId, connectionId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                    (binding.ConnectionId is null && includeManaged))
                {
                    matchingIds.Add(layerId);
                }
            }
        }

        AddConnectionScopeParameters(command, scope, new LayerConnectionIds(canonicalIds, matchingIds));
    }

    private readonly record struct LayerConnectionIds(HashSet<int> Canonical, HashSet<int> Matching);

    private static void AddConnectionScopeParameters(
        NpgsqlCommand command, ConnectionLayerScope scope, LayerConnectionIds ids)
    {
        command.Parameters.Add(new NpgsqlParameter("@connectionId", NpgsqlDbType.Uuid)
        {
            Value = scope.ConnectionId is { } connectionId ? connectionId : DBNull.Value
        });
        command.Parameters.AddWithValue("@canonicalLayerIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, ids.Canonical.ToArray());
        command.Parameters.AddWithValue("@matchingLayerIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, ids.Matching.ToArray());
        command.Parameters.AddWithValue("@includeManaged", scope.ConnectionId.HasValue && scope.ConnectionId == scope.ManagedConnectionId);
    }
}
