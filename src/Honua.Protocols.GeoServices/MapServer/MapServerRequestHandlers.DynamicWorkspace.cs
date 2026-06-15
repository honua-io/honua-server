// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// A candidate published layer the dynamicLayers source resolver can map a <c>dataLayer</c>
    /// source onto. Decouples the resolver from the per-operation descriptor records.
    /// </summary>
    private readonly record struct DynamicLayerCandidate(int PublicLayerId, MetadataV2Resource Resource);

    /// <summary>
    /// Builds a <see cref="DynamicLayerSourceResolver"/> for the current request, reading the
    /// MapServer dynamicLayers workspace allowlist options from the request services.
    /// </summary>
    private static DynamicLayerSourceResolver CreateDynamicLayerSourceResolver(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        IEnumerable<DynamicLayerCandidate> candidates)
    {
        var options = context.RequestServices
            .GetRequiredService<IOptions<MapServerDynamicLayersOptions>>().Value;
        return new DynamicLayerSourceResolver(snapshot, candidates, options);
    }

    /// <summary>
    /// Resolution context shared by every MapServer operation that accepts <c>dynamicLayers</c>
    /// (export, identify, find, legend) and the dynamicLayer child resource. It translates an Esri
    /// dynamic-layer <c>source</c> into a published map-layer id, supporting both
    /// <c>source.type=mapLayer</c> (reference an existing published layer) and
    /// <c>source.type=dataLayer</c> (reference a registered workspace + table that is already
    /// materialized by a published resource).
    /// </summary>
    private sealed class DynamicLayerSourceResolver
    {
        private readonly MetadataV2GraphSnapshot _snapshot;
        private readonly IReadOnlyList<DynamicLayerCandidate> _candidates;
        private readonly HashSet<int> _knownLayerIds;
        private readonly MapServerDynamicLayersOptions _options;

        public DynamicLayerSourceResolver(
            MetadataV2GraphSnapshot snapshot,
            IEnumerable<DynamicLayerCandidate> candidates,
            MapServerDynamicLayersOptions options)
        {
            _snapshot = snapshot;
            _candidates = candidates as IReadOnlyList<DynamicLayerCandidate> ?? candidates.ToArray();
            _knownLayerIds = _candidates.Select(static candidate => candidate.PublicLayerId).ToHashSet();
            _options = options;
        }

        /// <summary>
        /// Resolves a dynamic-layer <c>source</c> element to the published map-layer id it targets.
        /// </summary>
        /// <param name="sourceElement">The parsed <c>source</c> JSON object.</param>
        /// <param name="contextLabel">
        /// A short label used to build error messages, e.g. <c>"dynamicLayers entry '5'"</c> or
        /// <c>"dynamicLayer"</c>.
        /// </param>
        /// <param name="mapLayerId">The resolved published map-layer id when successful.</param>
        /// <param name="error">A client-safe validation message when resolution fails.</param>
        /// <returns><see langword="true"/> when the source resolves to a published layer.</returns>
        public bool TryResolveMapLayerId(
            JsonElement sourceElement,
            string contextLabel,
            out int mapLayerId,
            out string? error)
        {
            mapLayerId = 0;
            error = null;

            if (!TryGetJsonString(sourceElement, "type", out var sourceType) ||
                string.IsNullOrWhiteSpace(sourceType))
            {
                error = $"{contextLabel} must include a source type.";
                return false;
            }

            if (string.Equals(sourceType, "mapLayer", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetJsonInt(sourceElement, "mapLayerId", out mapLayerId))
                {
                    error = $"{contextLabel} must include a mapLayerId.";
                    return false;
                }

                if (!_knownLayerIds.Contains(mapLayerId))
                {
                    error = $"{contextLabel} references unknown layer '{mapLayerId}'.";
                    return false;
                }

                return true;
            }

            if (string.Equals(sourceType, "dataLayer", StringComparison.OrdinalIgnoreCase))
            {
                return TryResolveDataLayer(sourceElement, contextLabel, out mapLayerId, out error);
            }

            // queryTable sources (raw SQL) and join sources remain deferred; reject explicitly so a
            // client never silently falls through to a different layer.
            error = $"{contextLabel} uses an unsupported source type '{sourceType}'. " +
                "Supported types are 'mapLayer' and 'dataLayer'.";
            return false;
        }

        private bool TryResolveDataLayer(
            JsonElement sourceElement,
            string contextLabel,
            out int mapLayerId,
            out string? error)
        {
            mapLayerId = 0;
            error = null;

            if (!_options.WorkspaceLayersEnabled)
            {
                error = $"{contextLabel} uses a dataLayer source, which is not enabled on this server.";
                return false;
            }

            if (!sourceElement.TryGetProperty("dataSource", out var dataSource) ||
                dataSource.ValueKind != JsonValueKind.Object)
            {
                error = $"{contextLabel} dataLayer source must include a dataSource object.";
                return false;
            }

            // Only plain table data sources are supported. Joins and dynamic query tables (raw SQL)
            // are intentionally rejected — see the parity matrix re-deferral note (#1660).
            if (TryGetJsonString(dataSource, "type", out var dataSourceType) &&
                !string.IsNullOrWhiteSpace(dataSourceType) &&
                !string.Equals(dataSourceType, "table", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{contextLabel} dataLayer dataSource type '{dataSourceType}' is not supported. " +
                    "Only 'table' data sources are supported; joins and query tables are not.";
                return false;
            }

            if (!TryGetJsonString(dataSource, "workspaceId", out var workspaceId) ||
                string.IsNullOrWhiteSpace(workspaceId))
            {
                error = $"{contextLabel} dataLayer dataSource must include a workspaceId.";
                return false;
            }

            if (!IsWorkspaceAllowed(workspaceId))
            {
                // Do not disclose whether the workspace exists; treat unknown and non-allowlisted
                // workspaces identically.
                error = $"{contextLabel} references a workspace that is not available.";
                return false;
            }

            if (!TryGetJsonString(dataSource, "dataSourceName", out var dataSourceName) ||
                string.IsNullOrWhiteSpace(dataSourceName))
            {
                error = $"{contextLabel} dataLayer dataSource must include a dataSourceName.";
                return false;
            }

            var requestedSchema = NormalizeIdentifier(
                ReadOptionalString(dataSource, "schema") ?? ReadOptionalString(dataSource, "schemaName"));
            var (parsedSchema, parsedTable) = SplitQualifiedName(dataSourceName);
            requestedSchema ??= parsedSchema;
            var requestedTable = NormalizeIdentifier(parsedTable);

            if (string.IsNullOrWhiteSpace(requestedTable))
            {
                error = $"{contextLabel} dataLayer dataSourceName is invalid.";
                return false;
            }

            if (!TryFindPublishedLayerForWorkspaceTable(
                    workspaceId,
                    requestedSchema,
                    requestedTable,
                    out mapLayerId))
            {
                // The table is not materialized by a published resource on this workspace. Reject
                // rather than performing ad-hoc table access.
                error = $"{contextLabel} references a table that is not published on this workspace.";
                return false;
            }

            return true;
        }

        private bool IsWorkspaceAllowed(string workspaceId)
        {
            if (_options.AllowAllRegisteredWorkspaces)
            {
                return true;
            }

            foreach (var allowed in _options.AllowedWorkspaceIds)
            {
                if (string.Equals(allowed, workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryFindPublishedLayerForWorkspaceTable(
            string workspaceId,
            string? requestedSchema,
            string requestedTable,
            out int mapLayerId)
        {
            mapLayerId = 0;
            foreach (var candidate in _candidates)
            {
                var binding = ResolveResourceBinding(candidate.Resource);
                if (binding is null ||
                    binding.StorageType != MetadataV2StorageType.RelationalTable ||
                    !string.Equals(binding.ConnectionId, workspaceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (bindingSchema, bindingTable) = ResolveBindingTable(binding);
                if (string.IsNullOrWhiteSpace(bindingTable) ||
                    !string.Equals(bindingTable, requestedTable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (requestedSchema is not null &&
                    !string.IsNullOrWhiteSpace(bindingSchema) &&
                    !string.Equals(bindingSchema, requestedSchema, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mapLayerId = candidate.PublicLayerId;
                return true;
            }

            return false;
        }

        private MetadataV2StorageBinding? ResolveResourceBinding(MetadataV2Resource resource)
        {
            if (!string.IsNullOrEmpty(resource.PrimaryStorageBindingId) &&
                _snapshot.Index.StorageBindingsById.TryGetValue(resource.PrimaryStorageBindingId, out var primary))
            {
                return primary;
            }

            return _snapshot.Index.StorageBindingsByResource[resource.Metadata.Id].FirstOrDefault();
        }

        private static (string? Schema, string? Table) ResolveBindingTable(MetadataV2StorageBinding binding)
        {
            var schema = NormalizeIdentifier(
                ReadOptionalString(binding.Options, "schemaName") ?? ReadOptionalString(binding.Options, "schema"));
            var table = NormalizeIdentifier(
                ReadOptionalString(binding.Options, "tableName") ?? ReadOptionalString(binding.Options, "table"));

            if (string.IsNullOrWhiteSpace(table))
            {
                var (locatorSchema, locatorTable) = SplitQualifiedName(binding.Locator);
                schema ??= locatorSchema;
                table = NormalizeIdentifier(locatorTable);
            }

            return (schema, table);
        }

        private static (string? Schema, string? Table) SplitQualifiedName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return (null, null);
            }

            var trimmed = value.Trim();
            var separator = trimmed.LastIndexOf('.');
            return separator > 0 && separator < trimmed.Length - 1
                ? (NormalizeIdentifier(trimmed[..separator]), trimmed[(separator + 1)..])
                : (null, trimmed);
        }

        private static string? NormalizeIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().Trim('"', '[', ']', '`').Trim();
        }

        private static string? ReadOptionalString(JsonElement element, string propertyName)
            => TryGetJsonString(element, propertyName, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

        private static string? ReadOptionalString(
            IReadOnlyDictionary<string, JsonElement> options,
            string key)
            => options.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : null;
    }
}
