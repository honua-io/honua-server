// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Admin.Domain;

public sealed record LayerSourceGovernance
{
    public const string LinkManager = "layer-source-governance";

    public const int MaxLicenseLength = SpdxLicensePolicy.MaxExpressionLength;

    public const int MaxAttributionLength = MetadataV2ObjectMetadata.MaxAttributionLength;

    public const int MaxPublisherLength = MetadataV2ObjectMetadata.MaxPublisherLength;

    public const int MaxUrlLength = MetadataV2Link.MaxGovernanceHrefLength;

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

    public string? License { get; }

    public string? Attribution { get; }

    public string? Publisher { get; }

    public string? LicenseUrl { get; }

    public string? SourceUrl { get; }

    public string? EffectiveLicenseUrl => LicenseUrl ?? GetSpdxLicenseUrl(License);

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

    public static bool IsSpdxLicenseIdentifier(string? license)
        => SpdxLicensePolicy.IsLicenseIdentifier(license);

    public static string? GetSpdxLicenseUrl(string? license)
        => SpdxLicensePolicy.GetLicenseUrl(license);

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
        if (normalized.Length > MaxUrlLength)
        {
            error = $"{fieldName} must not exceed {MaxUrlLength} characters.";
            normalized = null;
            return false;
        }

        return true;
    }

}
