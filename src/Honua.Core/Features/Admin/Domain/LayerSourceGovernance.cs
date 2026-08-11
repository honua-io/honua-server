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
    public const int MaxLicenseLength = SpdxLicensePolicy.MaxExpressionLength;

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
    /// Effective license documentation URL, including the canonical SPDX URL derived for a
    /// standalone cataloged license identifier when no explicit URL was authored.
    /// </summary>
    public string? EffectiveLicenseUrl => LicenseUrl ?? GetSpdxLicenseUrl(License);

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
            else if (!SpdxLicensePolicy.IsValidExpression(normalizedLicense))
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

    /// <summary>
    /// Determines whether a value is one standalone identifier from the embedded SPDX
    /// License List snapshot. Expressions, custom references, and exception identifiers
    /// are not standalone STAC license identifiers.
    /// </summary>
    /// <param name="license">Candidate SPDX license identifier.</param>
    /// <returns><see langword="true"/> only for a cataloged SPDX license identifier.</returns>
    public static bool IsSpdxLicenseIdentifier(string? license)
        => SpdxLicensePolicy.IsLicenseIdentifier(license);

    /// <summary>Returns the canonical SPDX documentation URL for a standalone license identifier.</summary>
    /// <param name="license">Candidate SPDX license identifier.</param>
    /// <returns>The canonical URL, or <see langword="null"/> for expressions and non-cataloged values.</returns>
    public static string? GetSpdxLicenseUrl(string? license)
        => IsSpdxLicenseIdentifier(license)
            ? $"https://spdx.org/licenses/{license}.html"
            : null;

    /// <summary>Builds canonical Metadata v2 license/source links for this value.</summary>
    /// <returns>Zero to two canonical external links.</returns>
    public IReadOnlyList<MetadataV2Link> ToMetadataLinks()
    {
        var links = new List<MetadataV2Link>(2);
        if (EffectiveLicenseUrl is { } effectiveLicenseUrl)
        {
            links.Add(new MetadataV2Link
            {
                Href = effectiveLicenseUrl,
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

}
