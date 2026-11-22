// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Ogc.Api.Records.Models;

namespace Honua.Protocols.Ogc.Api.Records;

// Environment custom fields as Records search terms: custom.<name>=a,b filters, facets and free text.
internal sealed class OgcRecordCustomFields
{
    private const string QueryPrefix = "custom.";

    private readonly MetadataV2CustomFieldSchema[] _schemas;
    private readonly Dictionary<string, HashSet<string>> _filters = new(StringComparer.Ordinal);

    private OgcRecordCustomFields(IReadOnlyList<MetadataV2CustomFieldSchema> schemas, IQueryCollection query)
    {
        _schemas = schemas.Where(static schema => !string.IsNullOrWhiteSpace(schema?.Name)).ToArray();
        ReadFilters(query);
    }

    public static OgcRecordCustomFields ForRequest(IReadOnlyList<MetadataV2CustomFieldSchema>? schemas, HttpRequest request) =>
        new(schemas ?? [], request.Query);

    public HashSet<string> AllowedQueryParameters(HashSet<string> standardParameters)
    {
        if (_schemas.Length == 0)
        {
            return standardParameters;
        }

        var allowed = new HashSet<string>(standardParameters, StringComparer.OrdinalIgnoreCase);
        allowed.UnionWith(_schemas.Select(static schema => QueryPrefix + schema.Name));
        return allowed;
    }

    private void ReadFilters(IQueryCollection query)
    {
        foreach (var schema in _schemas)
        {
            if (!query.TryGetValue(QueryPrefix + schema.Name, out var values))
            {
                continue;
            }

            var matched = values.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (matched.Count > 0)
            {
                _filters[schema.Name] = matched;
            }
        }
    }

    public bool Matches(IReadOnlyDictionary<string, JsonElement> customFields) =>
        _filters.All(filter => customFields.TryGetValue(filter.Key, out var element)
            && Values(element).Any(filter.Value.Contains));

    public Dictionary<string, ImmutableArray<OgcRecordFacetBucket>>? Facets(
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> matchedFields)
    {
        var facetable = _schemas.Where(static schema => schema.Facetable).ToArray();
        if (facetable.Length == 0)
        {
            return null;
        }

        return facetable.ToDictionary(
            static schema => schema.Name,
            schema => Buckets(matchedFields, schema.Name),
            StringComparer.Ordinal);
    }

    public static IEnumerable<string> SearchValues(IReadOnlyDictionary<string, JsonElement> customFields) =>
        customFields.Values.SelectMany(Values);

    private static ImmutableArray<OgcRecordFacetBucket> Buckets(
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> matchedFields,
        string name) =>
        matchedFields
            .Where(fields => fields.ContainsKey(name))
            .SelectMany(fields => Values(fields[name]).Distinct(StringComparer.Ordinal))
            .GroupBy(static value => value, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new OgcRecordFacetBucket { Value = group.Key, Count = group.Count() })
            .ToImmutableArray();

    private static IEnumerable<string> Values(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String when element.GetString() is { Length: > 0 } text => [text],
        JsonValueKind.Number => [element.GetRawText()],
        JsonValueKind.True => ["true"],
        JsonValueKind.False => ["false"],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Values),
        _ => [],
    };
}
