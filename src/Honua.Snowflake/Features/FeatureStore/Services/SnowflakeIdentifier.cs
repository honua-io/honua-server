// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Snowflake.Features.FeatureStore.Services;

// Quoted Snowflake identifiers are case-sensitive, while unquoted catalog objects are stored upper-case, so configured names must match the catalog exactly.
internal static partial class SnowflakeIdentifier
{
    private static readonly Regex _identifierRegex = IdentifierPattern();

    public static bool IsValid(string? value)
        => !string.IsNullOrWhiteSpace(value) && _identifierRegex.IsMatch(value);

    public static void EnsureValid(string value, string label)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Invalid Snowflake {label} '{value}'. Identifiers must match [A-Za-z_][A-Za-z0-9_]*.");
        }
    }

    public static string Quote(string identifier)
    {
        EnsureValid(identifier, "identifier");
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
