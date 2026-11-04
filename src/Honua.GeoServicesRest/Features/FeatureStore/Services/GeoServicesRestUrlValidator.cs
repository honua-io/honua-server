// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.GeoServicesRest.Features.FeatureStore.Services;

internal static class GeoServicesRestUrlValidator
{
    private const string InvalidServiceRootUrlMessage =
        "ArcGIS service URL must target a service root URL (FeatureServer or MapServer).";

    public static string NormalizeServiceRootUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var validation = OutboundHttpUrlValidator.ValidateConfiguration(url.Trim());
        if (!validation.IsValid || validation.Uri is null)
        {
            throw new ArgumentException(
                $"ArcGIS service URL {validation.ErrorMessage}",
                nameof(url));
        }

        var uri = validation.Uri;
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (!IsServiceRootUrl(normalized))
        {
            throw new ArgumentException(InvalidServiceRootUrlMessage, nameof(url));
        }

        return normalized;
    }

    private static bool IsServiceRootUrl(string normalizedUrl)
    {
        var segments = normalizedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var tail = segments[^1];
        return tail.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
            || tail.Equals("MapServer", StringComparison.OrdinalIgnoreCase);
    }
}
