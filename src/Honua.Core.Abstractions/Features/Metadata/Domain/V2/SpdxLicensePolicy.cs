// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.Json;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Shared SPDX license-expression policy for canonical metadata and authored governance input.
/// </summary>
public static class SpdxLicensePolicy
{
    private const string SpdxIdentifierResourceName =
        "Honua.Core.Features.Metadata.Domain.V2.spdx-identifiers.json";
    private static readonly SpdxIdentifierCatalog SpdxIdentifiers = LoadSpdxIdentifiers();

    /// <summary>Maximum canonical SPDX expression length.</summary>
    public const int MaxExpressionLength = 256;

    /// <summary>Determines whether a value is a cataloged standalone SPDX license identifier.</summary>
    /// <param name="license">Candidate SPDX license identifier.</param>
    /// <returns><see langword="true"/> only for a cataloged standalone identifier.</returns>
    public static bool IsLicenseIdentifier(string? license)
        => !string.IsNullOrWhiteSpace(license) && SpdxIdentifiers.Licenses.Contains(license);

    /// <summary>Returns the canonical SPDX documentation URL for a standalone identifier.</summary>
    /// <param name="license">Candidate SPDX license identifier.</param>
    /// <returns>The canonical URL, or <see langword="null"/> for expressions and non-cataloged values.</returns>
    public static string? GetLicenseUrl(string? license)
        => IsLicenseIdentifier(license)
            ? $"https://spdx.org/licenses/{license}.html"
            : null;

    /// <summary>Validates SPDX expression syntax and identifier membership.</summary>
    /// <param name="expression">Candidate SPDX expression.</param>
    /// <returns><see langword="true"/> when the expression is valid.</returns>
    public static bool IsValidExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
        {
            return false;
        }

