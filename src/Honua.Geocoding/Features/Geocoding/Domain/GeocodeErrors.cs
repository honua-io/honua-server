// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Exception thrown when a geocoding provider is not available or misconfigured
/// </summary>
public sealed class GeocodeProviderException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeProviderException"/> class with a message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public GeocodeProviderException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeProviderException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public GeocodeProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Name of the provider that caused the error
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Error code for specific error types
    /// </summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Exception thrown when a geocoding request is invalid
/// </summary>
public sealed class GeocodeRequestException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeRequestException"/> class with a message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public GeocodeRequestException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeRequestException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public GeocodeRequestException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Name of the invalid parameter
    /// </summary>
    public string? ParameterName { get; init; }
}

/// <summary>
/// Exception thrown when a geocoding provider's rate limit is exceeded
/// </summary>
public sealed class GeocodeRateLimitException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeRateLimitException"/> class with a message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public GeocodeRateLimitException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeRateLimitException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public GeocodeRateLimitException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Time to wait before retrying the request
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Provider that enforced the rate limit
    /// </summary>
    public string? ProviderName { get; init; }
}

/// <summary>
/// Exception thrown when authentication fails for a geocoding provider
/// </summary>
public sealed class GeocodeAuthenticationException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeAuthenticationException"/> class with a message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public GeocodeAuthenticationException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GeocodeAuthenticationException"/> class with a message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public GeocodeAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Provider that failed authentication
    /// </summary>
    public string? ProviderName { get; init; }
}

/// <summary>
/// Error codes for geocoding operations
/// </summary>
public static class GeocodeErrorCodes
{
    /// <summary>
    /// Provider is not configured or unavailable
    /// </summary>
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";

    /// <summary>
    /// Provider configuration is invalid
    /// </summary>
    public const string InvalidConfiguration = "INVALID_CONFIGURATION";

    /// <summary>
    /// Request parameters are invalid
    /// </summary>
    public const string InvalidRequest = "INVALID_REQUEST";

    /// <summary>
    /// Rate limit exceeded
    /// </summary>
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";

    /// <summary>
    /// Authentication failed
    /// </summary>
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";

    /// <summary>
    /// Service temporarily unavailable
    /// </summary>
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";

    /// <summary>
    /// Network timeout occurred
    /// </summary>
    public const string NetworkTimeout = "NETWORK_TIMEOUT";

    /// <summary>
    /// Unsupported spatial reference system
    /// </summary>
    public const string UnsupportedSrs = "UNSUPPORTED_SRS";

    /// <summary>
    /// No results found
    /// </summary>
    public const string NoResults = "NO_RESULTS";

    /// <summary>
    /// Query too broad or ambiguous
    /// </summary>
    public const string AmbiguousQuery = "AMBIGUOUS_QUERY";
}
