// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Db.Databricks.Features.Infrastructure;

internal static partial class DatabricksIdentifier
{
    // `\z`, not `$`: .NET's `$` also matches immediately before a trailing newline, so
    // `"field\n"` would satisfy `...$` and reach SQL as an identifier with an embedded
    // newline. Found while fixing the same anchoring flaw in the Postgres field-name
    // validators (honua-server#3389).
    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleIdentifierRegex();

    public static string Quote(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            throw new ArgumentException("Identifier must not be empty.", nameof(identifier));
        }

        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }

    public static void ValidateIdentifier(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || !SimpleIdentifierRegex().IsMatch(fieldName))
        {
            throw new ArgumentException($"Invalid Databricks identifier: {fieldName}");
        }
    }

    public static void ValidateAttributeName(string fieldName)
    {
        if (!FeatureFieldNameSyntax.IsValid(fieldName))
        {
            throw new ArgumentException($"Invalid Databricks attribute name: {fieldName}");
        }
    }
}
