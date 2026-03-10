// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Geocoding.Domain;

/// <summary>
/// Validator for geocoding configuration
/// </summary>
public sealed class GeocodingConfigurationValidator : OptionsValidator<GeocodingConfiguration>
{
    protected override void ValidateOptions(GeocodingConfiguration options, List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(failures);

        // Validate general settings
        if (string.IsNullOrWhiteSpace(options.DefaultProvider))
        {
            failures.Add("Geocoding:DefaultProvider is required.");
        }

        if (string.IsNullOrWhiteSpace(options.LocatorName))
        {
            failures.Add("Geocoding:LocatorName is required.");
        }

        if (options.DefaultSpatialReferenceWkid <= 0)
        {
            failures.Add("Geocoding:DefaultSpatialReferenceWkid must be greater than 0.");
        }

        if (options.DefaultMaxResults <= 0)
        {
            failures.Add("Geocoding:DefaultMaxResults must be greater than 0.");
        }

        if (options.DefaultTimeoutSeconds <= 0)
        {
            failures.Add("Geocoding:DefaultTimeoutSeconds must be greater than 0.");
        }

        if (options.MaxFailoverAttempts <= 0)
        {
            failures.Add("Geocoding:MaxFailoverAttempts must be greater than 0.");
        }

        if (options.CacheExpirationMinutes <= 0)
        {
            failures.Add("Geocoding:CacheExpirationMinutes must be greater than 0.");
        }

        // Validate provider configurations
        ValidateNominatimConfiguration(options.Providers.Nominatim, failures);
        ValidateAmazonLocationConfiguration(options.Providers.AmazonLocation, failures);
        ValidateAzureMapsConfiguration(options.Providers.AzureMaps, failures);
    }

    private static void ValidateNominatimConfiguration(NominatimProviderConfiguration config, List<string> failures)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            failures.Add("Geocoding:Providers:Nominatim:BaseUrl is required when provider is enabled.");
        }
        else
        {
            ValidateOutboundHttpUrl(config.BaseUrl, "Geocoding:Providers:Nominatim:BaseUrl", failures);
        }

        if (string.IsNullOrWhiteSpace(config.UserAgent))
        {
            failures.Add("Geocoding:Providers:Nominatim:UserAgent is required when provider is enabled.");
        }

        if (config.TimeoutSeconds <= 0)
        {
            failures.Add("Geocoding:Providers:Nominatim:TimeoutSeconds must be greater than 0.");
        }

        if (config.MaxResults <= 0)
        {
            failures.Add("Geocoding:Providers:Nominatim:MaxResults must be greater than 0.");
        }

        if (config.MaxSuggestions <= 0)
        {
            failures.Add("Geocoding:Providers:Nominatim:MaxSuggestions must be greater than 0.");
        }
    }

    private static void ValidateAmazonLocationConfiguration(AmazonLocationProviderConfiguration config, List<string> failures)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(config.Region))
        {
            failures.Add("Geocoding:Providers:AmazonLocation:Region is required when provider is enabled.");
        }

        if (string.IsNullOrWhiteSpace(config.PlaceIndexName))
        {
            failures.Add("Geocoding:Providers:AmazonLocation:PlaceIndexName is required when provider is enabled.");
        }

        if (!config.UseIamRole)
        {
            if (string.IsNullOrWhiteSpace(config.AccessKeyId))
            {
                failures.Add("Geocoding:Providers:AmazonLocation:AccessKeyId is required when UseIamRole is false.");
            }

            if (string.IsNullOrWhiteSpace(config.SecretAccessKey))
            {
                failures.Add("Geocoding:Providers:AmazonLocation:SecretAccessKey is required when UseIamRole is false.");
            }
        }
    }

    private static void ValidateAzureMapsConfiguration(AzureMapsProviderConfiguration config, List<string> failures)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(config.SubscriptionKey))
        {
            failures.Add("Geocoding:Providers:AzureMaps:SubscriptionKey is required when provider is enabled.");
        }

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            failures.Add("Geocoding:Providers:AzureMaps:BaseUrl is required when provider is enabled.");
        }
        else
        {
            ValidateOutboundHttpUrl(config.BaseUrl, "Geocoding:Providers:AzureMaps:BaseUrl", failures);
        }

        if (string.IsNullOrWhiteSpace(config.ApiVersion))
        {
            failures.Add("Geocoding:Providers:AzureMaps:ApiVersion is required when provider is enabled.");
        }
    }

}
