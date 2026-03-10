// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Geocoding;

/// <summary>
/// Validator for Esri geocoding configuration options
/// </summary>
internal sealed class EsriGeocodingOptionsValidator : IValidateOptions<EsriGeocodingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EsriGeocodingOptions options)
    {
        var failures = new List<string>();

        // Validate base URL
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add("BaseUrl is required.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            failures.Add("BaseUrl must be a valid absolute URL.");
        }
        else if (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp)
        {
            failures.Add("BaseUrl must use HTTP or HTTPS scheme.");
        }

        // Validate authentication configuration
        var hasApiKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        var hasOAuth = !string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ClientSecret);

        if (!hasApiKey && !hasOAuth)
        {
            failures.Add("Either ApiKey or both ClientId and ClientSecret must be provided for authentication.");
        }

        if (hasApiKey && hasOAuth)
        {
            failures.Add("Cannot configure both ApiKey and OAuth authentication. Choose one method.");
        }

        // Validate OAuth token endpoint if using OAuth
        if (hasOAuth)
        {
            if (string.IsNullOrWhiteSpace(options.TokenEndpoint))
            {
                failures.Add("TokenEndpoint is required when using OAuth authentication.");
            }
            else if (!Uri.TryCreate(options.TokenEndpoint, UriKind.Absolute, out var tokenUri))
            {
                failures.Add("TokenEndpoint must be a valid absolute URL.");
            }
            else if (tokenUri.Scheme != Uri.UriSchemeHttps && tokenUri.Scheme != Uri.UriSchemeHttp)
            {
                failures.Add("TokenEndpoint must use HTTP or HTTPS scheme.");
            }
        }

        // Validate timeout
        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("TimeoutSeconds must be greater than 0.");
        }
        else if (options.TimeoutSeconds > 300) // 5 minutes max
        {
            failures.Add("TimeoutSeconds cannot exceed 300 seconds (5 minutes).");
        }

        // Validate max results
        if (options.MaxResults <= 0)
        {
            failures.Add("MaxResults must be greater than 0.");
        }
        else if (options.MaxResults > 50) // Esri limit
        {
            failures.Add("MaxResults cannot exceed 50 (Esri API limit).");
        }

        // Validate batch size
        if (options.EnableBatchGeocoding)
        {
            if (options.MaxBatchSize <= 0)
            {
                failures.Add("MaxBatchSize must be greater than 0 when batch geocoding is enabled.");
            }
            else if (options.MaxBatchSize > 1000) // Esri limit
            {
                failures.Add("MaxBatchSize cannot exceed 1000 (Esri API limit).");
            }
        }

        // Validate spatial reference
        if (options.DefaultSpatialReference <= 0)
        {
            failures.Add("DefaultSpatialReference must be greater than 0.");
        }

        // Validate token cache duration
        if (hasOAuth)
        {
            if (options.TokenCacheDurationMinutes <= 0)
            {
                failures.Add("TokenCacheDurationMinutes must be greater than 0 when using OAuth.");
            }
            else if (options.TokenCacheDurationMinutes > 120) // 2 hours max
            {
                failures.Add("TokenCacheDurationMinutes cannot exceed 120 minutes (2 hours).");
            }
        }

        // Validate rate limiting
        if (options.RateLimitRequestsPerSecond.HasValue)
        {
            if (options.RateLimitRequestsPerSecond <= 0)
            {
                failures.Add("RateLimitRequestsPerSecond must be greater than 0 when specified.");
            }
            else if (options.RateLimitRequestsPerSecond > 100)
            {
                failures.Add("RateLimitRequestsPerSecond cannot exceed 100.");
            }
        }

        // Validate user agent
        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            failures.Add("UserAgent is required.");
        }

        // Validate priority
        if (options.Priority < 0)
        {
            failures.Add("Priority cannot be negative.");
        }

        // Validate out fields
        if (options.DefaultOutFields.Length == 0)
        {
            failures.Add("At least one DefaultOutField must be specified.");
        }

        // Validate custom locators
        foreach (var locator in options.CustomLocators)
        {
            if (string.IsNullOrWhiteSpace(locator.Key))
            {
                failures.Add("Custom locator names cannot be null or empty.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(locator.Value))
            {
                failures.Add($"Custom locator '{locator.Key}' URL cannot be null or empty.");
                continue;
            }

            if (!Uri.TryCreate(locator.Value, UriKind.Absolute, out var locatorUri))
            {
                failures.Add($"Custom locator '{locator.Key}' must have a valid absolute URL.");
                continue;
            }

            if (locatorUri.Scheme != Uri.UriSchemeHttps && locatorUri.Scheme != Uri.UriSchemeHttp)
            {
                failures.Add($"Custom locator '{locator.Key}' must use HTTP or HTTPS scheme.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}