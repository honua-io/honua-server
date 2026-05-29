// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.Geocoding.Domain;

/// <summary>
/// Validator for geocoding configuration
/// </summary>
public sealed class GeocodingConfigurationValidator : ConfigurationValidator<GeocodingConfiguration>
{
    protected override void PerformFeatureSpecificValidation(GeocodingConfiguration options, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        // Validate general settings
        if (string.IsNullOrWhiteSpace(options.DefaultProvider))
        {
            errors.Add("Geocoding:DefaultProvider is required.");
        }

        if (string.IsNullOrWhiteSpace(options.LocatorName))
        {
            errors.Add("Geocoding:LocatorName is required.");
        }

        if (options.DefaultSpatialReferenceWkid <= 0)
        {
            errors.Add("Geocoding:DefaultSpatialReferenceWkid must be greater than 0.");
        }

        if (options.DefaultMaxResults <= 0)
        {
            errors.Add("Geocoding:DefaultMaxResults must be greater than 0.");
        }

        if (options.DefaultTimeoutSeconds <= 0)
        {
            errors.Add("Geocoding:DefaultTimeoutSeconds must be greater than 0.");
        }

        if (options.MaxFailoverAttempts <= 0)
        {
            errors.Add("Geocoding:MaxFailoverAttempts must be greater than 0.");
        }

        if (options.CacheExpirationMinutes <= 0)
        {
            errors.Add("Geocoding:CacheExpirationMinutes must be greater than 0.");
        }

        // Validate provider configurations
        ValidateNominatimConfiguration(options.Providers.Nominatim, errors);
        ValidateAmazonLocationConfiguration(options.Providers.AmazonLocation, errors);
        ValidateAzureMapsConfiguration(options.Providers.AzureMaps, errors);
    }

    private static void ValidateNominatimConfiguration(NominatimProviderConfiguration config, List<string> errors)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            errors.Add("Geocoding:Providers:Nominatim:BaseUrl is required when provider is enabled.");
        }
        else
        {
            ValidateOutboundHttpUrl(config.BaseUrl, "Geocoding:Providers:Nominatim:BaseUrl", errors);
        }

        if (string.IsNullOrWhiteSpace(config.UserAgent))
        {
            errors.Add("Geocoding:Providers:Nominatim:UserAgent is required when provider is enabled.");
        }

        if (config.TimeoutSeconds <= 0)
        {
            errors.Add("Geocoding:Providers:Nominatim:TimeoutSeconds must be greater than 0.");
        }

        if (config.MaxResults <= 0)
        {
            errors.Add("Geocoding:Providers:Nominatim:MaxResults must be greater than 0.");
        }

        if (config.MaxSuggestions <= 0)
        {
            errors.Add("Geocoding:Providers:Nominatim:MaxSuggestions must be greater than 0.");
        }
    }

    private static void ValidateAmazonLocationConfiguration(AmazonLocationProviderConfiguration config, List<string> errors)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(config.Region))
        {
            errors.Add("Geocoding:Providers:AmazonLocation:Region is required when provider is enabled.");
        }

        if (string.IsNullOrWhiteSpace(config.PlaceIndexName))
        {
            errors.Add("Geocoding:Providers:AmazonLocation:PlaceIndexName is required when provider is enabled.");
        }

        if (!config.UseIamRole)
        {
            if (string.IsNullOrWhiteSpace(config.AccessKeyId))
            {
                errors.Add("Geocoding:Providers:AmazonLocation:AccessKeyId is required when UseIamRole is false.");
            }

            if (string.IsNullOrWhiteSpace(config.SecretAccessKey))
            {
                errors.Add("Geocoding:Providers:AmazonLocation:SecretAccessKey is required when UseIamRole is false.");
            }
        }
    }

    private static void ValidateAzureMapsConfiguration(AzureMapsProviderConfiguration config, List<string> errors)
    {
        if (!config.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(config.SubscriptionKey))
        {
            errors.Add("Geocoding:Providers:AzureMaps:SubscriptionKey is required when provider is enabled.");
        }

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            errors.Add("Geocoding:Providers:AzureMaps:BaseUrl is required when provider is enabled.");
        }
        else
        {
            ValidateOutboundHttpUrl(config.BaseUrl, "Geocoding:Providers:AzureMaps:BaseUrl", errors);
        }

        if (string.IsNullOrWhiteSpace(config.ApiVersion))
        {
            errors.Add("Geocoding:Providers:AzureMaps:ApiVersion is required when provider is enabled.");
        }
    }

}
