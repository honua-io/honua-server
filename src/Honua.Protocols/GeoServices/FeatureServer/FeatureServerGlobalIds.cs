// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal sealed class GlobalIdLookupContext
{
    public required IFeatureReader Reader { get; init; }
    public required IFilterExpressionService FilterExpressionService { get; init; }
    public required MetadataV2Object Resource { get; init; }
    public required MetadataV2Field Field { get; init; }
    public required int StorageLayerId { get; init; }
    public VersionContext? VersionContext { get; init; }
}

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

    internal static string? ParseGlobalId(object? rawValue)
    {
        if (rawValue is Guid guid)
        {
            return guid == Guid.Empty ? null : guid.ToString("D");
        }

        var text = rawValue switch
        {
            string s => s.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text) || !Guid.TryParse(text, out var parsed) || parsed == Guid.Empty)
        {
            return null;
        }

        return parsed.ToString("D");
    }

    internal static (bool Found, object? Value) FindRawGlobalId(
        IReadOnlyDictionary<string, object?>? attributes,
        string globalIdFieldName)
    {
        if (attributes is null || attributes.Count == 0)
        {
            return (false, null);
        }

        foreach (var entry in attributes)
        {
            if (entry.Key.Equals(globalIdFieldName, StringComparison.OrdinalIgnoreCase))
            {
                return (true, entry.Value);
            }
        }

        return (false, null);
    }

    internal static void ValidateClientInsertGlobalId(
        MetadataV2Field? globalIdField,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        if (globalIdField is null)
        {
            return;
        }

        var (found, rawValue) = FindRawGlobalId(attributes, globalIdField.Name);
        if (!found || IsOmittedGlobalId(rawValue))
        {
            return;
        }

        if (ParseGlobalId(rawValue) is null)
        {
            throw new ArgumentException($"Field '{globalIdField.Name}' must be a valid UUID.");
        }
    }

    private static bool IsOmittedGlobalId(object? rawValue)
        => rawValue is null
            || rawValue is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

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
        var distinct = CollectDistinctIds(normalizedGlobalIds);
        var resolved = new Dictionary<string, Feature>(distinct.Count, StringComparer.Ordinal);
        if (distinct.Count == 0)
        {
            return resolved;
        }

        var isUuidColumn = lookup.Field.Type == MetadataV2FieldType.Uuid;
        var chunkSize = isUuidColumn ? MaxInListValues : MaxInListValues / 4;
        foreach (var chunk in distinct.Chunk(chunkSize))
        {
            var result = await QueryChunkAsync(lookup, chunk, cancellationToken).ConfigureAwait(false);
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

    private static List<string> CollectDistinctIds(IEnumerable<string> normalizedGlobalIds)
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

        return distinct;
    }

    private static async Task<QueryResult<Feature>> QueryChunkAsync(
        GlobalIdLookupContext lookup,
        string[] chunk,
        CancellationToken cancellationToken)
    {
        var literals = lookup.Field.Type == MetadataV2FieldType.Uuid
            ? BuildUuidInLiterals(chunk)
            : BuildTextInLiterals(chunk);
        var expression = new BinaryExpression(
            new PropertyReference(lookup.Field.Name),
            BinaryOperator.In,
            new ValueList(literals));
        var translation = lookup.FilterExpressionService.Translate(expression, lookup.Resource);
        if (!translation.IsSuccess)
        {
            throw new ArgumentException(translation.ErrorMessage ?? "Invalid GlobalID field.");
        }

        var activeVersion = lookup.VersionContext is { IsDefault: false } ? lookup.VersionContext : null;
        return await lookup.Reader.QueryAsync(
            lookup.StorageLayerId,
            new FeatureQuery
            {
                SqlFilter = translation.SqlFilter,
                Limit = chunk.Length,
                VersionContext = activeVersion
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static Literal[] BuildUuidInLiterals(string[] chunk)
    {
        var literals = new Literal[chunk.Length];
        for (var i = 0; i < chunk.Length; i++)
        {
            literals[i] = new Literal(chunk[i], LiteralType.Text);
        }

        return literals;
    }

    private static Literal[] BuildTextInLiterals(string[] chunk)
    {
        var literals = new Literal[chunk.Length * 4];
        for (var i = 0; i < chunk.Length; i++)
        {
            var lower = chunk[i];
            var upper = lower.ToUpperInvariant();
            var offset = i * 4;
            literals[offset] = new Literal(lower, LiteralType.Text);
            literals[offset + 1] = new Literal(upper, LiteralType.Text);
            literals[offset + 2] = new Literal($"{{{lower}}}", LiteralType.Text);
            literals[offset + 3] = new Literal($"{{{upper}}}", LiteralType.Text);
        }

        return literals;
    }
}
