// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Helpers;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Maximum number of distinct left-side join keys batched into a single right-side lookup.
    /// Bounds both the IN-list size and the memory held while materializing the join. Larger left
    /// result sets are handled by processing keys in successive batches.
    /// </summary>
    private const int DynamicJoinKeyBatchSize = 500;

    /// <summary>
    /// Right-side rows for a <c>joinTable</c>, indexed by their (string-normalized) join-key value.
    /// Built once per export/identify request by querying the allowlisted right layer with a
    /// parameter-safe key filter, then used to enrich left features without any further data access.
    /// </summary>
    private sealed class DynamicJoinLookup
    {
        private readonly Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> _rowsByKey;

        private DynamicJoinLookup(
            Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> rowsByKey)
        {
            _rowsByKey = rowsByKey;
        }

        /// <summary>True when the right source returned no rows for any requested key.</summary>
        public bool IsEmpty => _rowsByKey.Count == 0;

        public bool TryGetFirstMatch(object? leftKeyValue, out IReadOnlyDictionary<string, object?> row)
        {
            row = ImmutableDictionary<string, object?>.Empty;
            var normalized = NormalizeJoinKey(leftKeyValue);
            if (normalized is null)
            {
                return false;
            }

            if (_rowsByKey.TryGetValue(normalized, out var matches) && matches.Count > 0)
            {
                row = matches[0];
                return true;
            }

            return false;
        }

        public static DynamicJoinLookup Empty { get; } =
            new(new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal));

        public static async Task<DynamicJoinLookup> BuildAsync(
            IFeatureReader featureReader,
            int rightStorageLayerId,
            string rightKeyField,
            IReadOnlyCollection<object?> leftKeyValues,
            int maxRows,
            CancellationToken cancellationToken)
        {
            var distinctKeys = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in leftKeyValues)
            {
                var normalized = NormalizeJoinKey(value);
                if (normalized is null || !seen.Add(normalized))
                {
                    continue;
                }

                distinctKeys.Add(value!);
            }

            if (distinctKeys.Count == 0)
            {
                return Empty;
            }

            var rowsByKey = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
            var materialized = 0;

            for (var offset = 0; offset < distinctKeys.Count; offset += DynamicJoinKeyBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = distinctKeys
                    .Skip(offset)
                    .Take(DynamicJoinKeyBatchSize)
                    .ToArray();

                var sqlFilter = BuildKeyInFilter(rightKeyField, batch);
                var query = new FeatureQuery
                {
                    SqlFilter = sqlFilter,
                    Limit = maxRows,
                };

                var result = await featureReader
                    .QueryAsync(rightStorageLayerId, query, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var feature in result.Items)
                {
                    if (feature.Attributes is null)
                    {
                        continue;
                    }

                    if (!feature.Attributes.TryGetValue(rightKeyField, out var keyValue))
                    {
                        TryGetAttributeCaseInsensitive(feature.Attributes, rightKeyField, out keyValue);
                    }

                    var normalized = NormalizeJoinKey(keyValue);
                    if (normalized is null)
                    {
                        continue;
                    }

                    var attributes = new Dictionary<string, object?>(feature.Attributes.Count, StringComparer.Ordinal);
                    foreach (var kvp in feature.Attributes)
                    {
                        if (FeatureAttributeVisibility.IsInternalAttribute(kvp.Key))
                        {
                            continue;
                        }

                        attributes[kvp.Key] = kvp.Value;
                    }

                    if (!rowsByKey.TryGetValue(normalized, out var list))
                    {
                        list = new List<IReadOnlyDictionary<string, object?>>();
                        rowsByKey[normalized] = list;
                    }

                    ((List<IReadOnlyDictionary<string, object?>>)list).Add(attributes);
                    materialized++;
                    if (materialized >= maxRows)
                    {
                        break;
                    }
                }

                if (materialized >= maxRows)
                {
                    break;
                }
            }

            return new DynamicJoinLookup(rowsByKey);
        }

        /// <summary>
        /// Builds a parameter-safe <c>"field" IN (@p0, @p1, ...)</c> filter for the right source.
        /// The field name is a validated identifier (see <c>IsSafeFieldIdentifier</c>); every key
        /// value is bound as a parameter so no caller-controlled value reaches the SQL text.
        /// </summary>
        private static SqlFragment BuildKeyInFilter(string keyField, object[] keys)
        {
            var placeholders = new string[keys.Length];
            var parameters = new object?[keys.Length];
            for (var i = 0; i < keys.Length; i++)
            {
                placeholders[i] = "@p" + i.ToString(CultureInfo.InvariantCulture);
                parameters[i] = keys[i];
            }

            var sql = $"\"{keyField}\" IN ({string.Join(", ", placeholders)})";
            return new SqlFragment(sql, parameters);
        }
    }

    /// <summary>
    /// Normalizes a join-key value to a stable string used for equality matching across the left
    /// and right sources. Numeric values are compared by invariant decimal form so an integer left
    /// key matches a long/decimal right key; everything else uses the invariant string form.
    /// </summary>
    private static string? NormalizeJoinKey(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s.Length == 0 ? null : "s:" + s;
            case bool b:
                return "b:" + (b ? "1" : "0");
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return "n:" + Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
            case float or double or decimal:
                {
                    var d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    // Integral decimals normalize to the same form as integer keys so 1 == 1.0.
                    return d == decimal.Truncate(d)
                        ? "n:" + ((long)d).ToString(CultureInfo.InvariantCulture)
                        : "d:" + d.ToString(CultureInfo.InvariantCulture);
                }
            case System.Text.Json.JsonElement element:
                return NormalizeJoinKey(ConvertJoinKeyJsonElement(element));
            default:
                {
                    var text = value.ToString();
                    return string.IsNullOrEmpty(text) ? null : "s:" + text;
                }
        }
    }

    private static object? ConvertJoinKeyJsonElement(System.Text.Json.JsonElement element)
        => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString(),
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => null,
        };

    private static bool TryGetAttributeCaseInsensitive(
        IReadOnlyDictionary<string, object?> attributes,
        string field,
        out object? value)
    {
        foreach (var kvp in attributes)
        {
            if (string.Equals(kvp.Key, field, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Merges the right-source attributes of the first matching join row onto a copy of the left
    /// feature's attributes. Right attributes are qualified as <c>{qualifier}.{field}</c> to match
    /// Esri joined-field naming and to avoid clobbering left attributes. Returns <see langword="null"/>
    /// for an inner join when no right row matches (the left feature is dropped).
    /// </summary>
    private static Dictionary<string, object?>? ApplyDynamicJoinAttributes(
        IReadOnlyDictionary<string, object?> leftAttributes,
        string leftKeyField,
        DynamicLayerJoinDefinition join,
        DynamicJoinLookup lookup)
    {
        var merged = new Dictionary<string, object?>(leftAttributes.Count, StringComparer.Ordinal);
        foreach (var kvp in leftAttributes)
        {
            merged[kvp.Key] = kvp.Value;
        }

        if (!leftAttributes.TryGetValue(leftKeyField, out var keyValue))
        {
            TryGetAttributeCaseInsensitive(leftAttributes, leftKeyField, out keyValue);
        }

        if (lookup.TryGetFirstMatch(keyValue, out var rightRow))
        {
            foreach (var kvp in rightRow)
            {
                merged[$"{join.RightQualifier}.{kvp.Key}"] = kvp.Value;
            }

            return merged;
        }

        return join.JoinType == DynamicLayerJoinType.Inner ? null : merged;
    }
}
