// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Import.Domain;

// Server-owned staged imports use exactly these derived names. Only create-only staging may
// create them and only a staged discard may drop them, so no other import can claim one.
public static partial class StagedImportTableNames
{
    public const string Prefix = "staged_";

    public const int HashLength = 24;

    public static bool IsReservedLogical(string? table) => table is not null && LogicalPattern().IsMatch(table);

    public static bool IsReservedPhysical(string? table) => table is not null && PhysicalPattern().IsMatch(table);

    // The quantifier must equal HashLength; a regex attribute needs a literal.
    [GeneratedRegex("^staged_[0-9a-f]{24}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogicalPattern();

    [GeneratedRegex("^imported_staged_[0-9a-f]{24}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhysicalPattern();
}