        var position = 0;
        return ParseOrExpression(expression, ref position) &&
            SkipWhitespace(expression, ref position) &&
            position == expression.Length;
    }

    /// <summary>
    /// Validates the normalized scalar form stored in canonical Metadata v2 object metadata.
    /// </summary>
    /// <param name="license">Canonical license scalar, or <see langword="null"/> when absent.</param>
    /// <returns><see langword="true"/> when the scalar is absent or canonical and valid.</returns>
    public static bool IsValidCanonicalValue(string? license)
        => license is null ||
           (license.Length > 0 &&
            license.Length <= MaxExpressionLength &&
            string.Equals(license, license.Trim(), StringComparison.Ordinal) &&
            !license.Any(char.IsControl) &&
            (string.Equals(license, "proprietary", StringComparison.Ordinal) || IsValidExpression(license)));

    private static bool ParseOrExpression(string expression, ref int position)
    {
        if (!ParseAndExpression(expression, ref position))
        {
            return false;
        }

        while (TryReadOperator(expression, ref position, "OR"))
        {
            if (!ParseAndExpression(expression, ref position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseAndExpression(string expression, ref int position)
    {
        if (!ParseWithExpression(expression, ref position))
        {
            return false;
        }

        while (TryReadOperator(expression, ref position, "AND"))
        {
            if (!ParseWithExpression(expression, ref position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseWithExpression(string expression, ref int position)
    {
        SkipWhitespace(expression, ref position);
        var isParenthesized = position < expression.Length && expression[position] == '(';
        if (!ParsePrimary(expression, ref position))
        {
            return false;
        }

        if (!isParenthesized && TryReadOperator(expression, ref position, "WITH"))
        {
            return TryReadIdentifier(expression, ref position, SpdxIdentifierRole.Addition);
        }

        return true;
    }

    private static bool ParsePrimary(string expression, ref int position)
    {
        SkipWhitespace(expression, ref position);
        if (position < expression.Length && expression[position] == '(')
        {
            position++;
            if (!ParseOrExpression(expression, ref position))
            {
                return false;
            }

            SkipWhitespace(expression, ref position);
            if (position >= expression.Length || expression[position] != ')')
            {
                return false;
            }

            position++;
            return true;
        }

        return TryReadIdentifier(expression, ref position, SpdxIdentifierRole.License);
    }

    private static bool TryReadIdentifier(
        string expression,
        ref int position,
        SpdxIdentifierRole role)
    {
        SkipWhitespace(expression, ref position);
        var start = position;
        while (position < expression.Length && IsIdentifierCharacter(expression[position]))
        {
            position++;
        }

        if (position == start)
        {
            return false;
        }

        var token = expression[start..position];
        return IsValidSpdxIdentifier(token, role) &&
            !string.Equals(token, "AND", StringComparison.Ordinal) &&
            !string.Equals(token, "OR", StringComparison.Ordinal) &&
            !string.Equals(token, "WITH", StringComparison.Ordinal);
    }

    private static bool IsValidSpdxIdentifier(
        ReadOnlySpan<char> token,
        SpdxIdentifierRole role)
    {
        var hasOrLaterSuffix = token.EndsWith("+", StringComparison.Ordinal);
        if (hasOrLaterSuffix)
        {
            token = token[..^1];
        }

        if (token.IsEmpty || token.Contains('+'))
        {
            return false;
        }

        var colon = token.IndexOf(':');
        if (colon < 0)
        {
            const string licenseReferencePrefix = "LicenseRef-";
            const string additionReferencePrefix = "AdditionRef-";
            if (token.StartsWith(licenseReferencePrefix, StringComparison.Ordinal))
            {
                return role == SpdxIdentifierRole.License &&
                    !hasOrLaterSuffix &&
                    IsValidSpdxIdString(token[licenseReferencePrefix.Length..]);
            }

            if (token.StartsWith(additionReferencePrefix, StringComparison.Ordinal))
            {
                return role == SpdxIdentifierRole.Addition &&
                    !hasOrLaterSuffix &&
                    IsValidSpdxIdString(token[additionReferencePrefix.Length..]);
            }

            if (!IsValidSpdxIdString(token))
            {
                return false;
            }

            var identifier = token.ToString();
            return role == SpdxIdentifierRole.License
                ? SpdxIdentifiers.Licenses.Contains(identifier)
                : !hasOrLaterSuffix && SpdxIdentifiers.Exceptions.Contains(identifier);
        }

        if (hasOrLaterSuffix || colon != token.LastIndexOf(':'))
        {
            return false;
        }

        const string documentReferencePrefix = "DocumentRef-";
        var documentReference = token[..colon];
        var referencedIdentifier = token[(colon + 1)..];
        if (!documentReference.StartsWith(documentReferencePrefix, StringComparison.Ordinal) ||
            !IsValidSpdxIdString(documentReference[documentReferencePrefix.Length..]))
        {
            return false;
        }

        var referencePrefix = role == SpdxIdentifierRole.License
            ? "LicenseRef-"
            : "AdditionRef-";
        return referencedIdentifier.StartsWith(referencePrefix, StringComparison.Ordinal) &&
            IsValidSpdxIdString(referencedIdentifier[referencePrefix.Length..]);
    }

    private static bool IsValidSpdxIdString(ReadOnlySpan<char> value)
    {
        var hasAlphaNumericCharacter = false;
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                hasAlphaNumericCharacter = true;
            }
            else if (character is not '-' and not '.')
            {
                return false;
            }
        }

        return hasAlphaNumericCharacter;
    }

    private static bool TryReadOperator(string expression, ref int position, string value)
    {
        var original = position;
        SkipWhitespace(expression, ref position);
        if (position == original ||
            !expression.AsSpan(position).StartsWith(value, StringComparison.Ordinal))
        {
            position = original;
            return false;
        }

        var end = position + value.Length;
        if (end < expression.Length && !char.IsWhiteSpace(expression[end]))
        {
            position = original;
            return false;
        }

        position = end;
        return true;
    }

    private static bool SkipWhitespace(string expression, ref int position)
    {
        while (position < expression.Length && char.IsWhiteSpace(expression[position]))
        {
            position++;
        }

        return true;
    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '-' or '.' or '+' or ':';

    private static SpdxIdentifierCatalog LoadSpdxIdentifiers()
    {
        var assembly = typeof(SpdxLicensePolicy).Assembly;
        using var stream = assembly.GetManifestResourceStream(SpdxIdentifierResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded SPDX identifier catalog '{SpdxIdentifierResourceName}' was not found.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return new SpdxIdentifierCatalog(
            ReadIdentifierSet(root, "licenses"),
            ReadIdentifierSet(root, "exceptions"));
    }

    private static FrozenSet<string> ReadIdentifierSet(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Embedded SPDX identifier catalog has no '{propertyName}' array.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identifier in values.EnumerateArray()
                     .Where(static value => value.ValueKind == JsonValueKind.String)
                     .Select(static value => value.GetString())
                     .OfType<string>()
                     .Where(static identifier => identifier.Length > 0))
        {
            identifiers.Add(identifier);
        }

        if (identifiers.Count == 0)
        {
            throw new InvalidOperationException(
                $"Embedded SPDX identifier catalog has an empty '{propertyName}' array.");
        }

        return identifiers.ToFrozenSet(StringComparer.Ordinal);
    }

    private sealed record SpdxIdentifierCatalog(
        FrozenSet<string> Licenses,
        FrozenSet<string> Exceptions);

    private enum SpdxIdentifierRole
    {
        License,
        Addition
    }
}
