// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Metadata.Domain.V2;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

// geospatial-grpc 1.2 global feature identity: hyphenated UUIDs without braces,
// case-insensitive on input and lowercase on output.
internal static partial class GrpcGlobalIds
{
    // IN values one filter expression may hold (Honua.Core FilterParserGuard.MaxInListSize).
    private const int MaxPredicateValues = 2_000;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainIdentifier();

    // Lowercase hyphenated form of a stored global ID; non-UUID values pass through.
    public static string Normalize(string value) => InsertGlobalIds.Normalize(value);

    // Selectors one bound predicate can carry: text columns match four spellings per ID.
    public static int MaxSelectorsPerPredicate(MetadataV2Resource resource, string field)
        => MaxPredicateValues / (IsUuidColumn(resource, field) ? 1 : 4);

    // One wire selector; throws ArgumentException (INVALID_ARGUMENT) for anything else.
    public static string ParseSelector(string value, string fieldName)
    {
        if (!Guid.TryParseExact(value, "D", out var guid))
        {
            throw new ArgumentException(
                $"{fieldName} must contain hyphenated UUIDs without braces; '{value}' is not one.");
        }

        return guid.ToString("D");
    }

    // Selectors in request order; duplicates are rejected so results stay aligned with requests.
    public static ImmutableArray<string> ParseDistinctSelectors(IEnumerable<string> values, string fieldName)
    {
        var parsed = values.Select(value => ParseSelector(value, fieldName)).ToImmutableArray();
        EnsureDistinct(parsed, fieldName);
        return parsed;
    }

    // With use_global_ids every update names one global ID and no integer update ID or deletes
    // appear; otherwise delete_global_ids is empty. No global ID may repeat across adds, or
    // across updates and deletes.
    public static void EnsureSelectorMode(Proto.ApplyEditsRequest request)
    {
        ParseDistinctSelectors(
            request.Adds.Where(static add => add.GlobalId.Length > 0).Select(static add => add.GlobalId),
            "add Feature.global_id");
        if (!request.UseGlobalIds)
        {
            if (request.DeleteGlobalIds.Count > 0)
            {
                throw new ArgumentException("delete_global_ids requires use_global_ids.");
            }

            return;
        }

        if (request.Deletes.Count > 0)
        {
            throw new ArgumentException("deletes must be empty when use_global_ids is set; use delete_global_ids.");
        }

        if (request.Updates.Any(static update => update.Id != 0 || update.GlobalId.Length == 0))
        {
            throw new ArgumentException("With use_global_ids every update supplies Feature.global_id and no Feature.id.");
        }

        ParseDistinctSelectors(
            request.Updates.Select(static update => update.GlobalId).Concat(request.DeleteGlobalIds),
            "update and delete global IDs");
    }

    // The layer's declared global-ID field, or null when it declares none.
    public static string? ResolveField(MetadataV2Resource resource)
    {
        var declared = resource.Editing?.GlobalIdField;
        return string.IsNullOrWhiteSpace(declared)
            ? null
            : resource.SchemaFields.FirstOrDefault(field => field.Name.Equals(declared, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    // A where predicate selecting ids. Every id is a parsed UUID and the field a plain identifier,
    // so the text needs no escaping; it is bound on its own and ANDed with the caller's filter.
    // Text columns may hold braced or uppercase forms, so those spellings are matched too.
    public static string Predicate(MetadataV2Resource resource, string field, ImmutableArray<string> ids)
    {
        if (!PlainIdentifier().IsMatch(field))
        {
            throw new ArgumentException($"Global-ID field '{field}' cannot be used as a selector.");
        }

        var spellings = IsUuidColumn(resource, field)
            ? ids
            : ids.SelectMany(static id => new[] { id, id.ToUpperInvariant(), $"{{{id}}}", $"{{{id.ToUpperInvariant()}}}" });
        return $"{field} IN ({string.Join(", ", spellings.Select(static id => $"'{id}'"))})";
    }

    // The stored global ID of a feature, normalised, when it is present.
    public static string? Read(IReadOnlyDictionary<string, object?> attributes, string? field)
    {
        if (field is null)
        {
            return null;
        }

        foreach (var (key, value) in attributes)
        {
            if (key.Equals(field, StringComparison.OrdinalIgnoreCase))
            {
                return value is Guid or string { Length: > 0 } ? InsertGlobalIds.Normalize(value) : null;
            }
        }

        return null;
    }

    private static bool IsUuidColumn(MetadataV2Resource resource, string field)
        => resource.SchemaFields
            .FirstOrDefault(candidate => candidate.Name.Equals(field, StringComparison.OrdinalIgnoreCase))?.Type
            is MetadataV2FieldType.Uuid;

    private static void EnsureDistinct(ImmutableArray<string> ids, string fieldName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw new ArgumentException($"{fieldName} repeats global ID {id}.");
            }
        }
    }
}
