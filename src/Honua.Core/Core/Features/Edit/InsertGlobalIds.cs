// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Edit;

/// <summary>
/// Insert-time global-ID assignment shared by every edit protocol. Writers never generate
/// global IDs, so each inserted row gets the supplied ID in canonical lowercase form or a new one.
/// </summary>
public static class InsertGlobalIds
{
    /// <summary>
    /// Stores the global ID for an inserted row under the declared field's canonical name and
    /// returns it: <paramref name="supplied"/> when given, otherwise a new UUID.
    /// </summary>
    public static string Assign(
        MetadataV2Resource resource,
        IDictionary<string, object?> attributes,
        string fieldName,
        object? supplied)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(attributes);

        var name = resource.SchemaFields
            .FirstOrDefault(field => field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))?.Name ?? fieldName;
        var id = supplied is null ? Guid.NewGuid().ToString("D") : Normalize(supplied);
        attributes[name] = id;
        return id;
    }

    /// <summary>Lowercase hyphenated form of a GUID value; other text passes through.</summary>
    public static string Normalize(object value)
    {
        if (value is Guid guid)
        {
            return guid.ToString("D");
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return Guid.TryParse(text, out var parsed) ? parsed.ToString("D") : text ?? Guid.NewGuid().ToString("D");
    }
}
