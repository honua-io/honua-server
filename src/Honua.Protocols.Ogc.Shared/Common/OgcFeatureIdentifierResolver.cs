// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcFeatureIdentifierResolver
{
    /// <summary>
    /// Maximum number of values accepted in the <c>ids</c> query parameter, aligned
    /// with the 1000-operation batch transaction cap. Prevents arbitrarily large IN
    /// clauses/parameter lists from a single query string.
    /// </summary>
    private const int MaxIdsCount = 1000;

    private static readonly SqlFragment NoMatchingIdsFilter = new("FALSE", Array.Empty<object?>());

    private readonly record struct PublicIdField(string Name, PublicIdFieldType Type);

    private enum PublicIdFieldType
    {
        String,
        Integer,
        BigInteger,
        Double,
        Float,
        Boolean,
        DateTime,
        Date,
        Time,
        Json,
        Binary,
        Uuid,
        Geometry
    }

    internal readonly record struct ResolvedFeature(long ObjectId, Feature Feature);

    public static string? FormatPayloadId(object? payloadId)
    {
        var normalized = NormalizePublicId(payloadId);
        return normalized switch
        {
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(normalized, CultureInfo.InvariantCulture)
        };
    }

    public static object GetPublicId(Feature feature, MetadataV2Resource resource)
        => GetPublicId(feature.Id, feature.Attributes, resource);

    public static object GetPublicId(EncodedGeoJsonFeature feature, MetadataV2Resource resource)
        => GetPublicId(feature.Id, feature.Attributes, resource);

    public static object GetPublicId(long objectId, IReadOnlyDictionary<string, object?> attributes, MetadataV2Resource resource)
    {
        var objectIdFieldName = ResolveObjectIdFieldName(resource);

        if (TryGetAttributeValue(attributes, objectIdFieldName, out var configuredId) &&
            NormalizePublicId(configuredId) is { } normalizedConfiguredId)
        {
            return normalizedConfiguredId;
        }

        if (!objectIdFieldName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttributeValue(attributes, "id", out var idValue) &&
            NormalizePublicId(idValue) is { } normalizedId)
        {
            return normalizedId;
        }

        return objectId;
    }

    public static string FormatPublicId(Feature feature, MetadataV2Resource resource)
        => Convert.ToString(GetPublicId(feature, resource), CultureInfo.InvariantCulture)
           ?? feature.Id.ToString(CultureInfo.InvariantCulture);

    public static string FormatPublicId(EncodedGeoJsonFeature feature, MetadataV2Resource resource)
        => Convert.ToString(GetPublicId(feature, resource), CultureInfo.InvariantCulture)
           ?? feature.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves the public id field from the resource's primary id field, falling
    /// back to <c>id</c> for parity with legacy OGC feature-id behaviour.
    /// </summary>
    public static string? FormatPayloadPublicId(
        IReadOnlyDictionary<string, object?>? properties,
        MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (properties is null)
        {
            return null;
        }

        var objectIdFieldName = ResolveObjectIdFieldName(resource);
        if (TryGetAttributeValue(properties, objectIdFieldName, out var configuredId))
        {
            return FormatPayloadId(configuredId);
        }

        if (!objectIdFieldName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttributeValue(properties, "id", out var idValue))
        {
            return FormatPayloadId(idValue);
        }

        return null;
    }

    public static MetadataV2Field? ResolveWritablePublicIdField(MetadataV2Resource resource)
    {
        var objectIdFieldName = ResolveObjectIdFieldName(resource);
        var field = resource.SchemaFields.FirstOrDefault(f =>
                        f.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                    ?? resource.SchemaFields.FirstOrDefault(f =>
                        f.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

        return field is null ||
               IsGeometryField(field) ||
               field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase)
            ? null
            : field;
    }

    public static bool TryCreateIdsFilter(
        string? rawIds,
        MetadataV2Resource resource,
        IFilterExpressionTranslator filterExpressionTranslator,
        out ImmutableArray<long>? objectIds,
        out SqlFragment? sqlFilter,
        out string? error)
    {
        objectIds = null;
        sqlFilter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawIds))
        {
            return true;
        }

        if (HasEmptyCommaSeparatedToken(rawIds))
        {
            error = "Parameter 'ids' contains an empty ID value.";
            return false;
        }

        var tokens = rawIds.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "Parameter 'ids' must contain at least one ID value.";
            return false;
        }

        if (tokens.Length > MaxIdsCount)
        {
            error = $"Parameter 'ids' must not contain more than {MaxIdsCount} ID values.";
            return false;
        }

        var idField = ResolvePublicIdField(resource);
        if (CanUseObjectIdFastPath(idField, tokens, out var numericObjectIds))
        {
            objectIds = numericObjectIds;
            return true;
        }

        var expression = BuildPublicIdExpression(idField, tokens);
        if (expression == null)
        {
            sqlFilter = NoMatchingIdsFilter;
            return true;
        }

        sqlFilter = filterExpressionTranslator.Translate(expression, resource);
        return true;
    }

    public static async Task<ResolvedFeature?> ResolveAsync(
        IFeatureReader featureReader,
        IQueryProcessor queryProcessor,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        string featureId,
        CancellationToken cancellationToken)
    {
        var idField = ResolvePublicIdField(resource);
        // Mirror the OgcFeaturesQueryHandler / FeatureServer V2 ports: when the graph
        // carries no explicit storage binding, fall back to publication.LayerIndex.
        var storageLayerId = publication.LayerIndex
            ?? snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource);

        if (CanUseObjectIdFastPath(idField) &&
            TryParseCanonicalPositiveObjectId(featureId, out var objectId) &&
            storageLayerId is { } layerId)
        {
            var direct = await featureReader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
            if (direct.HasValue)
            {
                return new ResolvedFeature(objectId, direct.Value);
            }
        }

        var expression = BuildPublicIdExpression(idField, [featureId]);
        if (expression != null && storageLayerId is { } sLayerId)
        {
            var unifiedQuery = new UnifiedQuery
            {
                Filter = QueryFilter.FromExpression(expression),
                Limit = 1
            };
            var query = queryProcessor.ToFeatureQuery(unifiedQuery, resource);
            var result = await featureReader.QueryAsync(sLayerId, query, cancellationToken).ConfigureAwait(false);
            if (!result.Items.IsDefaultOrEmpty)
            {
                var feature = result.Items[0];
                return new ResolvedFeature(feature.Id, feature);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a set of public feature ids to their existing rows with at most two
    /// provider round trips (one <see cref="FeatureQuery.ObjectIds"/> fast-path query
    /// for canonical internal object ids plus one IN-filter query over the public id
    /// field), instead of one <see cref="ResolveAsync"/> lookup per id. Ids that do not
    /// resolve are simply absent from the result, so callers keep per-id error
    /// semantics. Public id field types whose values cannot be matched back to input
    /// tokens by invariant comparison (uuid, boolean, temporal, json, binary, geometry)
    /// fall back to per-id <see cref="ResolveAsync"/> calls with identical semantics.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ResolvedFeature>> ResolveManyAsync(
        IFeatureReader featureReader,
        IQueryProcessor queryProcessor,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Publication publication,
        MetadataV2Resource resource,
        IReadOnlyCollection<string> featureIds,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, ResolvedFeature>(featureIds.Count, StringComparer.Ordinal);
        if (featureIds.Count == 0)
        {
            return resolved;
        }

        var storageLayerId = publication.LayerIndex
            ?? snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource);
        if (storageLayerId is not { } layerId)
        {
            return resolved;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinctIds = featureIds
            .Where(featureId => !string.IsNullOrWhiteSpace(featureId) && seen.Add(featureId))
            .ToList();

        if (distinctIds.Count == 0)
        {
            return resolved;
        }

        var idField = ResolvePublicIdField(resource);
        if (!CanBatchResolvePublicIdType(idField.Type))
        {
            foreach (var featureId in distinctIds)
            {
                var single = await ResolveAsync(
                    featureReader,
                    queryProcessor,
                    snapshot,
                    publication,
                    resource,
                    featureId,
                    cancellationToken).ConfigureAwait(false);
                if (single.HasValue)
                {
                    resolved[featureId] = single.Value;
                }
            }

            return resolved;
        }

        // Fast path: internal object id field — resolve every canonical numeric id with
        // a single ObjectIds query (mirrors the per-id GetAsync fast path in ResolveAsync).
        if (CanUseObjectIdFastPath(idField))
        {
            var tokensByObjectId = new Dictionary<long, string>(distinctIds.Count);
            foreach (var featureId in distinctIds)
            {
                if (TryParseCanonicalPositiveObjectId(featureId, out var objectId))
                {
                    tokensByObjectId.TryAdd(objectId, featureId);
                }
            }

            if (tokensByObjectId.Count > 0)
            {
                var fastPathResult = await featureReader.QueryAsync(
                    layerId,
                    new FeatureQuery
                    {
                        ObjectIds = tokensByObjectId.Keys.ToImmutableArray(),
                        Limit = tokensByObjectId.Count
                    },
                    cancellationToken).ConfigureAwait(false);
                if (!fastPathResult.Items.IsDefaultOrEmpty)
                {
                    foreach (var feature in fastPathResult.Items)
                    {
                        if (tokensByObjectId.TryGetValue(feature.Id, out var token))
                        {
                            resolved.TryAdd(token, new ResolvedFeature(feature.Id, feature));
                        }
                    }
                }
            }
        }

        var pendingIds = distinctIds.FindAll(featureId => !resolved.ContainsKey(featureId));
        if (pendingIds.Count == 0)
        {
            return resolved;
        }

        // Single IN-filter query over the public id field for everything else, mirroring
        // the per-id expression path in ResolveAsync. Tokens whose literal cannot be
        // represented in the id field's type are unresolvable on the per-id path too,
        // so they are skipped instead of failing the rest of the batch.
        var tokensByKey = new Dictionary<string, string>(pendingIds.Count, StringComparer.Ordinal);
        var literals = new List<FilterExpression>(pendingIds.Count);
        foreach (var token in pendingIds)
        {
            var literal = CreateLiteral(idField, token);
            if (literal is null)
            {
                continue;
            }

            var key = NormalizeBatchKey(idField, literal.Value);
            if (key is not null && tokensByKey.TryAdd(key, token))
            {
                literals.Add(literal);
            }
        }

        if (literals.Count == 0)
        {
            return resolved;
        }

        var expression = new BinaryExpression(
            new PropertyReference(idField.Name),
            BinaryOperator.In,
            new ValueList(literals));
        var unifiedQuery = new UnifiedQuery
        {
            Filter = QueryFilter.FromExpression(expression),
            Limit = literals.Count
        };
        var query = queryProcessor.ToFeatureQuery(unifiedQuery, resource);
        var result = await featureReader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (result.Items.IsDefaultOrEmpty)
        {
            return resolved;
        }

        var useInternalObjectId = CanUseObjectIdFastPath(idField);
        foreach (var feature in result.Items)
        {
            string? key;
            if (useInternalObjectId)
            {
                key = feature.Id.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                TryGetAttributeValue(feature.Attributes, idField.Name, out var attributeValue);
                key = NormalizeBatchKey(idField, attributeValue);
            }

            if (key is not null && tokensByKey.TryGetValue(key, out var token))
            {
                // First match wins, matching the Limit=1 semantics of the per-id path.
                resolved.TryAdd(token, new ResolvedFeature(feature.Id, feature));
            }
        }

        return resolved;
    }

    private static bool CanBatchResolvePublicIdType(PublicIdFieldType type)
        => type is PublicIdFieldType.String
            or PublicIdFieldType.Integer
            or PublicIdFieldType.BigInteger
            or PublicIdFieldType.Double
            or PublicIdFieldType.Float;

    /// <summary>
    /// Normalizes a public id value (input token literal or attribute value read back
    /// from a feature row) to an invariant string key so batched IN-filter results can
    /// be matched back to the originating tokens (e.g. token <c>"007"</c> and attribute
    /// value <c>7L</c> both normalize to <c>"7"</c> for integer id fields).
    /// </summary>
    private static string? NormalizeBatchKey(PublicIdField idField, object? value)
    {
        var formatted = FormatPayloadId(value);
        if (formatted is null)
        {
            return null;
        }

        return idField.Type switch
        {
            PublicIdFieldType.Integer or PublicIdFieldType.BigInteger =>
                long.TryParse(formatted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                    ? longValue.ToString(CultureInfo.InvariantCulture)
                    : null,
            PublicIdFieldType.Double or PublicIdFieldType.Float =>
                double.TryParse(formatted, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
                    ? doubleValue.ToString("R", CultureInfo.InvariantCulture)
                    : null,
            _ => formatted
        };
    }

    private static string ResolveObjectIdFieldName(MetadataV2Resource resource)
        => resource.FindPrimaryIdField()?.Name ?? "objectid";

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    private static PublicIdField ResolvePublicIdField(MetadataV2Resource resource)
    {
        var objectIdFieldName = ResolveObjectIdFieldName(resource);
        var match = resource.SchemaFields.FirstOrDefault(f =>
                        f.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                    ?? resource.SchemaFields.FirstOrDefault(f =>
                        f.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return new PublicIdField(FieldNames.ObjectId, PublicIdFieldType.BigInteger);
        }

        return new PublicIdField(match.Name, MapV2PublicIdFieldType(match.Type));
    }

    private static PublicIdFieldType MapV2PublicIdFieldType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.String => PublicIdFieldType.String,
        MetadataV2FieldType.Integer => PublicIdFieldType.Integer,
        MetadataV2FieldType.BigInteger => PublicIdFieldType.BigInteger,
        MetadataV2FieldType.Double => PublicIdFieldType.Double,
        MetadataV2FieldType.Float => PublicIdFieldType.Float,
        MetadataV2FieldType.Boolean => PublicIdFieldType.Boolean,
        MetadataV2FieldType.DateTime => PublicIdFieldType.DateTime,
        MetadataV2FieldType.Date => PublicIdFieldType.Date,
        MetadataV2FieldType.Time => PublicIdFieldType.Time,
        MetadataV2FieldType.Json => PublicIdFieldType.Json,
        MetadataV2FieldType.Binary => PublicIdFieldType.Binary,
        MetadataV2FieldType.Uuid => PublicIdFieldType.Uuid,
        MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => PublicIdFieldType.Geometry,
        _ => PublicIdFieldType.String,
    };

    private static bool CanUseObjectIdFastPath(
        PublicIdField idField,
        string[] tokens,
        out ImmutableArray<long> objectIds)
    {
        objectIds = ImmutableArray<long>.Empty;
        if (!CanUseObjectIdFastPath(idField))
        {
            return false;
        }

        var ids = ImmutableArray.CreateBuilder<long>(tokens.Length);
        var seen = new HashSet<long>();
        foreach (var token in tokens)
        {
            if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return false;
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        objectIds = ids.ToImmutable();
        return true;
    }

    private static bool CanUseObjectIdFastPath(PublicIdField idField)
        => idField.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
           idField.Type is PublicIdFieldType.Integer or PublicIdFieldType.BigInteger;

    private static bool TryParseCanonicalPositiveObjectId(string value, out long objectId)
    {
        objectId = 0;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            return false;
        }

        var canonical = parsed.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(value, canonical, StringComparison.Ordinal))
        {
            return false;
        }

        objectId = parsed;
        return true;
    }

    private static BinaryExpression? BuildPublicIdExpression(PublicIdField idField, string[] tokens)
    {
        var values = new List<FilterExpression>(tokens.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (!seen.Add(token))
            {
                continue;
            }

            var literal = CreateLiteral(idField, token);
            if (literal == null)
            {
                return null;
            }

            values.Add(literal);
        }

        return new BinaryExpression(
            new PropertyReference(idField.Name),
            BinaryOperator.In,
            new ValueList(values));
    }

    private static Literal? CreateLiteral(PublicIdField idField, string token)
    {
        return idField.Type switch
        {
            PublicIdFieldType.Integer or PublicIdFieldType.BigInteger
                when long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue) =>
                    new Literal(longValue, LiteralType.Number),
            PublicIdFieldType.Integer or PublicIdFieldType.BigInteger => null,
            PublicIdFieldType.Double or PublicIdFieldType.Float
                when double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) =>
                    new Literal(doubleValue, LiteralType.Number),
            PublicIdFieldType.Double or PublicIdFieldType.Float => null,
            _ => new Literal(token, LiteralType.Text)
        };
    }

    private static bool TryGetAttributeValue(
        IReadOnlyDictionary<string, object?> attributes,
        string key,
        out object? value)
    {
        foreach (var (candidateKey, candidateValue) in attributes)
        {
            if (candidateKey.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static object? NormalizePublicId(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.Number } jsonElement when jsonElement.TryGetInt64(out var longValue) => longValue,
            JsonElement { ValueKind: JsonValueKind.Number } jsonElement => jsonElement.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } jsonElement => jsonElement.GetString(),
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            _ => value
        };

    private static bool HasEmptyCommaSeparatedToken(string value)
        => value.Split(',', StringSplitOptions.None).Any(token => token.Trim().Length == 0);
}
