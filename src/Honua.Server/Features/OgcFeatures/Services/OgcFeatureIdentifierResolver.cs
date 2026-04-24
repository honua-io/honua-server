// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.OgcFeatures.Services;

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
