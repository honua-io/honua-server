// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Redshift.Features.FeatureStore.Services;

// Configured identifiers stay in the regular form, even though quoted Redshift names can be wider, so they cannot carry SQL.
internal static partial class RedshiftIdentifier
{
    private static readonly Regex _identifierRegex = IdentifierPattern();

    public static bool IsValid(string? value)
        => !string.IsNullOrWhiteSpace(value) && _identifierRegex.IsMatch(value);

    public static void EnsureValid(string value, string label)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"Invalid Redshift {label} '{value}'. Identifiers must match [A-Za-z_][A-Za-z0-9_]*.");
        }
    }

    public static string Quote(string identifier)
    {
        EnsureValid(identifier, "identifier");
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string QuoteAttribute(string identifier)
    {
        if (!FeatureFieldNameSyntax.IsValid(identifier))
        {
            throw new ArgumentException($"Invalid Redshift attribute column '{identifier}'.");
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
