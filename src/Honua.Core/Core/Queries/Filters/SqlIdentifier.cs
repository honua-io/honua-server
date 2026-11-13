// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Queries.Filters;

/// <summary>Quotes SQL identifiers for dialects that use ANSI double quotes.</summary>
public static class SqlIdentifier
{
    /// <summary>
    /// Quotes one identifier, doubling embedded quotes. Callers retain responsibility
    /// for validating names and quoting each component of a qualified name separately.
    /// </summary>
    public static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
