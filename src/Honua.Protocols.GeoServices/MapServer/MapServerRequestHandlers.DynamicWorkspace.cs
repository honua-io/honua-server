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
    /// How a <c>joinTable</c> dynamic data source combines its left (primary) and right
    /// (secondary) sources. Mirrors the Esri <c>joinType</c> values.
    /// </summary>
    private enum DynamicLayerJoinType
    {
        /// <summary>Keep every left feature; right attributes are null when no match exists.</summary>
        LeftOuter,

        /// <summary>Keep only left features that have at least one matching right row.</summary>
        Inner,
    }

    /// <summary>
    /// Parsed <c>joinTable</c> data source. Both sides resolve to allowlisted, access-policy-gated
    /// published map layers; the join is materialized application-side (no raw SQL) by keying the
    /// right source on <see cref="RightKey"/> and merging its attributes onto each left feature.
    /// </summary>
    /// <param name="LeftMapLayerId">Resolved public layer id of the left (geometry/identity) source.</param>
    /// <param name="RightMapLayerId">Resolved public layer id of the right (attribute) source.</param>
    /// <param name="LeftKey">Left-source join key field name (validated as an identifier).</param>
    /// <param name="RightKey">Right-source join key field name (validated as an identifier).</param>
    /// <param name="JoinType">Left-outer or inner join semantics.</param>
    /// <param name="RightQualifier">Prefix applied to right-source attribute names (Esri <c>table.field</c> qualification).</param>
    private sealed record DynamicLayerJoinDefinition(
        int LeftMapLayerId,
        int RightMapLayerId,
        string LeftKey,
        string RightKey,
        DynamicLayerJoinType JoinType,
        string RightQualifier);

    /// <summary>
    /// A resolved dynamic-layer source: either a single published layer (<c>mapLayer</c>/plain
    /// <c>dataLayer</c>) or a <c>joinTable</c> that combines two published layers. The "anchor"
    /// layer (<see cref="MapLayerId"/>) is the layer that supplies geometry/identity and whose
    /// access policy and rendering the existing pipeline already enforces; <see cref="Join"/> is
    /// non-null only for join sources.
    /// </summary>
    private readonly record struct ResolvedDynamicSource(int MapLayerId, DynamicLayerJoinDefinition? Join)
    {
        public bool IsJoin => Join is not null;
    }

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

            // queryTable sources (raw SQL) and join sources remain deferred for the single-layer
            // resolver; reject explicitly so a client never silently falls through to a different
            // layer. joinTable is handled by TryResolveSource.
            error = $"{contextLabel} uses an unsupported source type '{sourceType}'. " +
                "Supported types are 'mapLayer' and 'dataLayer'.";
            return false;
        }

        /// <summary>
        /// Resolves a dynamic-layer <c>source</c> to either a single published layer or a
        /// <c>joinTable</c> that combines two published layers. A <c>dataLayer</c> source whose
        /// <c>dataSource.type</c> is <c>joinTable</c> is resolved into a structured join; every other
        /// supported source falls back to <see cref="TryResolveMapLayerId"/>. queryTable (raw SQL)
        /// remains deferred and is rejected.
        /// </summary>
        public bool TryResolveSource(
            JsonElement sourceElement,
            string contextLabel,
            out ResolvedDynamicSource resolved,
            out string? error)
        {
            resolved = default;
            error = null;

            if (TryGetJsonString(sourceElement, "type", out var sourceType) &&
                string.Equals(sourceType, "dataLayer", StringComparison.OrdinalIgnoreCase) &&
                sourceElement.TryGetProperty("dataSource", out var dataSource) &&
                dataSource.ValueKind == JsonValueKind.Object &&
                TryGetJsonString(dataSource, "type", out var dataSourceType) &&
                string.Equals(dataSourceType, "joinTable", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveJoinTable(dataSource, contextLabel, out var join, out error))
                {
                    return false;
                }

                resolved = new ResolvedDynamicSource(join!.LeftMapLayerId, join);
                return true;
            }

            if (!TryResolveMapLayerId(sourceElement, contextLabel, out var mapLayerId, out error))
            {
                return false;
            }

            resolved = new ResolvedDynamicSource(mapLayerId, Join: null);
            return true;
        }

        private bool TryResolveJoinTable(
            JsonElement dataSource,
            string contextLabel,
            out DynamicLayerJoinDefinition? join,
            out string? error)
        {
            join = null;
            error = null;

            if (!_options.WorkspaceLayersEnabled)
            {
                error = $"{contextLabel} uses a joinTable source, which is not enabled on this server.";
                return false;
            }

            if (!dataSource.TryGetProperty("leftTableSource", out var leftSource) ||
                leftSource.ValueKind != JsonValueKind.Object)
            {
                error = $"{contextLabel} joinTable dataSource must include a leftTableSource object.";
                return false;
            }

            if (!dataSource.TryGetProperty("rightTableSource", out var rightSource) ||
                rightSource.ValueKind != JsonValueKind.Object)
            {
                error = $"{contextLabel} joinTable dataSource must include a rightTableSource object.";
                return false;
            }

            // Both sides are themselves dynamic-layer sources. Resolve each through the same
            // allowlist/workspace gating used for top-level sources. Nested joins are rejected by
            // resolving each side as a single layer only (TryResolveMapLayerId), so a joinTable can
            // only combine two ordinary published layers — never another join or a raw query table.
            if (!TryResolveMapLayerId(leftSource, $"{contextLabel} joinTable leftTableSource", out var leftLayerId, out error))
            {
                return false;
            }

            if (!TryResolveMapLayerId(rightSource, $"{contextLabel} joinTable rightTableSource", out var rightLayerId, out error))
            {
                return false;
            }

            if (!TryReadJoinKey(dataSource, "leftTableKey", contextLabel, out var leftKey, out error) ||
                !TryReadJoinKey(dataSource, "rightTableKey", contextLabel, out var rightKey, out error))
            {
                return false;
            }

            if (!TryReadJoinType(dataSource, contextLabel, out var joinType, out error))
            {
                return false;
            }

            var rightQualifier = ResolveJoinRightQualifier(dataSource, rightLayerId);

            join = new DynamicLayerJoinDefinition(
                leftLayerId,
                rightLayerId,
                leftKey!,
                rightKey!,
                joinType,
                rightQualifier);
            return true;
        }

        private static bool TryReadJoinKey(
            JsonElement dataSource,
            string propertyName,
            string contextLabel,
            out string? key,
            out string? error)
        {
            key = null;
            error = null;

            var value = ReadOptionalString(dataSource, propertyName);
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{contextLabel} joinTable dataSource must include a {propertyName}.";
                return false;
            }

            // The join key is interpolated into an attribute lookup, never raw SQL, but reject
            // anything that is not a plain field identifier so a malformed key cannot widen the
            // surface area (e.g. a qualified or quoted name that bypasses attribute matching).
            if (!IsSafeFieldIdentifier(value))
            {
                error = $"{contextLabel} joinTable {propertyName} is not a valid field name.";
                return false;
            }

            key = value.Trim();
            return true;
        }

        private static bool TryReadJoinType(
            JsonElement dataSource,
            string contextLabel,
            out DynamicLayerJoinType joinType,
            out string? error)
        {
            joinType = DynamicLayerJoinType.LeftOuter;
            error = null;

            var raw = ReadOptionalString(dataSource, "joinType");
            if (string.IsNullOrWhiteSpace(raw))
            {
                // Esri defaults an unspecified joinType to a left outer join.
                return true;
            }

            switch (raw.Trim())
            {
                case "esriLeftOuterJoin":
                case "leftOuterJoin":
                case "left":
                    joinType = DynamicLayerJoinType.LeftOuter;
                    return true;
                case "esriLeftInnerJoin":
                case "innerJoin":
                case "inner":
                    joinType = DynamicLayerJoinType.Inner;
                    return true;
                default:
                    error = $"{contextLabel} joinTable joinType '{raw}' is not supported. " +
                        "Supported values are 'esriLeftOuterJoin' and 'esriLeftInnerJoin'.";
                    return false;
            }
        }

        private string ResolveJoinRightQualifier(JsonElement dataSource, int rightLayerId)
        {
            // Esri qualifies joined attributes as "<rightTableName>.<field>". Prefer an explicit
            // dataSourceName on the right source's dataSource; otherwise fall back to the right
            // layer's published name so qualified attribute names remain stable and collision-free.
            if (dataSource.TryGetProperty("rightTableSource", out var rightSource) &&
                rightSource.ValueKind == JsonValueKind.Object &&
                rightSource.TryGetProperty("dataSource", out var rightDataSource) &&
                rightDataSource.ValueKind == JsonValueKind.Object)
            {
                var name = ReadOptionalString(rightDataSource, "dataSourceName");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var (_, table) = SplitQualifiedName(name);
                    if (!string.IsNullOrWhiteSpace(table))
                    {
                        return table.Trim();
                    }
                }
            }

            foreach (var resourceName in (_candidates.Where(c => c.PublicLayerId == rightLayerId)).Select(candidate => candidate.Resource.Metadata.Name))
            {
                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    return resourceName.Trim();
                }

                break;
            }

            return rightLayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsSafeFieldIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 128)
            {
                return false;
            }

            if (trimmed.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_'))
            {
                return false;
            }

            return char.IsLetter(trimmed[0]) || trimmed[0] == '_';
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

            // This single-layer path only resolves plain table data sources. joinTable sources are
            // resolved earlier by TryResolveSource into a structured join (#1866); dynamic query
            // tables (raw SQL) remain deferred. Reject anything else here so a join/query table that
            // reaches this path (e.g. via a caller that does not use TryResolveSource) is not
            // silently treated as a plain table.
            if (TryGetJsonString(dataSource, "type", out var dataSourceType) &&
                !string.IsNullOrWhiteSpace(dataSourceType) &&
                !string.Equals(dataSourceType, "table", StringComparison.OrdinalIgnoreCase))
            {
                error = $"{contextLabel} dataLayer dataSource type '{dataSourceType}' is not supported here. " +
                    "Only 'table' data sources resolve to a single layer; query tables are not supported.";
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

            return _options.AllowedWorkspaceIds.Any(
                allowed => string.Equals(allowed, workspaceId, StringComparison.OrdinalIgnoreCase));
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
