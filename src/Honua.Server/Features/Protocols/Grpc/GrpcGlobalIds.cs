// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Grpc.Core;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Protocols.Grpc;

/// <summary>
/// geospatial-grpc 1.2 global feature identity (specification "Global feature identity"):
/// hyphenated UUIDs without braces, case-insensitive on input, lowercase on output.
/// </summary>
internal static partial class GrpcGlobalIds
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainIdentifier();

    /// <summary>Lowercase hyphenated form of a stored global ID; non-UUID values pass through.</summary>
    public static string Normalize(string value)
        => Guid.TryParse(value, out var guid) ? guid.ToString("D") : value;

    /// <summary>Parses one wire selector, failing the call with INVALID_ARGUMENT.</summary>
    public static string ParseSelector(string value, string fieldName)
    {
        if (!Guid.TryParseExact(value, "D", out var guid))
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{fieldName} must contain hyphenated UUIDs without braces; '{value}' is not one."));
        }

        return guid.ToString("D");
    }

    /// <summary>Parses selectors, keeping first-seen order and removing duplicates.</summary>
    public static ImmutableArray<string> ParseSelectors(IEnumerable<string> values, string fieldName)
        => values.Select(value => ParseSelector(value, fieldName)).Distinct(StringComparer.Ordinal).ToImmutableArray();

    /// <summary>
    /// Enforces the selector modes: with <c>use_global_ids</c> every update names a global ID and
    /// no integer update ID or <c>deletes</c> appears; otherwise <c>delete_global_ids</c> is empty.
    /// </summary>
    /// <exception cref="ArgumentException">The request mixes or misplaces selectors.</exception>
    public static void EnsureSelectorMode(Geospatial.V1.ApplyEditsRequest request)
    {
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
    }

    /// <summary>The layer's declared global-ID field, or <c>null</c> when it declares none.</summary>
    public static string? ResolveField(MetadataV2Resource resource)
    {
        var declared = resource.Editing?.GlobalIdField;
        return string.IsNullOrWhiteSpace(declared)
            ? null
            : resource.SchemaFields.FirstOrDefault(field => field.Name.Equals(declared, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    /// <summary>The declared global-ID field, failing with FAILED_PRECONDITION when absent.</summary>
    public static string RequireField(MetadataV2Resource resource)
    {
        var field = ResolveField(resource);
        if (field is null || !PlainIdentifier().IsMatch(field))
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "The layer does not declare a global-ID field, so global-ID selectors are unavailable."));
        }

        return field;
    }

    /// <summary>
    /// A where predicate selecting <paramref name="ids"/>, combined with the caller's where. Every
    /// ID is a parsed UUID and the field a plain identifier, so the text needs no escaping; the
    /// combined clause still goes through the normal binder and masked-field checks.
    /// </summary>
    public static string CombineWhere(string? where, string field, ImmutableArray<string> ids)
    {
        var predicate = $"{field} IN ({string.Join(", ", ids.Select(id => $"'{id}'"))})";
        return string.IsNullOrWhiteSpace(where) || where.Trim() == "1=1" ? predicate : $"({where}) AND {predicate}";
    }

    /// <summary>The stored global ID of a feature, normalised, when it is present.</summary>
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
                return value switch
                {
                    Guid guid => guid.ToString("D"),
                    string { Length: > 0 } text => Normalize(text),
                    _ => null,
                };
            }
        }

        return null;
    }
}
