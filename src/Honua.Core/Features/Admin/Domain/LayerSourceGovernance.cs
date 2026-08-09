// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Admin.Domain;

/// <summary>
/// Validated source-governance metadata authored for a published layer.
/// </summary>
public sealed record LayerSourceGovernance
{
    /// <summary>Canonical owner marker for links managed by the layer source-governance surface.</summary>
    public const string LinkManager = "layer-source-governance";

    /// <summary>Maximum SPDX expression length.</summary>
    public const int MaxLicenseLength = 256;

    /// <summary>Maximum attribution length.</summary>
    public const int MaxAttributionLength = 512;

    /// <summary>Maximum publisher length.</summary>
    public const int MaxPublisherLength = 256;

    /// <summary>Maximum documentation URL length.</summary>
    public const int MaxUrlLength = 2048;

    private LayerSourceGovernance(
        string? license,
        string? attribution,
        string? publisher,
        string? licenseUrl,
        string? sourceUrl)
    {
        License = license;
        Attribution = attribution;
        Publisher = publisher;
        LicenseUrl = licenseUrl;
        SourceUrl = sourceUrl;
    }

    /// <summary>SPDX license expression or the literal <c>proprietary</c>.</summary>
    public string? License { get; }

    /// <summary>Human-readable attribution that public protocol metadata must surface.</summary>
    public string? Attribution { get; }

    /// <summary>Data producer or source organization.</summary>
    public string? Publisher { get; }

    /// <summary>Absolute HTTP(S) URL for license documentation.</summary>
    public string? LicenseUrl { get; }

    /// <summary>Absolute HTTP(S) URL for source documentation.</summary>
    public string? SourceUrl { get; }

    /// <summary>
    /// Validates and normalizes optional source-governance input without deriving any rights or credits.
    /// </summary>
    /// <param name="license">SPDX expression or <c>proprietary</c>.</param>
    /// <param name="attribution">Attribution text.</param>
    /// <param name="publisher">Publisher text.</param>
    /// <param name="licenseUrl">License documentation URL.</param>
    /// <param name="sourceUrl">Source documentation URL.</param>
    /// <param name="governance">Validated governance, or null when every value is absent/empty.</param>
    /// <param name="error">Validation error when validation fails.</param>
    /// <returns>True when the supplied values are valid.</returns>
    public static bool TryCreate(
        string? license,
        string? attribution,
        string? publisher,
        string? licenseUrl,
        string? sourceUrl,
        out LayerSourceGovernance? governance,
        out string? error)
    {
        governance = null;
        error = null;

        if (!TryNormalizeText(license, MaxLicenseLength, "license", out var normalizedLicense, out error) ||
            !TryNormalizeText(attribution, MaxAttributionLength, "attribution", out var normalizedAttribution, out error) ||
            !TryNormalizeText(publisher, MaxPublisherLength, "publisher", out var normalizedPublisher, out error) ||
            !TryNormalizeUrl(licenseUrl, "licenseUrl", out var normalizedLicenseUrl, out error) ||
            !TryNormalizeUrl(sourceUrl, "sourceUrl", out var normalizedSourceUrl, out error))
        {
            return false;
        }

        if (normalizedLicense is not null)
        {
            if (string.Equals(normalizedLicense, "proprietary", StringComparison.OrdinalIgnoreCase))
            {
                normalizedLicense = "proprietary";
            }
            else if (!IsValidSpdxExpression(normalizedLicense))
            {
                error = "license must be a syntactically valid SPDX expression or the literal 'proprietary'.";
                return false;
            }
        }

        if (normalizedLicense is null &&
            normalizedAttribution is null &&
            normalizedPublisher is null &&
            normalizedLicenseUrl is null &&
            normalizedSourceUrl is null)
        {
            return true;
        }

        governance = new LayerSourceGovernance(
            normalizedLicense,
            normalizedAttribution,
            normalizedPublisher,
            normalizedLicenseUrl,
            normalizedSourceUrl);
        return true;
    }

    /// <summary>Builds canonical Metadata v2 license/source links for this value.</summary>
    /// <returns>Zero to two canonical external links.</returns>
    public IReadOnlyList<MetadataV2Link> ToMetadataLinks()
    {
        var links = new List<MetadataV2Link>(2);
        if (LicenseUrl is not null)
        {
            links.Add(new MetadataV2Link
            {
                Href = LicenseUrl,
                Rel = "license",
                Title = License,
                ManagedBy = LinkManager
            });
        }

        if (SourceUrl is not null)
        {
            links.Add(new MetadataV2Link
            {
                Href = SourceUrl,
                Rel = "describedby",
                Title = "Source documentation",
                ManagedBy = LinkManager
            });
        }

        return links;
    }

    private static bool TryNormalizeText(
        string? value,
        int maxLength,
        string fieldName,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (value is null)
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > maxLength)
        {
            error = $"{fieldName} must not exceed {maxLength} characters.";
            return false;
        }

        if (trimmed.Any(char.IsControl))
        {
            error = $"{fieldName} must not contain control characters.";
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static bool TryNormalizeUrl(
        string? value,
        string fieldName,
        out string? normalized,
        out string? error)
    {
        if (!TryNormalizeText(value, MaxUrlLength, fieldName, out normalized, out error) || normalized is null)
        {
            return error is null;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = $"{fieldName} must be an absolute HTTP(S) URL without embedded credentials.";
            normalized = null;
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }

    private static bool IsValidSpdxExpression(string expression)
    {
        var position = 0;
        return ParseOrExpression(expression, ref position) &&
            SkipWhitespace(expression, ref position) &&
            position == expression.Length;
    }

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
        if (!ParsePrimary(expression, ref position))
        {
            return false;
        }

        if (TryReadOperator(expression, ref position, "WITH"))
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

            return (role == SpdxIdentifierRole.License || !hasOrLaterSuffix) &&
                IsValidSpdxIdString(token);
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
        if (end < expression.Length && !char.IsWhiteSpace(expression[end]) && expression[end] != '(')
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

    private enum SpdxIdentifierRole
    {
        License,
        Addition
    }
}
