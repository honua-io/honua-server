// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public sealed class GeocodeProviderException : Exception
{
    public GeocodeProviderException(string message) : base(message)
    {
    }

    public GeocodeProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string? ProviderName { get; init; }

    public string? ErrorCode { get; init; }
}

public sealed class GeocodeRequestException : Exception
{
    public GeocodeRequestException(string message) : base(message)
    {
    }

    public GeocodeRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string? ParameterName { get; init; }
}

public sealed class GeocodeRateLimitException : Exception
{
    public GeocodeRateLimitException(string message) : base(message)
    {
    }

    public GeocodeRateLimitException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public TimeSpan? RetryAfter { get; init; }

    public string? ProviderName { get; init; }
}

public sealed class GeocodeAuthenticationException : Exception
{
    public GeocodeAuthenticationException(string message) : base(message)
    {
    }

    public GeocodeAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public string? ProviderName { get; init; }
}

public static class GeocodeErrorCodes
{
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";

    public const string InvalidConfiguration = "INVALID_CONFIGURATION";

    public const string InvalidRequest = "INVALID_REQUEST";

    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";

    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";

    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";

    public const string NetworkTimeout = "NETWORK_TIMEOUT";

    public const string UnsupportedSrs = "UNSUPPORTED_SRS";

    public const string NoResults = "NO_RESULTS";

    public const string AmbiguousQuery = "AMBIGUOUS_QUERY";
}
