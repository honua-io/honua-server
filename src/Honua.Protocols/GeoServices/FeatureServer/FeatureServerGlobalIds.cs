// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal readonly record struct GlobalIdLookupContext(
    IFeatureReader Reader,
    IFilterExpressionService FilterExpressionService,
    MetadataV2Object Resource,
    MetadataV2Field Field,
    int StorageLayerId,
    VersionContext? VersionContext);

internal static class FeatureServerGlobalIds
{
    private const int MaxInListValues = 2_000;

    internal static MetadataV2Field? ResolveGlobalIdField(MetadataV2Object resource)
    {
        var declared = resource.Editing?.GlobalIdField;
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        return resource.SchemaFields.FirstOrDefault(field =>
            !field.Hidden &&
            field.Type is MetadataV2FieldType.Uuid or MetadataV2FieldType.String &&
            field.Name.Equals(declared, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryParseGlobalId(object? rawValue, out string normalized)
    {
        normalized = string.Empty;
        if (rawValue is Guid guid)
        {
            if (guid == Guid.Empty)
            {
                return false;
            }

            normalized = guid.ToString("D");
            return true;
        }

        var text = rawValue switch
        {
            string s => s.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text) || !Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        normalized = parsed.ToString("D");
        return true;
    }

    internal static bool TryGetGlobalId(
        IReadOnlyDictionary<string, object?>? attributes,
        string globalIdFieldName,
        out string normalized)
    {
        normalized = string.Empty;
        if (attributes is null || attributes.Count == 0)
        {
            return false;
        }

        foreach (var entry in attributes)
        {
            if (entry.Key.Equals(globalIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return TryParseGlobalId(entry.Value, out normalized);
            }
        }

        return false;
    }

    internal static bool TryGetRawGlobalId(
        IReadOnlyDictionary<string, object?>? attributes,
        string globalIdFieldName,
        out object? rawValue)
    {
        rawValue = null;
        if (attributes is null || attributes.Count == 0)
        {
            return false;
        }

        foreach (var entry in attributes)
        {
            if (entry.Key.Equals(globalIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                rawValue = entry.Value;
                return true;
            }
        }

        return false;
    }

    internal static string? ReadStoredGlobalId(IReadOnlyDictionary<string, object?> attributes, string? globalIdFieldName)
    {
        if (string.IsNullOrWhiteSpace(globalIdFieldName))
        {
            return null;
        }

        foreach (var entry in attributes)
        {
            if (entry.Key.Equals(globalIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value is Guid or string { Length: > 0 }
                    ? InsertGlobalIds.Normalize(entry.Value)
                    : null;
            }
        }

        return null;
    }

    internal static long ResolvePublicObjectId(Feature feature, MetadataV2Object resource)
    {
        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        foreach (var entry in feature.Attributes)
        {
            if (entry.Key.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
                FeatureServerValueParser.TryConvertToLong(entry.Value, out var publicObjectId) &&
                publicObjectId > 0)
            {
                return publicObjectId;
            }
        }

        return feature.Id;
    }

    internal static async Task<IReadOnlyDictionary<string, Feature>> ResolveFeaturesByGlobalIdsAsync(
        GlobalIdLookupContext lookup,
        IEnumerable<string> normalizedGlobalIds,
        CancellationToken cancellationToken)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in normalizedGlobalIds)
        {
            if (seen.Add(id))
            {
                distinct.Add(id);
            }
        }

        var resolved = new Dictionary<string, Feature>(distinct.Count, StringComparer.Ordinal);
        if (distinct.Count == 0)
        {
            return resolved;
        }

        var isUuidColumn = lookup.Field.Type == MetadataV2FieldType.Uuid;
        var chunkSize = isUuidColumn ? MaxInListValues : MaxInListValues / 4;
        var activeVersion = lookup.VersionContext is { IsDefault: false } ? lookup.VersionContext : null;

        foreach (var chunk in distinct.Chunk(chunkSize))
        {
            var literals = BuildInLiterals(chunk, isUuidColumn);
            var expression = new BinaryExpression(
                new PropertyReference(lookup.Field.Name),
                BinaryOperator.In,
                new ValueList(literals));
            var translation = lookup.FilterExpressionService.Translate(expression, lookup.Resource);
            if (!translation.IsSuccess)
            {
                throw new ArgumentException(translation.ErrorMessage ?? "Invalid GlobalID field.");
            }

            var result = await lookup.Reader.QueryAsync(
                lookup.StorageLayerId,
                new FeatureQuery
                {
                    SqlFilter = translation.SqlFilter,
                    Limit = chunk.Length,
                    VersionContext = activeVersion
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var feature in result.Items)
            {
                if (ReadStoredGlobalId(feature.Attributes, lookup.Field.Name) is { } storedId)
                {
                    resolved[storedId] = feature;
                }
            }
        }

        return resolved;
    }

    private static Literal[] BuildInLiterals(string[] chunk, bool isUuidColumn)
    {
        if (isUuidColumn)
        {
            var uuidLiterals = new Literal[chunk.Length];
            for (var i = 0; i < chunk.Length; i++)
            {
                uuidLiterals[i] = new Literal(chunk[i], LiteralType.Text);
            }

            return uuidLiterals;
        }

        var textLiterals = new Literal[chunk.Length * 4];
        for (var i = 0; i < chunk.Length; i++)
        {
            var lower = chunk[i];
            var upper = lower.ToUpperInvariant();
            var offset = i * 4;
            textLiterals[offset] = new Literal(lower, LiteralType.Text);
            textLiterals[offset + 1] = new Literal(upper, LiteralType.Text);
            textLiterals[offset + 2] = new Literal($"{{{lower}}}", LiteralType.Text);
            textLiterals[offset + 3] = new Literal($"{{{upper}}}", LiteralType.Text);
        }

        return textLiterals;
    }
}
