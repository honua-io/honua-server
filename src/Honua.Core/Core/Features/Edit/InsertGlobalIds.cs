// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Edit;

// Insert-time global IDs shared by every edit protocol. Writers never generate them, so each
// inserted row stores the supplied ID in canonical lowercase form or a new one.
public static class InsertGlobalIds
{
    // The ID to store for an inserted row: the supplied one, normalised, or a new UUID.
    public static string Resolve(object? supplied)
        => supplied is null ? Guid.NewGuid().ToString("D") : Normalize(supplied);

    // Lowercase hyphenated form of a GUID value; other text passes through.
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
