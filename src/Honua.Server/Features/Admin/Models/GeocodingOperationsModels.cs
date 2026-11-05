// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

internal sealed class GeocodingOperationsProvidersResponse
{
    public GeocodingOperationsProviderStatusResponse[] Providers { get; init; } = [];

    public string? DefaultProvider { get; init; }

    public bool FailoverEnabled { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }
}

internal sealed class GeocodingOperationsProviderStatusResponse
{
    public string? ProviderName { get; init; }

    public bool IsHealthy { get; init; }

    public string? ErrorMessage { get; init; }

    public DateTime? LastChecked { get; init; }

    public double? ResponseTimeMs { get; init; }

    public GeocodingOperationsCapabilitiesResponse? Capabilities { get; init; }
}

internal sealed class GeocodingOperationsCapabilitiesResponse
{
    public bool SupportsForwardGeocode { get; init; }

    public bool SupportsReverseGeocode { get; init; }

    public bool SupportsSuggest { get; init; }

    public bool SupportsBatch { get; init; }

    public bool SupportsStructuredInput { get; init; }

    public int MaxResultsPerRequest { get; init; }

    public int MaxBatchSize { get; init; }

    public int? RateLimitPerMinute { get; init; }

    public bool RequiresAuthentication { get; init; }
}

internal sealed class GeocodingConfigurationResponse
{
    public bool Enabled { get; init; }

    public string? DefaultProvider { get; init; }

    public bool EnableFailover { get; init; }

    public int MaxFailoverAttempts { get; init; }

    public bool EnableCaching { get; init; }

    public int CacheExpirationMinutes { get; init; }

    public int DefaultTimeoutSeconds { get; init; }
}
