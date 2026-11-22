// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Server.Features.Forms;

/// <summary>
/// A caller-supplied form id becomes a URL path segment on the admin and runtime form routes,
/// so it is limited to ASCII letters, digits, hyphens and underscores (1-128 characters).
/// An absent id is allowed: the store generates one.
/// </summary>
internal static partial class FormPackageIdentifier
{
    internal const int MaxLength = 128;

    // "import" is the sibling admin route POST /api/v1/admin/forms/import.
    private static readonly string[] ReservedSegments = ["import"];

    public static bool IsAcceptable(string? formId)
        => formId is null
            || (IdentifierPattern().IsMatch(formId)
                && !ReservedSegments.Contains(formId, StringComparer.OrdinalIgnoreCase));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
