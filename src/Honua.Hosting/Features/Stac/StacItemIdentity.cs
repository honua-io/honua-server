// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Infrastructure.Stac;

/// <summary>
/// Resolves the storage field that carries a public STAC item id.
/// A non-unique column merely named <c>id</c> is not an item identity.
/// </summary>
internal static class StacItemIdentity
{
    internal static readonly string[] CanonicalNames = ["stac_id", "item_id", "id"];

    private static readonly string[] UniqueRoles = ["id.primary", "id.global", "id.unique"];

    /// <summary>
    /// The schema field that stores the public item id, when one is declared.
    /// <see cref="StacItemIdSource.IsDeclaredIdentity"/> is false when the field is only
    /// the primary key used after no unique canonical column exists.
    /// </summary>
    internal readonly record struct StacItemIdSource(MetadataV2Field? Field, bool IsDeclaredIdentity);

    internal static StacItemIdSource Resolve(MetadataV2Resource? resource)
    {
        if (resource is null)
        {
            return new StacItemIdSource(null, false);
        }

        var explicitName = ReadExplicitIdField(resource);
        if (explicitName is not null)
        {
            var explicitField = FindSchemaField(resource, explicitName);
            if (explicitField is not null)
            {
                return new StacItemIdSource(explicitField, true);
            }
        }

        foreach (var name in CanonicalNames)
        {
            var field = FindSchemaField(resource, name);
            if (field is not null && HasUniqueRole(field))
            {
                return new StacItemIdSource(field, true);
            }
        }

        return new StacItemIdSource(FindPrimaryKeyField(resource), false);
    }

    internal static MetadataV2Field? FindSchemaField(MetadataV2Resource resource, string name)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    internal static bool IsNumericStorage(MetadataV2Field? field)
        => field is null || field.Type is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float;

    private static MetadataV2Field? FindPrimaryKeyField(MetadataV2Resource resource)
    {
        foreach (var field in resource.SchemaFields)
        {
            if (HasRole(field, "id.primary"))
            {
                return field;
            }
        }

        foreach (var field in resource.SchemaFields)
        {
            if (string.Equals(field.Name, "objectid", StringComparison.OrdinalIgnoreCase))
            {
                return field;
            }
        }

        return null;
    }

    private static bool HasUniqueRole(MetadataV2Field field)
    {
        foreach (var role in UniqueRoles)
        {
            if (HasRole(field, role))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRole(MetadataV2Field field, string role)
    {
        for (var i = 0; i < field.SemanticRoles.Count; i++)
        {
            if (string.Equals(field.SemanticRoles[i], role, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadExplicitIdField(MetadataV2Resource resource)
    {
        if (resource.Extensions is null ||
            !resource.Extensions.TryGetValue("stac", out var stac) ||
            stac.ValueKind != JsonValueKind.Object ||
            !stac.TryGetProperty("idField", out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
