// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeatureIdentifierResolver
{
    private static readonly SqlFragment NoMatchingIdsFilter = new("FALSE", Array.Empty<object?>());

    internal readonly record struct ResolvedFeature(long ObjectId, Feature Feature);

    public static object GetPublicId(Feature feature, LayerDefinition layer)
        => GetPublicId(feature.Id, feature.Attributes, layer);

    public static object GetPublicId(EncodedGeoJsonFeature feature, LayerDefinition layer)
        => GetPublicId(feature.Id, feature.Attributes, layer);

    public static object GetPublicId(long objectId, IReadOnlyDictionary<string, object?> attributes, LayerDefinition layer)
    {
        if (TryGetAttributeValue(attributes, layer.ObjectIdFieldName, out var configuredId) &&
            NormalizePublicId(configuredId) is { } normalizedConfiguredId)
        {
            return normalizedConfiguredId;
        }

        if (!layer.ObjectIdFieldName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttributeValue(attributes, "id", out var idValue) &&
            NormalizePublicId(idValue) is { } normalizedId)
        {
            return normalizedId;
        }

        return objectId;
    }

    public static string FormatPublicId(Feature feature, LayerDefinition layer)
        => Convert.ToString(GetPublicId(feature, layer), CultureInfo.InvariantCulture)
           ?? feature.Id.ToString(CultureInfo.InvariantCulture);

    public static string FormatPublicId(EncodedGeoJsonFeature feature, LayerDefinition layer)
        => Convert.ToString(GetPublicId(feature, layer), CultureInfo.InvariantCulture)
           ?? feature.Id.ToString(CultureInfo.InvariantCulture);

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

    public static string? FormatPayloadPublicId(
        IReadOnlyDictionary<string, object?>? properties,
        LayerDefinition layer)
    {
        if (properties is null)
        {
            return null;
        }

        if (TryGetAttributeValue(properties, layer.ObjectIdFieldName, out var configuredId))
        {
            return FormatPayloadId(configuredId);
        }

        if (!layer.ObjectIdFieldName.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttributeValue(properties, "id", out var idValue))
        {
            return FormatPayloadId(idValue);
        }

        return null;
    }

    public static FieldDefinition? ResolveWritablePublicIdField(LayerDefinition layer)
    {
        var field = layer.Fields.FirstOrDefault(field =>
                        field.Name.Equals(layer.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase))
                    ?? layer.Fields.FirstOrDefault(field =>
                        field.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

        return field is null ||
               field.IsGeometry ||
               field.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase)
            ? null
            : field;
    }

    public static bool TryCreateIdsFilter(
        string? rawIds,
        LayerDefinition layer,
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

        var idField = ResolvePublicIdField(layer);
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

        sqlFilter = filterExpressionTranslator.Translate(expression, layer);
        return true;
    }

    public static async Task<ResolvedFeature?> ResolveAsync(
        IFeatureReader featureReader,
        IQueryProcessor queryProcessor,
        LayerDefinition layer,
        string featureId,
        CancellationToken cancellationToken)
    {
        var idField = ResolvePublicIdField(layer);
        if (CanUseObjectIdFastPath(idField) &&
            TryParseCanonicalPositiveObjectId(featureId, out var objectId))
        {
            var direct = await featureReader.GetAsync(layer.Id, objectId, cancellationToken).ConfigureAwait(false);
            if (direct.HasValue)
            {
                return new ResolvedFeature(objectId, direct.Value);
            }
        }

        var expression = BuildPublicIdExpression(idField, [featureId]);
        if (expression != null)
        {
            var unifiedQuery = new UnifiedQuery
            {
                Filter = QueryFilter.FromExpression(expression),
                Limit = 1
            };
            var query = queryProcessor.ToFeatureQuery(unifiedQuery, layer);
            var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken).ConfigureAwait(false);
            if (!result.Items.IsDefaultOrEmpty)
            {
                var feature = result.Items[0];
                return new ResolvedFeature(feature.Id, feature);
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Metadata v2 overloads
    // -----------------------------------------------------------------------

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

    private static string ResolveObjectIdFieldName(MetadataV2Resource resource)
        => resource.FindPrimaryIdField()?.Name ?? "objectid";

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    private static FieldDefinition ResolvePublicIdField(MetadataV2Resource resource)
    {
        var objectIdFieldName = ResolveObjectIdFieldName(resource);
        var match = resource.SchemaFields.FirstOrDefault(f =>
                        f.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase))
                    ?? resource.SchemaFields.FirstOrDefault(f =>
                        f.Name.Equals("id", StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return new FieldDefinition(FieldNames.ObjectId, FieldType.BigInteger, Nullable: false);
        }

        return new FieldDefinition(match.Name, MapV2FieldType(match.Type), Nullable: match.Nullable);
    }

    private static FieldType MapV2FieldType(MetadataV2FieldType type) => type switch
    {
        MetadataV2FieldType.String => FieldType.String,
        MetadataV2FieldType.Integer => FieldType.Integer,
        MetadataV2FieldType.BigInteger => FieldType.BigInteger,
        MetadataV2FieldType.Double => FieldType.Double,
        MetadataV2FieldType.Float => FieldType.Float,
        MetadataV2FieldType.Boolean => FieldType.Boolean,
        MetadataV2FieldType.DateTime => FieldType.DateTime,
        MetadataV2FieldType.Date => FieldType.Date,
        MetadataV2FieldType.Time => FieldType.Time,
        MetadataV2FieldType.Json => FieldType.Json,
        MetadataV2FieldType.Binary => FieldType.Binary,
        MetadataV2FieldType.Uuid => FieldType.Uuid,
        MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => FieldType.Geometry,
        _ => FieldType.String,
    };

    private static FieldDefinition ResolvePublicIdField(LayerDefinition layer)
        => layer.Fields.FirstOrDefault(field => field.Name.Equals(layer.ObjectIdFieldName, StringComparison.OrdinalIgnoreCase))
           ?? layer.Fields.FirstOrDefault(field => field.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
           ?? new FieldDefinition(FieldNames.ObjectId, FieldType.BigInteger, Nullable: false);

    private static bool CanUseObjectIdFastPath(
        FieldDefinition idField,
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

    private static bool CanUseObjectIdFastPath(FieldDefinition idField)
        => idField.Name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase) &&
           idField.Type is FieldType.Integer or FieldType.BigInteger;

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

    private static BinaryExpression? BuildPublicIdExpression(FieldDefinition idField, string[] tokens)
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

    private static Literal? CreateLiteral(FieldDefinition idField, string token)
    {
        return idField.Type switch
        {
            FieldType.Integer or FieldType.BigInteger
                when long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue) =>
                    new Literal(longValue, LiteralType.Number),
            FieldType.Integer or FieldType.BigInteger => null,
            FieldType.Double or FieldType.Float
                when double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) =>
                    new Literal(doubleValue, LiteralType.Number),
            FieldType.Double or FieldType.Float => null,
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
    {
        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            if (token.Trim().Length == 0)
            {
                return true;
            }
        }

        return false;
    }
}
